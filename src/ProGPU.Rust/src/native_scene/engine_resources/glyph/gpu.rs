//! Native actual atlas capture: unchanged compute/binary/fragment kernels.
use super::*;
use crate::native_scene::engine_resources::path::gpu::{
    self as common, NativeRasterResourceMetrics,
};
use std::num::NonZeroU64;
#[derive(Clone)]
struct Compute {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::ComputePipeline,
}
#[derive(Clone)]
struct Binary {
    layout: wgpu::BindGroupLayout,
    horizontal: wgpu::ComputePipeline,
    vertical: wgpu::ComputePipeline,
}
#[derive(Clone)]
struct Fragment {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::RenderPipeline,
}
pub struct NativeGlyphPageEngine {
    device: wgpu::Device,
    queue: wgpu::Queue,
    compute: Option<Compute>,
    binary: Option<Binary>,
    fragment: Option<Fragment>,
}
pub struct PreparedNativeGlyphPage {
    device: wgpu::Device,
    page: Arc<NativeGlyphRasterPage>,
    gray: wgpu::Texture,
    gray_view: wgpu::TextureView,
    color: wgpu::Texture,
    color_view: wgpu::TextureView,
    sampler: wgpu::Sampler,
    instances: wgpu::Buffer,
    paints: wgpu::Buffer,
    coverage: Option<wgpu::Buffer>,
    _buffers: Vec<wgpu::Buffer>,
    compute: Option<(Compute, wgpu::BindGroup)>,
    binary: Option<(Binary, wgpu::BindGroup)>,
    fragment: Option<(Fragment, wgpu::BindGroup)>,
    metrics: NativeRasterResourceMetrics,
}
impl NativeGlyphPageEngine {
    /// Device and queue must belong to the same actual request.
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        Self {
            device: device.clone(),
            queue: queue.clone(),
            compute: None,
            binary: None,
            fragment: None,
        }
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn preflight(
        &self,
        page: &NativeGlyphRasterPage,
    ) -> Result<NativeRasterResourceMetrics, Error> {
        let source = page.source();
        let flags = source.engine_flags();
        let fragment = flags & ENGINE_GLYPH_RASTER_SHADER_FALLBACK != 0;
        // These original CPU execution families are deliberately separate from
        // the GPU producer. Never satisfy a forced CPU policy with GPU coverage.
        if flags & (ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK | ENGINE_GLYPH_SCALAR_CPU_FALLBACK)
            != 0
        {
            return Err(Error::UnsupportedExecution);
        }
        let l = self.device.limits();
        let storage = l.max_storage_buffer_binding_size.min(l.max_buffer_size);
        if l.min_uniform_buffer_offset_alignment > 256
            || l.max_uniform_buffer_binding_size < 48
            || page.atlas_size() > l.max_texture_dimension_2d
            || page.color_atlas_size() > l.max_texture_dimension_2d
        {
            return Err(Error::Capacity);
        }
        if page.has_binary_glyphs()
            && (fragment
                || l.max_compute_workgroup_size_x < 256
                || l.max_compute_workgroup_storage_size < 10240
                || l.max_storage_buffers_per_shader_stage < 4)
        {
            return Err(Error::UnsupportedExecution);
        }
        if !fragment
            && !page.work_indices().is_empty()
            && (l.max_compute_invocations_per_workgroup < 256
                || l.max_compute_workgroup_size_x < 16
                || l.max_compute_workgroup_size_y < 16
                || l.max_storage_buffers_per_shader_stage < 3)
        {
            return Err(Error::UnsupportedExecution);
        }
        if fragment && l.max_storage_buffers_per_shader_stage < 2 {
            return Err(Error::UnsupportedExecution);
        }
        let uniform = page.uniforms().len() as u64 * 256;
        let records = page.records().len() as u64 * 32;
        let segments = source.segments().len() as u64 * 48;
        let instances = page.instances().len() as u64 * 96;
        let paints = source.paints().len() as u64 * 96;
        if [records, segments, paints].into_iter().any(|v| v > storage)
            || !fragment && page.coverage_staging_bytes() > storage
            || uniform > l.max_buffer_size
            || instances > l.max_buffer_size
            || uniform > u64::from(u32::MAX) + 1
        {
            return Err(Error::Capacity);
        }
        for &index in page.work_indices() {
            let r = page.rasters()[index as usize];
            let binary = page.records()[index as usize].padding[0] != 0;
            let size = if binary {
                [r.width, r.height]
            } else {
                [r.width.div_ceil(64), r.height.div_ceil(16)]
            };
            if !fragment
                && size
                    .into_iter()
                    .any(|v| v > l.max_compute_workgroups_per_dimension)
            {
                return Err(Error::Capacity);
            }
        }
        let mut color_bytes = 0u64;
        for bitmap in source.color_bitmaps() {
            color_bytes = color_bytes
                .checked_add(
                    u64::from(bitmap.row_bytes) * u64::from(bitmap.height - 1)
                        + u64::from(bitmap.width) * 4,
                )
                .ok_or(Error::Capacity)?;
        }
        Ok(NativeRasterResourceMetrics {
            instance_upload_bytes: instances,
            outline_upload_bytes: records + segments,
            uniform_upload_bytes: uniform + paints,
            texture_upload_bytes: color_bytes,
            texture_bytes: u64::from(page.atlas_size()).pow(2)
                + u64::from(page.color_atlas_size().max(1)).pow(2) * 4,
            coverage_staging_bytes: if fragment {
                0
            } else {
                page.coverage_staging_bytes() * (1 + u64::from(page.has_binary_glyphs()))
            },
            raster_count: page.work_indices().len() as u64,
            compute_passes: u64::from(!fragment && !page.work_indices().is_empty()),
            raster_passes: u64::from(fragment && !page.work_indices().is_empty()),
            ..Default::default()
        })
    }
    pub async fn prepare(
        &mut self,
        page: Arc<NativeGlyphRasterPage>,
    ) -> Result<Arc<PreparedNativeGlyphPage>, Error> {
        let metrics = self.preflight(&page)?;
        let fragment = page.source().engine_flags() & ENGINE_GLYPH_RASTER_SHADER_FALLBACK != 0;
        if page.has_vector_glyphs() {
            self.prepare_vector(fragment).await?;
        }
        if page.has_binary_glyphs() {
            self.prepare_binary().await?;
        }
        let mut uniform_bytes = allocate(page.uniforms().len() * 256)?;
        uniform_bytes.resize(page.uniforms().len() * 256, 0);
        for (u, b) in page
            .uniforms()
            .iter()
            .zip(uniform_bytes.chunks_exact_mut(256))
        {
            b[..48].copy_from_slice(bytemuck::bytes_of(u));
        }
        let segment_bytes = common::encode_segments(page.source().segments())?;
        let mut paint_bytes = allocate(page.source().paints().len() * 96)?;
        paint_bytes.resize(page.source().paints().len() * 96, 0);
        for (p, b) in page
            .source()
            .paints()
            .iter()
            .zip(paint_bytes.chunks_exact_mut(96))
        {
            p.write_into(b).map_err(|_| Error::InvalidInput)?;
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let uniforms = common::buffer(
            &self.device,
            "ProGPU native source-indexed glyph uniforms",
            &uniform_bytes,
            wgpu::BufferUsages::UNIFORM,
            48,
        );
        let records = common::buffer(
            &self.device,
            "ProGPU native glyph records",
            bytemuck::cast_slice(page.records()),
            wgpu::BufferUsages::STORAGE,
            32,
        );
        let segments = common::buffer(
            &self.device,
            "ProGPU native glyph segments",
            &segment_bytes,
            wgpu::BufferUsages::STORAGE,
            48,
        );
        let instances = common::buffer(
            &self.device,
            "ProGPU native glyph instances",
            bytemuck::cast_slice(page.instances()),
            wgpu::BufferUsages::VERTEX,
            4,
        );
        let paints = common::buffer(
            &self.device,
            "ProGPU native glyph paints",
            &paint_bytes,
            wgpu::BufferUsages::STORAGE,
            96,
        );
        let staging = |label, copy| {
            self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size: page.coverage_staging_bytes().max(4),
                usage: wgpu::BufferUsages::STORAGE
                    | if copy {
                        wgpu::BufferUsages::COPY_SRC
                    } else {
                        wgpu::BufferUsages::empty()
                    },
                mapped_at_creation: false,
            })
        };
        let coverage = if !fragment && !page.work_indices().is_empty() {
            Some(staging("ProGPU native glyph coverage", true))
        } else {
            None
        };
        let horizontal = if page.has_binary_glyphs() {
            Some(staging("ProGPU native binary horizontal coverage", false))
        } else {
            None
        };
        let bindings = |layout: &wgpu::BindGroupLayout, binary: bool, fragment: bool| {
            let mut entries = vec![
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &uniforms,
                        offset: 0,
                        size: NonZeroU64::new(48),
                    }),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: records.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: segments.as_entire_binding(),
                },
            ];
            if !fragment {
                entries.push(wgpu::BindGroupEntry {
                    binding: 3,
                    resource: coverage
                        .as_ref()
                        .expect("complete compute preflight owns coverage")
                        .as_entire_binding(),
                });
            }
            if binary {
                entries.push(wgpu::BindGroupEntry {
                    binding: 4,
                    resource: horizontal
                        .as_ref()
                        .expect("complete binary preflight owns horizontal storage")
                        .as_entire_binding(),
                });
            }
            self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                label: Some("ProGPU native glyph raster bindings"),
                layout,
                entries: &entries,
            })
        };
        let compute = if page.has_vector_glyphs() && !fragment {
            let p = self
                .compute
                .as_ref()
                .ok_or(Error::UnsupportedExecution)?
                .clone();
            let b = bindings(&p.layout, false, false);
            Some((p, b))
        } else {
            None
        };
        let binary = if page.has_binary_glyphs() {
            let p = self
                .binary
                .as_ref()
                .ok_or(Error::UnsupportedExecution)?
                .clone();
            let b = bindings(&p.layout, true, false);
            Some((p, b))
        } else {
            None
        };
        let fragment = if page.has_vector_glyphs() && fragment {
            let p = self
                .fragment
                .as_ref()
                .ok_or(Error::UnsupportedExecution)?
                .clone();
            let b = bindings(&p.layout, false, true);
            Some((p, b))
        } else {
            None
        };
        let gray = common::texture(
            &self.device,
            page.atlas_size(),
            wgpu::TextureFormat::R8Unorm,
        );
        let gray_view = gray.create_view(&Default::default());
        let color = common::texture(
            &self.device,
            page.color_atlas_size().max(1),
            wgpu::TextureFormat::Rgba8Unorm,
        );
        let color_view = color.create_view(&Default::default());
        let sampler = common::sampler(&self.device);
        for (bitmap, raster) in page
            .source()
            .color_bitmaps()
            .iter()
            .zip(page.color_rasters())
        {
            let count = u64::from(bitmap.row_bytes) * u64::from(bitmap.height - 1)
                + u64::from(bitmap.width) * 4;
            let start = usize::try_from(bitmap.pixel_offset).map_err(|_| Error::Capacity)?;
            let count = usize::try_from(count).map_err(|_| Error::Capacity)?;
            let pixels = page
                .source()
                .color_pixels()
                .get(start..start.checked_add(count).ok_or(Error::Capacity)?)
                .ok_or(Error::InvalidInput)?;
            self.queue.write_texture(
                wgpu::TexelCopyTextureInfo {
                    texture: &color,
                    mip_level: 0,
                    origin: wgpu::Origin3d {
                        x: raster.atlas_x,
                        y: raster.atlas_y,
                        z: 0,
                    },
                    aspect: wgpu::TextureAspect::All,
                },
                pixels,
                wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(bitmap.row_bytes),
                    rows_per_image: Some(bitmap.height),
                },
                wgpu::Extent3d {
                    width: bitmap.width,
                    height: bitmap.height,
                    depth_or_array_layers: 1,
                },
            );
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let mut buffers = vec![uniforms, records, segments];
        if let Some(h) = horizontal {
            buffers.push(h);
        }
        Ok(Arc::new(PreparedNativeGlyphPage {
            device: self.device.clone(),
            page,
            gray,
            gray_view,
            color,
            color_view,
            sampler,
            instances,
            paints,
            coverage,
            _buffers: buffers,
            compute,
            binary,
            fragment,
            metrics,
        }))
    }
    async fn prepare_vector(&mut self, fragment: bool) -> Result<(), Error> {
        if fragment && self.fragment.is_some() || !fragment && self.compute.is_some() {
            return Ok(());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = layout(&self.device, false, fragment);
        let pipeline_layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU native glyph raster pipeline"),
                bind_group_layouts: &[Some(&layout)],
                immediate_size: 0,
            });
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU canonical GlyphRasterizer"),
                source: wgpu::ShaderSource::Wgsl(crate::shaders::GLYPH.into()),
            });
        if fragment {
            let pipeline = self
                .device
                .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                    label: Some("ProGPU native glyph raster shader"),
                    layout: Some(&pipeline_layout),
                    vertex: wgpu::VertexState {
                        module: &module,
                        entry_point: Some("vs_raster_fallback"),
                        compilation_options: Default::default(),
                        buffers: &[],
                    },
                    primitive: Default::default(),
                    depth_stencil: None,
                    multisample: Default::default(),
                    fragment: Some(wgpu::FragmentState {
                        module: &module,
                        entry_point: Some("fs_raster_fallback"),
                        compilation_options: Default::default(),
                        targets: &[Some(wgpu::ColorTargetState {
                            format: wgpu::TextureFormat::R8Unorm,
                            blend: None,
                            write_mask: wgpu::ColorWrites::ALL,
                        })],
                    }),
                    multiview_mask: None,
                    cache: None,
                });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.fragment = Some(Fragment { layout, pipeline });
        } else {
            let pipeline = compute(&self.device, &pipeline_layout, &module, "cs_main");
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.compute = Some(Compute { layout, pipeline });
        }
        Ok(())
    }
    async fn prepare_binary(&mut self) -> Result<(), Error> {
        if self.binary.is_some() {
            return Ok(());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = layout(&self.device, true, false);
        let pipeline_layout = self
            .device
            .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                label: Some("ProGPU native binary glyph pipeline"),
                bind_group_layouts: &[Some(&layout)],
                immediate_size: 0,
            });
        let module = self
            .device
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("ProGPU canonical BinaryGlyphRasterizer"),
                source: wgpu::ShaderSource::Wgsl(crate::shaders::BINARY_GLYPH.into()),
            });
        let horizontal = compute(
            &self.device,
            &pipeline_layout,
            &module,
            "cs_binary_horizontal",
        );
        let vertical = compute(
            &self.device,
            &pipeline_layout,
            &module,
            "cs_binary_vertical",
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.binary = Some(Binary {
            layout,
            horizontal,
            vertical,
        });
        Ok(())
    }
}
impl PreparedNativeGlyphPage {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn page(&self) -> &Arc<NativeGlyphRasterPage> {
        &self.page
    }
    pub fn gray_texture(&self) -> &wgpu::Texture {
        &self.gray
    }
    pub fn gray_view(&self) -> &wgpu::TextureView {
        &self.gray_view
    }
    pub fn color_texture(&self) -> &wgpu::Texture {
        &self.color
    }
    pub fn color_view(&self) -> &wgpu::TextureView {
        &self.color_view
    }
    pub fn sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }
    pub fn instances(&self) -> &wgpu::Buffer {
        &self.instances
    }
    pub fn paints(&self) -> &wgpu::Buffer {
        &self.paints
    }
    pub fn metrics(&self) -> NativeRasterResourceMetrics {
        self.metrics
    }
    pub fn encode_coverage(&self, encoder: &mut wgpu::CommandEncoder) {
        if self.page.work_indices().is_empty() {
            return;
        }
        if let Some((fragment, binding)) = &self.fragment {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU native glyph raster shader pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &self.gray_view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_pipeline(&fragment.pipeline);
            for &index in self.page.work_indices() {
                let raster = self.page.rasters()[index as usize];
                pass.set_bind_group(0, binding, &[index * 256]);
                pass.set_viewport(
                    raster.atlas_x as f32,
                    raster.atlas_y as f32,
                    raster.width as f32,
                    raster.height as f32,
                    0.0,
                    1.0,
                );
                pass.set_scissor_rect(raster.atlas_x, raster.atlas_y, raster.width, raster.height);
                pass.draw(0..3, 0..1);
            }
            return;
        }
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU native ordered glyph coverage"),
                timestamp_writes: None,
            });
            for &index in self.page.work_indices() {
                let raster = self.page.rasters()[index as usize];
                let binary = self.page.records()[index as usize].padding[0] != 0;
                if binary {
                    let (p, b) = self
                        .binary
                        .as_ref()
                        .expect("complete preflight retains original binary pipelines");
                    pass.set_bind_group(0, b, &[index * 256]);
                    pass.set_pipeline(&p.horizontal);
                    pass.dispatch_workgroups(1, raster.height, 1);
                    pass.set_pipeline(&p.vertical);
                    pass.dispatch_workgroups(1, raster.width, 1);
                } else {
                    let (p, b) = self
                        .compute
                        .as_ref()
                        .expect("complete preflight retains original vector pipeline");
                    pass.set_bind_group(0, b, &[index * 256]);
                    pass.set_pipeline(&p.pipeline);
                    pass.dispatch_workgroups(
                        raster.width.div_ceil(64),
                        raster.height.div_ceil(16),
                        1,
                    );
                }
            }
        }
        let coverage = self
            .coverage
            .as_ref()
            .expect("complete compute preflight retains staging");
        for &index in self.page.work_indices() {
            let raster = self.page.rasters()[index as usize];
            common::copy(
                encoder,
                coverage,
                &self.gray,
                [raster.atlas_x, raster.atlas_y],
                [raster.width, raster.height],
                u64::from(raster.output_offset),
                raster.output_bytes_per_row,
            );
        }
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn compute(
    device: &wgpu::Device,
    layout: &wgpu::PipelineLayout,
    module: &wgpu::ShaderModule,
    entry: &str,
) -> wgpu::ComputePipeline {
    device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some(entry),
        layout: Some(layout),
        module,
        entry_point: Some(entry),
        compilation_options: Default::default(),
        cache: None,
    })
}
fn layout(device: &wgpu::Device, binary: bool, fragment: bool) -> wgpu::BindGroupLayout {
    let count = if binary {
        5
    } else if fragment {
        3
    } else {
        4
    };
    let visibility = if fragment {
        wgpu::ShaderStages::VERTEX_FRAGMENT
    } else {
        wgpu::ShaderStages::COMPUTE
    };
    let entries: Vec<_> = (0..count)
        .map(|binding| wgpu::BindGroupLayoutEntry {
            binding,
            visibility,
            ty: wgpu::BindingType::Buffer {
                ty: if binding == 0 {
                    wgpu::BufferBindingType::Uniform
                } else {
                    wgpu::BufferBindingType::Storage {
                        read_only: binding < 3,
                    }
                },
                has_dynamic_offset: binding == 0,
                min_binding_size: NonZeroU64::new(match binding {
                    0 => 48,
                    1 => 32,
                    2 => 48,
                    _ => 4,
                }),
            },
            count: None,
        })
        .collect();
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("ProGPU native glyph raster bindings"),
        entries: &entries,
    })
}
