use crate::{Error, geometry::VectorVertex, shaders};
pub(super) struct Pipeline {
    pub pipeline: wgpu::RenderPipeline,
    pub layout: wgpu::BindGroupLayout,
}
impl Pipeline {
    pub async fn new(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        sample_count: u32,
    ) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original static Hatch.wgsl"),
            source: wgpu::ShaderSource::Wgsl(shaders::HATCH.into()),
        });
        let entry = |binding, storage, size| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: if storage {
                wgpu::ShaderStages::FRAGMENT
            } else {
                wgpu::ShaderStages::VERTEX
            },
            ty: wgpu::BindingType::Buffer {
                ty: if storage {
                    wgpu::BufferBindingType::Storage { read_only: true }
                } else {
                    wgpu::BufferBindingType::Uniform
                },
                has_dynamic_offset: false,
                min_binding_size: std::num::NonZeroU64::new(size),
            },
            count: None,
        };
        let layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original static Hatch five bindings"),
            entries: &[
                entry(0, false, 192),
                entry(1, true, 256),
                entry(2, true, 32),
                entry(3, true, 48),
                entry(4, true, 32),
            ],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU static Hatch pipeline layout"),
            bind_group_layouts: &[Some(&layout)],
            immediate_size: 0,
        });
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original static Hatch straight SrcOver"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: size_of::<VectorVertex>() as u64,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &attributes,
                })],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState {
                count: sample_count,
                ..Default::default()
            },
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self { pipeline, layout })
    }
}
