//! Canonical native pipeline families, created only for actual page demand.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Variant {
    Line,
    Mesh,
    Front,
    Back,
    Strip,
    FrontStrip,
    BackStrip,
    Edge,
    OccludedEdge,
}
impl Variant {
    pub(super) fn mesh(m: &NativeThreeDMeshDraw) -> Self {
        if m.topology == MESH_3D_EDGE_LIST {
            Self::Edge
        } else if m.topology == MESH_3D_TRIANGLE_STRIP {
            if m.flags & MESH_3D_FRONT_FACE != 0 {
                Self::FrontStrip
            } else if m.flags & MESH_3D_BACK_FACE != 0 {
                Self::BackStrip
            } else {
                Self::Strip
            }
        } else if m.flags & MESH_3D_FRONT_FACE != 0 {
            Self::Front
        } else if m.flags & MESH_3D_BACK_FACE != 0 {
            Self::Back
        } else {
            Self::Mesh
        }
    }
}
pub(super) fn required(page: &NativeThreeDPage) -> [bool; 9] {
    let mut r = [false; 9];
    if !page.lines.is_empty() {
        r[Variant::Line as usize] = true;
    }
    for m in page.mesh_draws.iter() {
        r[Variant::mesh(m) as usize] = true;
        if m.topology == MESH_3D_EDGE_LIST && m.flags & MESH_3D_EDGE_DISPLAY_OCCLUDED != 0 {
            r[Variant::OccludedEdge as usize] = true;
        }
    }
    r
}
pub(super) const VARIANTS: [Variant; 9] = [
    Variant::Line,
    Variant::Mesh,
    Variant::Front,
    Variant::Back,
    Variant::Strip,
    Variant::FrontStrip,
    Variant::BackStrip,
    Variant::Edge,
    Variant::OccludedEdge,
];
#[derive(Clone)]
pub(super) struct Resources {
    pub storage: wgpu::BindGroupLayout,
    pub material: wgpu::BindGroupLayout,
    pub sampler: wgpu::Sampler,
    pub sentinel: wgpu::Texture,
    pub sentinel_view: wgpu::TextureView,
    pub shader: wgpu::ShaderModule,
    pub line_layout: wgpu::PipelineLayout,
    pub mesh_layout: wgpu::PipelineLayout,
}
impl Resources {
    pub(super) fn new(device: &wgpu::Device) -> Self {
        let entries: [wgpu::BindGroupLayoutEntry; 9] =
            std::array::from_fn(|i| wgpu::BindGroupLayoutEntry {
                binding: i as u32,
                visibility: if i == 0 || i == 2 {
                    wgpu::ShaderStages::VERTEX_FRAGMENT
                } else if (5..=7).contains(&i) {
                    wgpu::ShaderStages::FRAGMENT
                } else {
                    wgpu::ShaderStages::VERTEX
                },
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Storage { read_only: true },
                    has_dynamic_offset: false,
                    min_binding_size: std::num::NonZeroU64::new(MINIMUM_SIZES[i]),
                },
                count: None,
            });
        let storage = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Native3D nine storage buffers"),
            entries: &entries,
        });
        let material = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Native3D material texture"),
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
            ],
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Native3D linear clamp level zero"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::MipmapFilterMode::Nearest,
            lod_min_clamp: 0.0,
            lod_max_clamp: 0.0,
            anisotropy_clamp: 1,
            ..Default::default()
        });
        let sentinel = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native3D material sentinel"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let sentinel_view = sentinel.create_view(&Default::default());
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Original native Native3D.wgsl"),
            source: wgpu::ShaderSource::Wgsl(crate::shaders::NATIVE_3D.into()),
        });
        let line_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Native3D line group zero only"),
            bind_group_layouts: &[Some(&storage)],
            immediate_size: 0,
        });
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Native3D mesh groups"),
            bind_group_layouts: &[Some(&storage), Some(&material)],
            immediate_size: 0,
        });
        Self {
            storage,
            material,
            sampler,
            sentinel,
            sentinel_view,
            shader,
            line_layout,
            mesh_layout,
        }
    }
    pub(super) fn pipeline(
        &self,
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        variant: Variant,
    ) -> wgpu::RenderPipeline {
        let (vertex, fragment) = match variant {
            Variant::Line => ("vs_line_3d", "fs_line_3d"),
            Variant::Edge => ("vs_mesh_edge_3d", "fs_mesh_edge_visible_3d"),
            Variant::OccludedEdge => ("vs_mesh_edge_3d", "fs_mesh_edge_occluded_3d"),
            _ => ("vs_mesh_3d", "fs_mesh_3d"),
        };
        let edge = matches!(variant, Variant::Edge | Variant::OccludedEdge);
        let topology = if matches!(
            variant,
            Variant::Strip | Variant::FrontStrip | Variant::BackStrip
        ) {
            wgpu::PrimitiveTopology::TriangleStrip
        } else {
            wgpu::PrimitiveTopology::TriangleList
        };
        let cull = if matches!(variant, Variant::Front | Variant::FrontStrip) {
            Some(wgpu::Face::Back)
        } else if matches!(variant, Variant::Back | Variant::BackStrip) {
            Some(wgpu::Face::Front)
        } else {
            None
        };
        device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Native3D demanded pipeline"),
            layout: Some(if variant == Variant::Line {
                &self.line_layout
            } else {
                &self.mesh_layout
            }),
            vertex: wgpu::VertexState {
                module: &self.shader,
                entry_point: Some(vertex),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: wgpu::PrimitiveState {
                topology,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: cull,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format: wgpu::TextureFormat::Depth24Plus,
                depth_write_enabled: Some(!edge),
                depth_compare: Some(if variant == Variant::OccludedEdge {
                    wgpu::CompareFunction::Greater
                } else {
                    wgpu::CompareFunction::LessEqual
                }),
                stencil: wgpu::StencilState {
                    front: wgpu::StencilFaceState {
                        compare: wgpu::CompareFunction::Always,
                        fail_op: wgpu::StencilOperation::Keep,
                        depth_fail_op: wgpu::StencilOperation::Keep,
                        pass_op: wgpu::StencilOperation::Keep,
                    },
                    back: wgpu::StencilFaceState {
                        compare: wgpu::CompareFunction::Always,
                        fail_op: wgpu::StencilOperation::Keep,
                        depth_fail_op: wgpu::StencilOperation::Keep,
                        pass_op: wgpu::StencilOperation::Keep,
                    },
                    read_mask: 0xffff_ffff,
                    write_mask: 0xffff_ffff,
                },
                bias: wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState {
                count: 1,
                mask: 0xffff_ffff,
                alpha_to_coverage_enabled: false,
            },
            fragment: Some(wgpu::FragmentState {
                module: &self.shader,
                entry_point: Some(fragment),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        })
    }
}
