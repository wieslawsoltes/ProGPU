//! Actual native compute pages, explicit source phase boundaries and GPU owners.
use super::*;
use std::{collections::BTreeMap, num::NonZeroU64};
use wgpu::util::DeviceExt;
mod phase;
mod plan;
use phase::{Kernel, Phase};
pub use plan::NativeRasterPlan;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeRasterResourceMetrics {
    pub vertex_upload_bytes: u64,
    pub index_upload_bytes: u64,
    pub instance_upload_bytes: u64,
    pub outline_upload_bytes: u64,
    pub uniform_upload_bytes: u64,
    pub texture_upload_bytes: u64,
    pub texture_bytes: u64,
    pub coverage_staging_bytes: u64,
    pub raster_count: u64,
    pub compute_passes: u64,
    pub raster_passes: u64,
}
pub struct NativePathPageEngine {
    device: wgpu::Device,
    layout: Option<wgpu::BindGroupLayout>,
    pipelines: BTreeMap<Kernel, wgpu::ComputePipeline>,
}
struct OwnedPhase {
    pipeline: wgpu::ComputePipeline,
    bindings: Vec<(wgpu::BindGroup, [u32; 3])>,
    split_after: bool,
}
pub type PreparedNativePathPage = PreparedNativeRaster<NativePathRasterPage>;
pub struct PreparedNativeRaster<P: NativeRasterPlan> {
    device: wgpu::Device,
    source: Arc<P>,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    sampler: wgpu::Sampler,
    vertices: wgpu::Buffer,
    indices: wgpu::Buffer,
    coverage: wgpu::Buffer,
    phases: Vec<OwnedPhase>,
    _buffers: Vec<wgpu::Buffer>,
    metrics: NativeRasterResourceMetrics,
}
/// Original command-buffer boundaries. Submit `preceding` individually in
/// order, then continue recording consumers in `encoder` on the same queue.
pub struct NativePathCoverageCommands {
    pub preceding: Vec<wgpu::CommandBuffer>,
    pub encoder: wgpu::CommandEncoder,
}
impl NativePathPageEngine {
    pub fn new(device: &wgpu::Device) -> Self {
        Self {
            device: device.clone(),
            layout: None,
            pipelines: BTreeMap::new(),
        }
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn preflight(
        &self,
        page: &NativePathRasterPage,
    ) -> Result<NativeRasterResourceMetrics, Error> {
        self.preflight_plan(page)
    }
    pub(crate) fn preflight_plan<P: NativeRasterPlan>(
        &self,
        page: &P,
    ) -> Result<NativeRasterResourceMetrics, Error> {
        let l = self.device.limits();
        if page.rasters().is_empty()
            || l.max_compute_invocations_per_workgroup < 256
            || l.max_compute_workgroup_size_x < 16
            || l.max_compute_workgroup_size_y < 16
            || l.max_storage_buffers_per_shader_stage < 5
            || l.min_storage_buffer_offset_alignment > 256
        {
            return Err(Error::UnsupportedExecution);
        }
        if page.atlas_size() > l.max_texture_dimension_2d {
            return Err(Error::Capacity);
        }
        let phases = phase::plan(page)?;
        let storage = l.max_storage_buffer_binding_size.min(l.max_buffer_size);
        let records = page.records().len() as u64 * 32;
        let segments = page.segments().len() as u64 * 48;
        if [records, segments, page.coverage_staging_bytes()]
            .into_iter()
            .any(|n| n > storage)
            || page.vertex_bytes().len() as u64 > l.max_buffer_size
            || page.index_bytes().len() as u64 > l.max_buffer_size
        {
            return Err(Error::Capacity);
        }
        let mut uniform = 0u64;
        for phase in &phases {
            if phase.bytes.len() as u64 > l.max_buffer_size {
                return Err(Error::Capacity);
            }
            for group in &phase.groups {
                if group.bytes > storage
                    || group
                        .size
                        .into_iter()
                        .any(|v| v == 0 || v > l.max_compute_workgroups_per_dimension)
                {
                    return Err(Error::Capacity);
                }
            }
            uniform = uniform
                .checked_add(phase.bytes.len() as u64)
                .ok_or(Error::Capacity)?;
        }
        Ok(NativeRasterResourceMetrics {
            vertex_upload_bytes: page.vertex_bytes().len() as u64,
            index_upload_bytes: page.index_bytes().len() as u64,
            outline_upload_bytes: records + segments,
            uniform_upload_bytes: uniform,
            texture_bytes: u64::from(page.atlas_size()).pow(2),
            coverage_staging_bytes: page.coverage_staging_bytes(),
            raster_count: page.rasters().len() as u64,
            compute_passes: phases.len() as u64,
            ..Default::default()
        })
    }
    pub async fn prepare(
        &mut self,
        page: Arc<NativePathRasterPage>,
    ) -> Result<Arc<PreparedNativePathPage>, Error> {
        self.prepare_plan(page).await
    }
    pub(crate) async fn prepare_plan<P: NativeRasterPlan + 'static>(
        &mut self,
        page: Arc<P>,
    ) -> Result<Arc<PreparedNativeRaster<P>>, Error> {
        let metrics = self.preflight_plan(page.as_ref())?;
        let plan = phase::plan(page.as_ref())?;
        self.prepare_pipelines(&plan).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layout = self.layout.as_ref().ok_or(Error::UnsupportedExecution)?;
        let records = buffer(
            &self.device,
            "ProGPU native path records",
            bytemuck::cast_slice(page.records()),
            wgpu::BufferUsages::STORAGE,
            32,
        );
        let segment_bytes = encode_segments(page.segments())?;
        let segments = buffer(
            &self.device,
            "ProGPU native path segments",
            &segment_bytes,
            wgpu::BufferUsages::STORAGE,
            48,
        );
        let coverage = self.device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("ProGPU native path coverage staging"),
            size: page.coverage_staging_bytes().max(4),
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let dummy_path = buffer(
            &self.device,
            "ProGPU native absent path phase",
            &[0; 48],
            wgpu::BufferUsages::STORAGE,
            48,
        );
        let dummy_combine = buffer(
            &self.device,
            "ProGPU native absent combine phase",
            &[0; 40],
            wgpu::BufferUsages::STORAGE,
            40,
        );
        let mut owned = allocate(plan.len())?;
        let mut buffers = allocate(plan.len() + 4)?;
        for phase in plan {
            let uniforms = buffer(
                &self.device,
                "ProGPU native path phase uniforms",
                &phase.bytes,
                wgpu::BufferUsages::STORAGE,
                if phase.combine { 40 } else { 48 },
            );
            let mut bindings = allocate(phase.groups.len())?;
            for group in phase.groups {
                let ranged = wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer: &uniforms,
                    offset: group.offset,
                    size: NonZeroU64::new(group.bytes),
                });
                let binding = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: Some("ProGPU native path phase"),
                    layout,
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: if phase.combine {
                                dummy_path.as_entire_binding()
                            } else {
                                ranged.clone()
                            },
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: records.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: segments.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: coverage.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 4,
                            resource: if phase.combine {
                                ranged
                            } else {
                                dummy_combine.as_entire_binding()
                            },
                        },
                    ],
                });
                bindings.push((binding, group.size));
            }
            owned.push(OwnedPhase {
                pipeline: self
                    .pipelines
                    .get(&phase.kernel)
                    .ok_or(Error::UnsupportedExecution)?
                    .clone(),
                bindings,
                split_after: phase.split_after,
            });
            buffers.push(uniforms);
        }
        buffers.extend([records, segments, dummy_path, dummy_combine]);
        let vertices = buffer(
            &self.device,
            "ProGPU native path vertices",
            page.vertex_bytes(),
            wgpu::BufferUsages::VERTEX,
            4,
        );
        let indices = buffer(
            &self.device,
            "ProGPU native path indices",
            page.index_bytes(),
            wgpu::BufferUsages::INDEX,
            4,
        );
        let texture = texture(
            &self.device,
            page.atlas_size(),
            wgpu::TextureFormat::R8Unorm,
        );
        let view = texture.create_view(&Default::default());
        let sampler = sampler(&self.device);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Arc::new(PreparedNativeRaster {
            device: self.device.clone(),
            source: page,
            texture,
            view,
            sampler,
            vertices,
            indices,
            coverage,
            phases: owned,
            _buffers: buffers,
            metrics,
        }))
    }
    async fn prepare_pipelines(&mut self, plan: &[Phase]) -> Result<(), Error> {
        for phase in plan {
            if self.pipelines.contains_key(&phase.kernel) {
                continue;
            }
            let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
            let layout = self.layout.clone().unwrap_or_else(|| {
                self.device
                    .create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                        label: Some("ProGPU native path raster layout"),
                        entries: &std::array::from_fn::<_, 5, _>(|i| wgpu::BindGroupLayoutEntry {
                            binding: i as u32,
                            visibility: wgpu::ShaderStages::COMPUTE,
                            ty: wgpu::BindingType::Buffer {
                                ty: wgpu::BufferBindingType::Storage { read_only: i != 3 },
                                has_dynamic_offset: false,
                                min_binding_size: NonZeroU64::new([48, 32, 48, 4, 40][i]),
                            },
                            count: None,
                        }),
                    })
            });
            let pipeline_layout =
                self.device
                    .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                        label: Some("ProGPU native path raster pipeline"),
                        bind_group_layouts: &[Some(&layout)],
                        immediate_size: 0,
                    });
            let (source, entry) = phase.kernel.shader();
            let module = self
                .device
                .create_shader_module(wgpu::ShaderModuleDescriptor {
                    label: Some(entry),
                    source: wgpu::ShaderSource::Wgsl(source.into()),
                });
            let pipeline = self
                .device
                .create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                    label: Some(entry),
                    layout: Some(&pipeline_layout),
                    module: &module,
                    entry_point: Some(entry),
                    compilation_options: Default::default(),
                    cache: None,
                });
            if let Some(error) = scope.pop().await {
                return Err(error.into());
            }
            self.layout = Some(layout);
            self.pipelines.insert(phase.kernel, pipeline);
        }
        Ok(())
    }
}
impl<P: NativeRasterPlan + 'static> PreparedNativeRaster<P> {
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn page(&self) -> &Arc<P> {
        &self.source
    }
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }
    pub fn sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }
    pub fn vertices(&self) -> &wgpu::Buffer {
        &self.vertices
    }
    pub fn indices(&self) -> &wgpu::Buffer {
        &self.indices
    }
    pub fn metrics(&self) -> NativeRasterResourceMetrics {
        self.metrics
    }
    pub fn encode_coverage(
        &self,
        mut encoder: wgpu::CommandEncoder,
    ) -> Result<NativePathCoverageCommands, Error> {
        let split = !self.source.combine_uniforms().is_empty()
            || !self.source.signed_combine_uniforms().is_empty();
        let mut preceding = allocate(self.phases.len() + 1)?;
        let fresh = || {
            self.device
                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                    label: Some("ProGPU native path phase commands"),
                })
        };
        if split {
            preceding.push(encoder.finish());
            encoder = fresh();
        }
        for phase in &self.phases {
            {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    label: Some("ProGPU native ordered path coverage"),
                    timestamp_writes: None,
                });
                pass.set_pipeline(&phase.pipeline);
                for (binding, size) in &phase.bindings {
                    pass.set_bind_group(0, binding, &[]);
                    pass.dispatch_workgroups(size[0], size[1], size[2]);
                }
            }
            if split && phase.split_after {
                preceding.push(encoder.finish());
                encoder = fresh();
            }
        }
        for raster in self.source.rasters() {
            copy(
                &mut encoder,
                &self.coverage,
                &self.texture,
                [raster.atlas_x, raster.atlas_y],
                [raster.width, raster.height],
                u64::from(raster.output_offset),
                raster.output_bytes_per_row,
            );
        }
        Ok(NativePathCoverageCommands { preceding, encoder })
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
pub(crate) fn buffer(
    device: &wgpu::Device,
    label: &str,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
    minimum: u64,
) -> wgpu::Buffer {
    if bytes.is_empty() {
        device.create_buffer(&wgpu::BufferDescriptor {
            label: Some(label),
            size: minimum,
            usage: usage | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        })
    } else {
        device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some(label),
            contents: bytes,
            usage: usage | wgpu::BufferUsages::COPY_DST,
        })
    }
}
pub(crate) fn encode_segments(segments: &[wire::PathSegment]) -> Result<Vec<u8>, Error> {
    let count = segments.len().checked_mul(48).ok_or(Error::Capacity)?;
    let mut bytes = allocate(count)?;
    bytes.resize(count, 0);
    for (segment, target) in segments.iter().zip(bytes.chunks_exact_mut(48)) {
        segment
            .write_into(target)
            .map_err(|_| Error::InvalidInput)?;
    }
    Ok(bytes)
}
pub(crate) fn texture(
    device: &wgpu::Device,
    size: u32,
    format: wgpu::TextureFormat,
) -> wgpu::Texture {
    device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU native immutable atlas generation"),
        size: wgpu::Extent3d {
            width: size,
            height: size,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_DST
            | wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    })
}
pub(crate) fn sampler(device: &wgpu::Device) -> wgpu::Sampler {
    device.create_sampler(&wgpu::SamplerDescriptor {
        label: Some("ProGPU native atlas sampler"),
        mag_filter: wgpu::FilterMode::Linear,
        min_filter: wgpu::FilterMode::Linear,
        mipmap_filter: wgpu::MipmapFilterMode::Nearest,
        lod_min_clamp: 0.0,
        lod_max_clamp: 0.0,
        anisotropy_clamp: 1,
        ..Default::default()
    })
}
pub(crate) fn copy(
    encoder: &mut wgpu::CommandEncoder,
    source: &wgpu::Buffer,
    target: &wgpu::Texture,
    origin: [u32; 2],
    size: [u32; 2],
    offset: u64,
    pitch: u32,
) {
    encoder.copy_buffer_to_texture(
        wgpu::TexelCopyBufferInfo {
            buffer: source,
            layout: wgpu::TexelCopyBufferLayout {
                offset,
                bytes_per_row: Some(pitch),
                rows_per_image: Some(size[1]),
            },
        },
        wgpu::TexelCopyTextureInfo {
            texture: target,
            mip_level: 0,
            origin: wgpu::Origin3d {
                x: origin[0],
                y: origin[1],
                z: 0,
            },
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
    );
}
