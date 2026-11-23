//! Original sampled-mask R8 ping-pong passes, preserving actual child views.
use super::*;
#[derive(Clone)]
pub(super) struct Pipelines {
    layout: wgpu::BindGroupLayout,
    pipeline: wgpu::RenderPipeline,
}
pub(super) struct PreparedCompose {
    pub texture: NativeImageTexture,
    targets: [wgpu::TextureView; 2],
    pipeline: wgpu::RenderPipeline,
    bindings: Vec<wgpu::BindGroup>,
    _uniforms: wgpu::Buffer,
}
impl PreparedCompose {
    pub fn encode(&self, encoder: &mut wgpu::CommandEncoder, children: &[PreparedNode]) {
        for (index, (binding, child)) in self.bindings.iter().zip(children).enumerate() {
            let mut pass = begin(encoder, &self.targets[index & 1]);
            pass.set_pipeline(&self.pipeline);
            pass.set_bind_group(0, binding, &[(index * 256) as u32]);
            pass.set_bind_group(1, child.binding.bind_group(), &[]);
            pass.draw(0..3, 0..1);
        }
    }
}
async fn pipelines(engine: &mut NativeMaskEngine) -> Result<Pipelines, Error> {
    if let Some(p) = &engine.composition {
        return Ok(p.clone());
    }
    let d = &engine.device;
    let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
    let texture = |binding| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    };
    let layout = d.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("Native sampled-mask compose"),
        entries: &[
            wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                count: None,
            },
            texture(1),
            texture(2),
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
    let child = &engine
        .pipelines
        .as_ref()
        .ok_or(Error::ResourceDomain)?
        .layouts()
        .image
        .mask;
    let pipeline_layout = d.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("Native sampled-mask original slots"),
        bind_group_layouts: &[Some(&layout), Some(child)],
        immediate_size: 0,
    });
    let module = d.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some("Native canonical ClipCompose"),
        source: wgpu::ShaderSource::Wgsl(crate::shaders::CLIP_COMPOSE.into()),
    });
    let pipeline = d.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("Native sampled mask composition"),
        layout: Some(&pipeline_layout),
        vertex: wgpu::VertexState {
            module: &module,
            entry_point: Some("vs_compose"),
            compilation_options: Default::default(),
            buffers: &[],
        },
        fragment: Some(wgpu::FragmentState {
            module: &module,
            entry_point: Some("fs_compose_sampled"),
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
    });
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    let result = Pipelines { layout, pipeline };
    engine.composition = Some(result.clone());
    Ok(result)
}
pub(super) async fn prepare(
    engine: &mut NativeMaskEngine,
    children: &[PreparedNode],
    size: [u32; 2],
) -> Result<PreparedCompose, Error> {
    if !(2..=64).contains(&children.len()) {
        return Err(Error::InvalidInput);
    }
    let p = pipelines(engine).await?;
    let d = &engine.device;
    let scope = d.push_error_scope(wgpu::ErrorFilter::Validation);
    let textures = [
        texture(d, size, wgpu::TextureUsages::RENDER_ATTACHMENT),
        texture(d, size, wgpu::TextureUsages::RENDER_ATTACHMENT),
    ];
    let targets = textures
        .each_ref()
        .map(|t| t.create_view(&Default::default()));
    let mut bytes = allocate(children.len() * 256)?;
    bytes.resize(children.len() * 256, 0);
    for (index, slot) in bytes.chunks_exact_mut(256).enumerate() {
        for (chunk, value) in
            slot.chunks_exact_mut(4)
                .zip([0, u32::from(index == 0), size[0], size[1]])
        {
            chunk.copy_from_slice(&value.to_le_bytes());
        }
    }
    let uniforms = buffer(
        d,
        "Native aligned composition uniforms",
        &bytes,
        wgpu::BufferUsages::UNIFORM,
    );
    let sampler = &engine.samplers.as_ref().ok_or(Error::ResourceDomain)?[1];
    let mut bindings = allocate(children.len())?;
    for (index, child) in children.iter().enumerate() {
        bindings.push(d.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Native ordered mask component"),
            layout: &p.layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(child.texture.view()),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(if index == 0 {
                        child.texture.view()
                    } else {
                        &targets[(index + 1) & 1]
                    }),
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
        }));
    }
    let texture = owned(d, &textures[(children.len() - 1) & 1])?;
    if let Some(error) = scope.pop().await {
        return Err(error.into());
    }
    Ok(PreparedCompose {
        texture,
        targets,
        pipeline: p.pipeline,
        bindings,
        _uniforms: uniforms,
    })
}
