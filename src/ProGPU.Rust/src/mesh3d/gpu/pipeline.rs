//! Original Mesh3D pipeline demand, layouts, depth and fragment entry selection.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(super) enum Variant {
    Front,
    Back,
    UnfilterableFront,
    UnfilterableBack,
    Wireframe,
    VisibleEdges,
    OccludedEdges,
}
impl Variant {
    pub(super) fn family(self) -> u8 {
        if self == Self::Wireframe {
            1
        } else if self.edge() {
            2
        } else {
            0
        }
    }
    pub(super) fn edge(self) -> bool {
        matches!(self, Self::VisibleEdges | Self::OccludedEdges)
    }
    pub(super) fn unfilterable(self) -> bool {
        matches!(self, Self::UnfilterableFront | Self::UnfilterableBack)
    }
}
pub(super) struct Layouts {
    pub solid: wgpu::BindGroupLayout,
    pub wire: wgpu::BindGroupLayout,
    pub texture: wgpu::BindGroupLayout,
    pub unfilterable: wgpu::BindGroupLayout,
}
impl Layouts {
    pub async fn new(device: &wgpu::Device) -> Result<Self, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let buffer = |binding, visibility, storage, size| wgpu::BindGroupLayoutEntry {
            binding,
            visibility,
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
        let entries = [
            buffer(0, wgpu::ShaderStages::VERTEX_FRAGMENT, false, 224),
            buffer(1, wgpu::ShaderStages::VERTEX_FRAGMENT, true, 560),
            buffer(2, wgpu::ShaderStages::FRAGMENT, true, 80),
            buffer(3, wgpu::ShaderStages::FRAGMENT, true, 32),
        ];
        let solid = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU Mesh3D material layout"),
            entries: &entries,
        });
        let wire = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU Mesh3D wireframe layout"),
            entries: &entries[..3],
        });
        let texture_entry = |binding, filterable| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type: wgpu::TextureSampleType::Float { filterable },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled: false,
            },
            count: None,
        };
        let texture = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU Mesh3D sampled material layout"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
                texture_entry(1, true),
                texture_entry(2, true),
            ],
        });
        let unfilterable = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("ProGPU Mesh3D integer-load material layout"),
            entries: &[texture_entry(3, false), texture_entry(4, false)],
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            solid,
            wire,
            texture,
            unfilterable,
        })
    }
    pub async fn pipeline(
        &self,
        device: &wgpu::Device,
        variant: Variant,
        samples: u32,
        shader: &wgpu::ShaderModule,
    ) -> Result<wgpu::RenderPipeline, Error> {
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let bindings = if variant == Variant::Wireframe {
            vec![Some(&self.wire)]
        } else if variant.edge() {
            vec![Some(&self.solid)]
        } else {
            vec![
                Some(&self.solid),
                Some(if variant.unfilterable() {
                    &self.unfilterable
                } else {
                    &self.texture
                }),
            ]
        };
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ProGPU Mesh3D pipeline layout"),
            bind_group_layouts: &bindings,
            immediate_size: 0,
        });
        let vertex_attributes = wgpu::vertex_attr_array![0=>Float32x3,1=>Float32x3,2=>Float32x2];
        let record_attributes = wgpu::vertex_attr_array![3=>Uint32];
        let edge_attributes = wgpu::vertex_attr_array![0=>Float32x4,1=>Float32x4,2=>Float32x4,3=>Float32x4,4=>Uint32,5=>Uint32];
        let buffers = if variant.edge() {
            vec![Some(wgpu::VertexBufferLayout {
                array_stride: 80,
                step_mode: wgpu::VertexStepMode::Instance,
                attributes: &edge_attributes,
            })]
        } else {
            vec![
                Some(wgpu::VertexBufferLayout {
                    array_stride: 32,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &vertex_attributes,
                }),
                Some(wgpu::VertexBufferLayout {
                    array_stride: 4,
                    step_mode: wgpu::VertexStepMode::Instance,
                    attributes: &record_attributes,
                }),
            ]
        };
        let cull_mode = match variant {
            Variant::Front | Variant::UnfilterableFront => Some(wgpu::Face::Back),
            Variant::Back | Variant::UnfilterableBack => Some(wgpu::Face::Front),
            _ => None,
        };
        let fragment = match variant {
            Variant::VisibleEdges => "fs_visible",
            Variant::OccludedEdges => "fs_occluded",
            Variant::UnfilterableFront | Variant::UnfilterableBack => "fs_unfilterable",
            _ => "fs_main",
        };
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU original Mesh3D demand pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &buffers,
            },
            primitive: wgpu::PrimitiveState {
                cull_mode,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: wgpu::TextureFormat::Depth24PlusStencil8,
                depth_write_enabled: Some(!variant.edge()),
                depth_compare: Some(if variant == Variant::OccludedEdges {
                    wgpu::CompareFunction::Greater
                } else {
                    wgpu::CompareFunction::LessEqual
                }),
                stencil: wgpu::StencilState {
                    read_mask: 0xff,
                    write_mask: 0xff,
                    ..Default::default()
                },
                bias: Default::default(),
            }),
            multisample: wgpu::MultisampleState {
                count: samples,
                ..Default::default()
            },
            fragment: Some(wgpu::FragmentState {
                module: shader,
                entry_point: Some(fragment),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::Rgba8Unorm,
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
        Ok(pipeline)
    }
}
