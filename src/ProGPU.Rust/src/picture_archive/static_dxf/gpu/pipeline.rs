use super::{Error, PipelineKey};
use crate::{geometry::VectorVertex, shaders};
pub(super) struct Pipeline {
    pub pipeline: wgpu::RenderPipeline,
    pub frame: wgpu::BindGroupLayout,
    pub coverage: Option<wgpu::BindGroupLayout>,
    pub mask: Option<wgpu::BindGroupLayout>,
}
fn buffer(binding: u32, storage: bool, size: u64) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
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
    }
}
fn sampler() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 0,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}
fn texture() -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding: 1,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type: wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled: false,
        },
        count: None,
    }
}
impl Pipeline {
    pub async fn new(device: &wgpu::Device, key: PipelineKey) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU original static DXF shader"),
            source: wgpu::ShaderSource::Wgsl(
                if key.hatch {
                    shaders::HATCH
                } else {
                    shaders::VECTOR
                }
                .into(),
            ),
        });
        let entries = if key.hatch {
            vec![
                buffer(0, false, 192),
                buffer(1, true, 256),
                buffer(2, true, 32),
                buffer(3, true, 48),
                buffer(4, true, 32),
            ]
        } else {
            vec![
                buffer(0, false, 224),
                buffer(1, true, 256),
                buffer(2, true, 32),
            ]
        };
        let frame = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU original static frame/material layout"),
            entries: &entries,
        });
        let coverage = (!key.hatch).then(|| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("ProGPU original static path atlas layout"),
                entries: &[sampler(), texture()],
            })
        });
        let mask = key.masked.then(|| {
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("ProGPU original static single resolved mask layout"),
                entries: &[sampler(), texture(), buffer(2, false, 96)],
            })
        });
        let mut layouts = vec![Some(&frame)];
        if let Some(coverage) = &coverage {
            layouts.push(Some(coverage));
        }
        if let Some(mask) = &mask {
            layouts.push(Some(mask));
        }
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU original static mixed pipeline layout"),
            bind_group_layouts: &layouts,
            immediate_size: 0,
        });
        let premultiplied = !key.hatch && key.blend.requires_premultiplied();
        let entry = if key.hatch {
            "fs_main"
        } else {
            match (key.masked, premultiplied) {
                (false, false) => "fs_main_unmasked",
                (false, true) => "fs_main_premultiplied_unmasked",
                (true, false) => "fs_main",
                (true, true) => "fs_main_premultiplied",
            }
        };
        let attributes = wgpu::vertex_attr_array![0=>Float32x2,1=>Float32x4,2=>Float32x2,3=>Float32,4=>Float32x2,5=>Float32,6=>Float32,7=>Float32];
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original mixed static DXF pipeline"),
            layout: Some(&layout),
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
            primitive: Default::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState {
                count: key.samples,
                ..Default::default()
            },
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some(entry),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: key.format,
                    blend: Some(if key.hatch {
                        wgpu::BlendState::ALPHA_BLENDING
                    } else {
                        key.blend.straight_draw_state()?
                    }),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            pipeline,
            frame,
            coverage,
            mask,
        })
    }
}
