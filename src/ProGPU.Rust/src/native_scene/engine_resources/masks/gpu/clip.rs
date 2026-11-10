//! Canonical raw native clip raster transfer and ordered R8 intersection passes.
use super::*;
use crate::native_scene::engine_resources::path::gpu::PreparedNativeRaster;
#[derive(Clone)]
pub(super) struct Pipelines {
    layout: wgpu::BindGroupLayout,
    path: wgpu::RenderPipeline,
    compose: wgpu::RenderPipeline,
}
pub(super) struct PreparedClip {
    pub texture: NativeImageTexture,
    pub metrics: NativeMaskMetrics,
    raster: Arc<PreparedNativeRaster<super::super::clip::ClipPlan>>,
    pipelines: Pipelines,
    node: wgpu::TextureView,
    targets: [wgpu::TextureView; 2],
    binding: wgpu::BindGroup,
    accumulation: [wgpu::BindGroup; 2],
    _uniforms: wgpu::Buffer,
    count: u32,
}
impl PreparedClip {
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder) {
        for index in 0..self.count {
            {
                let mut pass = begin(encoder, &self.node);
                pass.set_pipeline(&self.pipelines.path);
                pass.set_bind_group(0, &self.binding, &[index * 256]);
                pass.set_vertex_buffer(0, self.raster.vertices().slice(..));
                pass.set_index_buffer(self.raster.indices().slice(..), wgpu::IndexFormat::Uint32);
                pass.draw_indexed(index * 6..index * 6 + 6, 0, 0..1);
            }
            let target = (index & 1) as usize;
            let mut pass = begin(encoder, &self.targets[target]);
            pass.set_pipeline(&self.pipelines.compose);
            pass.set_bind_group(0, &self.accumulation[target], &[index * 256]);
            pass.draw(0..3, 0..1);
        }
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
async fn pipelines(engine: &mut NativeMaskEngine) -> Result<Pipelines, Error> {
    if let Some(p) = &engine.clip_pipelines {
        return Ok(p.clone());
    }
    let device = &engine.device;
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("Native raw clip composition layout"),
        entries: &[
            wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count: None,
            },
            wgpu::BindGroupLayoutEntry {
                binding: 1,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: None,
            },
            wgpu::BindGroupLayoutEntry {
                binding: 2,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Texture {
                    sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled: false,
                },
                count: None,
            },
            wgpu::BindGroupLayoutEntry {
                binding: 3,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: true,
                    min_binding_size: std::num::NonZeroU64::new(32),
                },
                count: None,
            },
        ],
    });
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("Native raw clip original slots"),
        bind_group_layouts: &[Some(&layout)],
        immediate_size: 0,
    });
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("Native canonical raw clip transfer"),
        source: wgpu::ShaderSource::Wgsl(crate::shaders::CLIP_COMPOSE.into()),
    });
    let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x2];
    let buffers = [Some(wgpu::VertexBufferLayout {
        array_stride: 16,
        step_mode: wgpu::VertexStepMode::Vertex,
        attributes: &attributes,
    })];
    let make = |path| {
        device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Native clip node and accumulation"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some(if path { "vs_path" } else { "vs_compose" }),
                compilation_options: Default::default(),
                buffers: if path { &buffers } else { &[] },
            },
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some(if path { "fs_path" } else { "fs_compose" }),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::R8Unorm,
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            multiview_mask: None,
            cache: None,
        })
    };
    let result = Pipelines {
        layout,
        path: make(true),
        compose: make(false),
    };
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    engine.clip_pipelines = Some(result.clone());
    Ok(result)
}
pub(super) async fn prepare(
    engine: &mut NativeMaskEngine,
    plan: Arc<super::super::clip::ClipPlan>,
    size: [u32; 2],
) -> Result<PreparedClip, Error> {
    let p = pipelines(engine).await?;
    let raster = engine.raster.prepare_plan(plan.clone()).await?;
    let device = &engine.device;
    let mut bytes = allocate(plan.uniforms().len() * 256)?;
    bytes.resize(plan.uniforms().len() * 256, 0);
    for (slot, record) in bytes.chunks_exact_mut(256).zip(plan.uniforms()) {
        slot[..32].copy_from_slice(bytemuck::bytes_of(record));
    }
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let uniforms = buffer(
        device,
        "Native clip aligned compose records",
        &bytes,
        wgpu::BufferUsages::UNIFORM,
    );
    let node = texture(device, size, wgpu::TextureUsages::RENDER_ATTACHMENT)
        .create_view(&Default::default());
    let textures = [
        texture(device, size, wgpu::TextureUsages::RENDER_ATTACHMENT),
        texture(device, size, wgpu::TextureUsages::RENDER_ATTACHMENT),
    ];
    let targets = textures
        .each_ref()
        .map(|t| t.create_view(&Default::default()));
    let sampler = &engine.samplers.as_ref().ok_or(Error::ResourceDomain)?[1];
    let binding = |source: &wgpu::TextureView, previous: &wgpu::TextureView| {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Native original clip source binding"),
            layout: &p.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(source),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(previous),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &uniforms,
                        offset: 0,
                        size: std::num::NonZeroU64::new(32),
                    }),
                },
            ],
        })
    };
    let source_binding = binding(raster.view(), raster.view());
    let accumulation = std::array::from_fn(|index| binding(&node, &targets[1 - index]));
    let count = plan.uniforms().len() as u32;
    let texture = owned(device, &textures[((count - 1) & 1) as usize])?;
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    let work = raster.encode_coverage(device.create_command_encoder(
        &wgpu::CommandEncoderDescriptor {
            label: Some("Native clip compute preparation"),
        },
    ))?;
    for command in work.preceding {
        engine.queue.submit([command]);
    }
    engine.queue.submit([work.encoder.finish()]);
    raster.retain_submitted(&engine.queue);
    let m = raster.metrics();
    let metrics = NativeMaskMetrics {
        texture_bytes: u64::from(size[0]) * u64::from(size[1]) * 3 + m.texture_bytes,
        uniform_upload_bytes: bytes.len() as u64 + m.uniform_upload_bytes,
        geometry_upload_bytes: m.vertex_upload_bytes
            + m.index_upload_bytes
            + m.outline_upload_bytes,
        render_passes: count * 2,
        path_rasters: u32::try_from(m.raster_count).map_err(|_| Error::Capacity)?,
        compute_passes: u32::try_from(m.compute_passes).map_err(|_| Error::Capacity)?,
        coverage_staging_bytes: m.coverage_staging_bytes,
        ..Default::default()
    };
    Ok(PreparedClip {
        texture,
        metrics,
        raster,
        pipelines: p,
        node,
        targets,
        binding: source_binding,
        accumulation,
        _uniforms: uniforms,
        count,
    })
}

/// Single original folded-revision cache. Each entry is a completed immutable
/// target, so eviction cannot overwrite previously prepared command bindings.
pub(super) struct Cache {
    revision: u32,
    target: NativeMaskTarget,
    _scene: Arc<NativeScene>,
    pub prepared: Arc<PreparedClip>,
}
impl Cache {
    pub fn new(revision: u32, request: &NativeMaskRequest, prepared: Arc<PreparedClip>) -> Self {
        Self {
            revision,
            target: request.target,
            _scene: request.scene.clone(),
            prepared,
        }
    }
    pub fn matches(&self, revision: u32, target: NativeMaskTarget) -> bool {
        self.revision == revision
            && self.target.dpi_scale == target.dpi_scale
            && self.target.size() == target.size()
    }
}
pub(super) fn revision(request: &NativeMaskRequest) -> Result<u32, Error> {
    let resource = request
        .scene
        .envelope()
        .resource(request.resource_index)
        .ok_or(Error::InvalidInput)?
        .record;
    let t = request.target;
    let mut mixed = resource.resource_id.wrapping_mul(0x9E3779B185EBCA87) ^ resource.generation;
    mixed ^= u64::from(t.extent.x).wrapping_mul(0xC2B2AE3D27D4EB4F);
    mixed ^= u64::from(t.extent.y).wrapping_mul(0x165667B19E3779F9);
    mixed = crate::native_scene::identity::presentation_content_hash(
        mixed,
        [
            t.presentation.viewport_width,
            t.presentation.viewport_height,
        ],
        t.dpi_scale,
        &t.presentation,
    );
    let revision = (mixed ^ (mixed >> 32)) as u32;
    Ok(if revision == 0 { 1 } else { revision })
}
