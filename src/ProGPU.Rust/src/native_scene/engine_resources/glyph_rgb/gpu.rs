//! One-shot native RGB execution owns the actual opaque attachment, destination
//! snapshot and original mask producer. Pipeline reuse never implies coverage reuse.
use super::*;
use crate::native_scene::{
    engine_gpu::{NativeMaskBinding, NativeMaskMode, NativeRenderLayouts},
    engine_resources::path::gpu as common,
};
use std::num::NonZeroU64;
/// Only the owning native executor can certify its actual materialized layer.
pub struct NativeRgbTarget {
    device: wgpu::Device,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    page: Arc<NativeRgbGlyphPage>,
    _producer: Arc<dyn NativeImageTextureOwner>,
}
impl NativeRgbTarget {
    pub(crate) fn from_materialized_layer(
        device: &wgpu::Device,
        texture: &wgpu::Texture,
        view: &wgpu::TextureView,
        page: Arc<NativeRgbGlyphPage>,
        producer: Arc<dyn NativeImageTextureOwner>,
    ) -> Result<Self, Error> {
        let f = page.frame();
        if !f.ignores_alpha()
            || texture.dimension() != wgpu::TextureDimension::D2
            || texture.sample_count() != 1
            || texture.width() != f.target.width
            || texture.height() != f.target.height
            || !matches!(
                texture.format(),
                wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Bgra8Unorm
            )
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::COPY_SRC | wgpu::TextureUsages::RENDER_ATTACHMENT)
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            device: device.clone(),
            texture: texture.clone(),
            view: view.clone(),
            page,
            _producer: producer,
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }
    pub fn page(&self) -> &Arc<NativeRgbGlyphPage> {
        &self.page
    }
}
/// Retained mask produced from this exact occurrence's original resource/frame.
/// Construction belongs to the shared native source mask producer, not callers
/// supplying a generic texture or an inferred coverage envelope.
pub struct NativeRgbMaskSource {
    page: Arc<NativeRgbGlyphPage>,
    mask: Arc<NativeMaskBinding>,
}
impl NativeRgbMaskSource {
    pub(crate) fn from_source(
        page: Arc<NativeRgbGlyphPage>,
        mask: Arc<NativeMaskBinding>,
    ) -> Result<Self, Error> {
        let kind = mask_kind(page.frame())?.ok_or(Error::InvalidInput)?;
        let expected = if kind == SCENE_LAYER_MASK_ANALYTIC_CHAIN {
            NativeMaskMode::Chain
        } else {
            NativeMaskMode::Sampled
        };
        if mask.mode() != expected {
            return Err(Error::InvalidInput);
        }
        Ok(Self { page, mask })
    }
    pub fn page(&self) -> &Arc<NativeRgbGlyphPage> {
        &self.page
    }
    pub fn mask(&self) -> &Arc<NativeMaskBinding> {
        &self.mask
    }
}
pub struct NativeRgbGlyphEngine {
    device: wgpu::Device,
    format: wgpu::TextureFormat,
    pipelines: pipeline::Cache,
}
struct Resources {
    device: wgpu::Device,
    page: Arc<NativeRgbGlyphPage>,
    target: Arc<NativeRgbTarget>,
    mask: Option<Arc<NativeRgbMaskSource>>,
    pipelines: Arc<pipeline::Pipelines>,
    atlas: wgpu::Texture,
    atlas_view: wgpu::TextureView,
    backdrop: wgpu::Texture,
    _backdrop_view: wgpu::TextureView,
    coverage: Option<wgpu::Buffer>,
    _buffers: Vec<wgpu::Buffer>,
    raster: wgpu::BindGroup,
    composite: wgpu::BindGroup,
    metrics: NativeRgbGlyphMetrics,
}
/// A prepared RGB execution can be encoded only once; the original source
/// uploads each replay and snapshots the live destination at its ordered draw.
pub struct PreparedNativeRgbGlyphPage {
    resources: Arc<Resources>,
}
pub struct NativeRgbGlyphSubmission {
    resources: Arc<Resources>,
}
impl NativeRgbGlyphEngine {
    pub fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        layouts: NativeRenderLayouts,
    ) -> Result<Self, Error> {
        if !matches!(
            format,
            wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Bgra8Unorm
        ) {
            return Err(Error::UnsupportedExecution);
        }
        Ok(Self {
            device: device.clone(),
            format,
            pipelines: pipeline::Cache::new(device, format, layouts),
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn preflight(
        &self,
        page: &Arc<NativeRgbGlyphPage>,
        target: &NativeRgbTarget,
        mask: Option<&NativeRgbMaskSource>,
    ) -> Result<NativeRgbGlyphMetrics, Error> {
        if target.device() != &self.device
            || target.texture.format() != self.format
            || !Arc::ptr_eq(page, target.page())
        {
            return Err(Error::ResourceDomain);
        }
        if !page.frame.ignores_alpha
            || target.texture.width() != page.frame.target.width
            || target.texture.height() != page.frame.target.height
        {
            return Err(Error::UnsupportedExecution);
        }
        if !page.drawable() {
            return Ok(Default::default());
        }
        if page.mask_resource_index().is_some() != mask.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        if let Some(mask) = mask {
            if !Arc::ptr_eq(page, mask.page()) || mask.mask.device() != &self.device {
                return Err(Error::ResourceDomain);
            }
        }
        let l = self.device.limits();
        let fragment = page.engine_flags & ENGINE_GLYPH_RASTER_SHADER_FALLBACK != 0;
        let uniform_size = match mask.map(|m| m.mask.mode()) {
            Some(NativeMaskMode::Chain) => 288,
            Some(NativeMaskMode::Sampled) => 96,
            _ => 48,
        };
        if l.min_uniform_buffer_offset_alignment > 256
            || l.max_uniform_buffer_binding_size < uniform_size
            || l.max_storage_buffers_per_shader_stage < 3
            || l.max_bind_groups < if mask.is_some() { 3 } else { 1 }
            || l.max_sampled_textures_per_shader_stage < if mask.is_some() { 3 } else { 2 }
            || (!fragment
                && (l.max_compute_invocations_per_workgroup < 256
                    || l.max_compute_workgroup_size_x < 16
                    || l.max_compute_workgroup_size_y < 16))
        {
            return Err(Error::UnsupportedExecution);
        }
        if page
            .atlas_size
            .into_iter()
            .chain(page.backdrop_size)
            .any(|n| n > l.max_texture_dimension_2d)
        {
            return Err(Error::Capacity);
        }
        let storage = l
            .max_buffer_size
            .min(l.max_storage_buffer_binding_size)
            .min(128 * 1024 * 1024);
        let sizes = [
            page.records.len() as u64 * 32,
            page.segments.len() as u64 * 48,
            page.instances.len() as u64 * 48,
            page.cells.len() as u64 * 32,
            page.references.len() as u64 * 4,
        ];
        if sizes.into_iter().any(|n| n == 0 || n > storage)
            || page.coverage_bytes > storage
            || page.uniforms.len() as u64 * 256 > l.max_buffer_size
        {
            return Err(Error::Capacity);
        }
        if !fragment
            && page.uniforms.iter().any(|u| {
                u.width.div_ceil(16) > l.max_compute_workgroups_per_dimension
                    || u.height.div_ceil(16) > l.max_compute_workgroups_per_dimension
            })
        {
            return Err(Error::Capacity);
        }
        Ok(page.metrics())
    }
    pub async fn prepare(
        &mut self,
        page: Arc<NativeRgbGlyphPage>,
        target: Arc<NativeRgbTarget>,
        mask: Option<Arc<NativeRgbMaskSource>>,
    ) -> Result<Option<PreparedNativeRgbGlyphPage>, Error> {
        let metrics = self.preflight(&page, &target, mask.as_deref())?;
        if !page.drawable() {
            return Ok(None);
        }
        let fragment = page.engine_flags & ENGINE_GLYPH_RASTER_SHADER_FALLBACK != 0;
        let mode = mask
            .as_ref()
            .map_or(NativeMaskMode::None, |m| m.mask.mode());
        let pipelines = self.pipelines.prepare(fragment, mode).await?;
        let mut uniform_bytes = allocate(page.uniforms.len() * 256)?;
        uniform_bytes.resize(page.uniforms.len() * 256, 0);
        for (uniform, bytes) in page
            .uniforms
            .iter()
            .zip(uniform_bytes.chunks_exact_mut(256))
        {
            bytes[..48].copy_from_slice(bytemuck::bytes_of(uniform));
        }
        let segment_bytes = common::encode_segments(&page.segments)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let uniforms = common::buffer(
            &self.device,
            "ProGPU RGB glyph uniforms",
            &uniform_bytes,
            wgpu::BufferUsages::UNIFORM,
            48,
        );
        let records = common::buffer(
            &self.device,
            "ProGPU RGB glyph records",
            bytemuck::cast_slice(&page.records),
            wgpu::BufferUsages::STORAGE,
            32,
        );
        let segments = common::buffer(
            &self.device,
            "ProGPU RGB glyph segments",
            &segment_bytes,
            wgpu::BufferUsages::STORAGE,
            48,
        );
        let policy = [
            page.policy.pixel_geometry,
            page.policy.filter_model,
            0u32,
            0u32,
        ];
        let policy = common::buffer(
            &self.device,
            "ProGPU RGB glyph original policy",
            bytemuck::cast_slice(&policy),
            wgpu::BufferUsages::UNIFORM,
            16,
        );
        let instances = common::buffer(
            &self.device,
            "ProGPU RGB glyph ordered instances",
            bytemuck::cast_slice(&page.instances),
            wgpu::BufferUsages::STORAGE,
            48,
        );
        let cells = common::buffer(
            &self.device,
            "ProGPU RGB glyph disjoint cells",
            bytemuck::cast_slice(&page.cells),
            wgpu::BufferUsages::STORAGE,
            32,
        );
        let references = common::buffer(
            &self.device,
            "ProGPU RGB glyph ordered cell references",
            bytemuck::cast_slice(&page.references),
            wgpu::BufferUsages::STORAGE,
            4,
        );
        let frame = common::buffer(
            &self.device,
            "ProGPU RGB glyph target frame",
            bytemuck::bytes_of(&page.composite),
            wgpu::BufferUsages::UNIFORM,
            32,
        );
        let coverage = (!fragment).then(|| {
            self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("ProGPU RGB glyph packed coverage"),
                size: page.coverage_bytes,
                usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: false,
            })
        });
        let atlas = texture(
            &self.device,
            page.atlas_size,
            wgpu::TextureFormat::Rgba8Unorm,
            true,
        );
        let atlas_view = atlas.create_view(&Default::default());
        let backdrop = texture(&self.device, page.backdrop_size, self.format, false);
        let backdrop_view = backdrop.create_view(&Default::default());
        let mut raster_entries = vec![
            wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: &uniforms,
                    offset: 0,
                    size: NonZeroU64::new(48),
                }),
            },
            entry(1, &records),
            entry(2, &segments),
        ];
        if let Some(coverage) = &coverage {
            raster_entries.push(entry(3, coverage));
        }
        raster_entries.push(entry(4, &policy));
        let raster = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU RGB glyph raster batch"),
            layout: &pipelines.raster_layout,
            entries: &raster_entries,
        });
        let composite = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU RGB glyph composite batch"),
            layout: &pipelines.composite_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&atlas_view),
                },
                entry(1, &instances),
                entry(2, &frame),
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::TextureView(&backdrop_view),
                },
                entry(4, &cells),
                entry(5, &references),
            ],
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Some(PreparedNativeRgbGlyphPage {
            resources: Arc::new(Resources {
                device: self.device.clone(),
                page,
                target,
                mask,
                pipelines,
                atlas,
                atlas_view,
                backdrop,
                _backdrop_view: backdrop_view,
                coverage,
                _buffers: vec![
                    uniforms, records, segments, policy, instances, cells, references, frame,
                ],
                raster,
                composite,
                metrics,
            }),
        }))
    }
}
impl PreparedNativeRgbGlyphPage {
    pub fn page(&self) -> &Arc<NativeRgbGlyphPage> {
        &self.resources.page
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.resources.device
    }
    pub fn metrics(&self) -> NativeRgbGlyphMetrics {
        self.resources.metrics
    }
    /// Encode at this occurrence's original position between surrounding draw
    /// bundles. On a later frame call prepare again, including destination capture.
    pub fn encode(self, encoder: &mut wgpu::CommandEncoder) -> NativeRgbGlyphSubmission {
        let r = &self.resources;
        let p = &r.page;
        match &r.pipelines.raster {
            pipeline::Raster::Fragment(pipeline) => {
                let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("ProGPU RGB glyph fragment coverage"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: &r.atlas_view,
                        depth_slice: None,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    ..Default::default()
                });
                pass.set_pipeline(pipeline);
                for (i, u) in p.uniforms.iter().enumerate() {
                    pass.set_bind_group(0, &r.raster, &[(i * 256) as u32]);
                    pass.set_viewport(
                        u.atlas_x,
                        u.atlas_y,
                        u.width as f32,
                        u.height as f32,
                        0.0,
                        1.0,
                    );
                    pass.set_scissor_rect(u.atlas_x as u32, u.atlas_y as u32, u.width, u.height);
                    pass.draw(0..3, 0..1);
                }
            }
            pipeline::Raster::Compute(pipeline) => {
                {
                    let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                        label: Some("ProGPU RGB glyph compute coverage"),
                        timestamp_writes: None,
                    });
                    pass.set_pipeline(pipeline);
                    for (i, u) in p.uniforms.iter().enumerate() {
                        pass.set_bind_group(0, &r.raster, &[(i * 256) as u32]);
                        pass.dispatch_workgroups(u.width.div_ceil(16), u.height.div_ceil(16), 1);
                    }
                }
                let coverage = r
                    .coverage
                    .as_ref()
                    .expect("complete original RGB compute storage");
                for u in p.uniforms.iter() {
                    common::copy(
                        encoder,
                        coverage,
                        &r.atlas,
                        [u.atlas_x as u32, u.atlas_y as u32],
                        [u.width, u.height],
                        u64::from(u.output_offset_words) * 4,
                        u.output_row_words * 4,
                    );
                }
            }
        }
        encoder.copy_texture_to_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &r.target.texture,
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x: p.composite.backdrop_origin[0],
                    y: p.composite.backdrop_origin[1],
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            r.backdrop.as_image_copy(),
            wgpu::Extent3d {
                width: p.backdrop_size[0],
                height: p.backdrop_size[1],
                depth_or_array_layers: 1,
            },
        );
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU RGB glyph original channel composition"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &r.target.view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
            pass.set_viewport(
                0.0,
                0.0,
                p.composite.extent[0],
                p.composite.extent[1],
                0.0,
                1.0,
            );
            let s = p.scissor;
            pass.set_scissor_rect(s.x, s.y, s.width, s.height);
            pass.set_bind_group(0, &r.composite, &[]);
            if let Some(mask) = &r.mask {
                pass.set_bind_group(
                    1,
                    r.pipelines
                        .empty
                        .as_ref()
                        .expect("original masked empty group"),
                    &[],
                );
                pass.set_bind_group(2, mask.mask.bind_group(), &[]);
            }
            for pipeline in &r.pipelines.composite {
                pass.set_pipeline(pipeline);
                pass.draw(0..6, 0..p.cell_count);
            }
        }
        NativeRgbGlyphSubmission {
            resources: self.resources,
        }
    }
}
impl NativeRgbGlyphSubmission {
    pub fn metrics(&self) -> NativeRgbGlyphMetrics {
        self.resources.metrics
    }
    pub fn page(&self) -> &Arc<NativeRgbGlyphPage> {
        &self.resources.page
    }
    /// Call after the actual queue submission. The callback owns the target,
    /// original source/mask producer, snapshot and every uploaded resource.
    pub fn retain_submitted(self, queue: &wgpu::Queue) {
        queue.on_submitted_work_done(move || drop(self));
    }
}
fn entry(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
fn texture(
    device: &wgpu::Device,
    size: [u32; 2],
    format: wgpu::TextureFormat,
    render: bool,
) -> wgpu::Texture {
    device.create_texture(&wgpu::TextureDescriptor {
        label: Some(if render {
            "ProGPU RGB glyph independent coverage"
        } else {
            "ProGPU RGB glyph owned destination snapshot"
        }),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_DST
            | if render {
                wgpu::TextureUsages::RENDER_ATTACHMENT
            } else {
                wgpu::TextureUsages::empty()
            },
        view_formats: &[],
    })
}
