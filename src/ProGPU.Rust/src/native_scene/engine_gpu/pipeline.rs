use super::*;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum NativeMaskMode {
    None,
    Sampled,
    Chain,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum NativeWriteChannels {
    All,
    Rgb,
    Alpha,
}
/// Independent original group-blend axes; unsupported combinations reject
/// before compiling a shader or inserting a pipeline in the cache.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct NativeGroupPipeline {
    pub blend_mode: u32,
    pub masked: bool,
    pub coverage_only: bool,
    pub channels: NativeWriteChannels,
    pub linear_byte_opacity: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum NativeRenderPipeline {
    SolidRectangle,
    Vector(NativeMaskMode),
    BrushOpacityMask,
    Text(NativeMaskMode),
    GlyphPaint {
        mask: NativeMaskMode,
        premultiplied: bool,
    },
    Image(NativeMaskMode),
    StraightMaskedImage,
    ColorMatrix(NativeMaskMode),
    ImageEffect {
        mask_chain: bool,
    },
    Group(NativeGroupPipeline),
}
/// Device/target-owned cache. Shader and pipeline candidates publish only after
/// the runtime's validation scope succeeds; shader compiler defaults are intact.
pub struct NativePipelineCache {
    device: wgpu::Device,
    target_format: wgpu::TextureFormat,
    layouts: NativeRenderLayouts,
    shaders: [Option<wgpu::ShaderModule>; 5],
    pipelines: HashMap<NativeRenderPipeline, wgpu::RenderPipeline>,
}
impl NativePipelineCache {
    pub async fn new(
        device: &wgpu::Device,
        target_format: wgpu::TextureFormat,
    ) -> Result<Self, Error> {
        if !matches!(
            target_format,
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::R8Unorm
        ) {
            return Err(Error::InvalidInput);
        }
        let limits = device.limits();
        if limits.max_bind_groups < 4
            || limits.max_vertex_attributes < 8
            || limits.max_vertex_buffer_array_stride < 96
            || limits.max_uniform_buffer_binding_size < 288
            || limits.max_storage_buffer_binding_size < 256
        {
            return Err(Error::Capacity);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let layouts = NativeRenderLayouts::new(device);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            target_format,
            layouts,
            shaders: Default::default(),
            pipelines: HashMap::new(),
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn target_format(&self) -> wgpu::TextureFormat {
        self.target_format
    }
    pub fn layouts(&self) -> &NativeRenderLayouts {
        &self.layouts
    }
    pub fn pipeline_count(&self) -> usize {
        self.pipelines.len()
    }
    /// Returns the exact demand-created pipeline and whether it was retained.
    pub async fn prepare(
        &mut self,
        key: NativeRenderPipeline,
    ) -> Result<(wgpu::RenderPipeline, bool), Error> {
        if let Some(pipeline) = self.pipelines.get(&key) {
            return Ok((pipeline.clone(), true));
        }
        let specification = Specification::new(key, self.target_format)?;
        if matches!(key, NativeRenderPipeline::GlyphPaint { .. }) {
            let limits = self.device.limits();
            if limits.max_vertex_attributes < 9
                || limits.max_storage_buffers_per_shader_stage < 3
                || limits.max_sampled_textures_per_shader_stage < 4
                || limits.max_samplers_per_shader_stage < 3
            {
                return Err(Error::Capacity);
            }
        }
        self.pipelines.try_reserve(1).map_err(|_| Error::Capacity)?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let shader = self.shaders[specification.shader]
            .clone()
            .unwrap_or_else(|| {
                let source = [
                    shaders::VECTOR,
                    shaders::TEXT,
                    shaders::TEXTURE,
                    shaders::IMAGE_EFFECT,
                    shaders::HINTED_GLYPH_PAINT,
                ][specification.shader];
                self.device
                    .create_shader_module(wgpu::ShaderModuleDescriptor {
                        label: Some("Native original owned render WGSL"),
                        source: wgpu::ShaderSource::Wgsl(source.into()),
                    })
            });
        let pipeline = specification.create(&self.device, &self.layouts, &shader);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        self.shaders[specification.shader] = Some(shader);
        self.pipelines.insert(key, pipeline.clone());
        Ok((pipeline, false))
    }
}

#[derive(Clone, Copy)]
enum Layout {
    Frame,
    Vector,
    Text,
    Texture,
    TextAtlas,
    Mask,
    Chain,
    Effect,
    EffectTexture,
    Paint,
    Empty,
}
struct Specification {
    shader: usize,
    vertex: &'static str,
    fragment: &'static str,
    bindings: [Layout; 4],
    binding_count: usize,
    glyphs: bool,
    painted: bool,
    format: wgpu::TextureFormat,
    blend: Option<wgpu::BlendState>,
    writes: wgpu::ColorWrites,
}
impl Specification {
    fn new(key: NativeRenderPipeline, format: wgpu::TextureFormat) -> Result<Self, Error> {
        use Layout::*;
        use NativeMaskMode as MaskMode;
        use NativeRenderPipeline as Pipeline;
        let mut s = Self {
            shader: 0,
            vertex: "vs_main",
            fragment: "fs_main_unmasked",
            bindings: [Vector, Texture, Mask, Mask],
            binding_count: 2,
            glyphs: false,
            painted: false,
            format,
            blend: Some(wgpu::BlendState::ALPHA_BLENDING),
            writes: wgpu::ColorWrites::ALL,
        };
        match key {
            Pipeline::SolidRectangle => {
                s.vertex = "vs_solid_rect";
                s.fragment = "fs_solid_rect_main_unmasked";
                s.bindings[0] = Frame;
                s.binding_count = 1;
            }
            Pipeline::Vector(mask) => match mask {
                MaskMode::None => {}
                MaskMode::Sampled => {
                    s.binding_count = 3;
                    s.fragment = "fs_main_premultiplied";
                    s.blend = Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING);
                }
                MaskMode::Chain => {
                    s.binding_count = 3;
                    s.bindings[2] = Chain;
                    s.fragment = "fs_main_chain";
                }
            },
            Pipeline::BrushOpacityMask => {
                s.fragment = "fs_mask_target_space";
                s.format = wgpu::TextureFormat::R8Unorm;
                s.blend = None;
            }
            Pipeline::Text(mask) => {
                s.shader = 1;
                s.glyphs = true;
                s.bindings = [Text, TextAtlas, Mask, Mask];
                match mask {
                    MaskMode::None => {}
                    MaskMode::Sampled => {
                        s.binding_count = 3;
                        s.fragment = "fs_main";
                    }
                    MaskMode::Chain => {
                        s.binding_count = 3;
                        s.bindings[2] = Chain;
                        s.fragment = "fs_main_chain";
                    }
                }
            }
            Pipeline::GlyphPaint {
                mask,
                premultiplied,
            } => {
                s.shader = 4;
                s.glyphs = true;
                s.painted = true;
                s.bindings = [
                    Paint,
                    TextAtlas,
                    match mask {
                        MaskMode::None => Empty,
                        MaskMode::Sampled => Mask,
                        MaskMode::Chain => Chain,
                    },
                    Texture,
                ];
                s.binding_count = 4;
                let alpha_mask = format == wgpu::TextureFormat::R8Unorm;
                s.blend = Some(if alpha_mask || premultiplied {
                    wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING
                } else {
                    wgpu::BlendState::ALPHA_BLENDING
                });
                s.fragment = match (alpha_mask, premultiplied, mask) {
                    (true, _, MaskMode::None) => "fs_mask_unmasked",
                    (true, _, MaskMode::Sampled) => "fs_mask",
                    (true, _, MaskMode::Chain) => "fs_mask_chain",
                    (_, true, MaskMode::None) => "fs_main_premultiplied_unmasked",
                    (_, true, MaskMode::Sampled) => "fs_main_premultiplied",
                    (_, true, MaskMode::Chain) => "fs_main_mask_chain_premultiplied",
                    (_, false, MaskMode::None) => "fs_main_unmasked",
                    (_, false, MaskMode::Sampled) => "fs_main",
                    (_, false, MaskMode::Chain) => "fs_main_mask_chain",
                };
            }
            Pipeline::Image(mask) => {
                s.shader = 2;
                s.bindings[0] = Frame;
                s.blend = Some(wgpu::BlendState::PREMULTIPLIED_ALPHA_BLENDING);
                s.fragment = match mask {
                    MaskMode::None => "fs_retained_image_unmasked",
                    MaskMode::Sampled => {
                        s.binding_count = 3;
                        "fs_retained_image"
                    }
                    MaskMode::Chain => {
                        s.binding_count = 3;
                        s.bindings[2] = Chain;
                        "fs_retained_image_chain"
                    }
                };
            }
            Pipeline::StraightMaskedImage => {
                s.shader = 2;
                s.bindings[0] = Frame;
                s.binding_count = 3;
                s.fragment = "fs_main";
            }
            Pipeline::ColorMatrix(mask) => {
                s.shader = 2;
                s.bindings[0] = Frame;
                // Group 2 is the matrix for the unmasked entry; group 3 is the
                // matrix for masked/chain entries. Keep the original schemas.
                s.fragment = match mask {
                    MaskMode::None => {
                        s.binding_count = 3;
                        "fs_main_color_matrix_unmasked"
                    }
                    MaskMode::Sampled => {
                        s.binding_count = 4;
                        "fs_main_color_matrix"
                    }
                    MaskMode::Chain => {
                        s.binding_count = 4;
                        s.bindings[2] = Chain;
                        "fs_main_color_matrix_chain"
                    }
                };
            }
            Pipeline::ImageEffect { mask_chain } => {
                s.shader = 3;
                s.bindings = [
                    Frame,
                    Effect,
                    EffectTexture,
                    if mask_chain { Chain } else { Mask },
                ];
                s.binding_count = 4;
                s.fragment = if mask_chain {
                    "fs_main_chain"
                } else {
                    "fs_main"
                };
            }
            Pipeline::Group(group) => {
                s.shader = 2;
                s.bindings[0] = Frame;
                s.binding_count = if group.masked { 3 } else { 2 };
                let NativeGroupPipeline {
                    blend_mode,
                    coverage_only,
                    channels,
                    linear_byte_opacity,
                    masked,
                } = group;
                if linear_byte_opacity
                    && (coverage_only
                        || blend_mode != BLEND_SRC_OVER
                        || channels == NativeWriteChannels::Alpha
                        || !matches!(
                            format,
                            wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Bgra8Unorm
                        ))
                    || coverage_only
                        && (blend_mode != BLEND_SRC || channels == NativeWriteChannels::Rgb)
                    || !coverage_only && channels == NativeWriteChannels::Alpha
                {
                    return Err(Error::InvalidInput);
                }
                s.blend = Some(fixed_group_blend(blend_mode).ok_or(Error::InvalidInput)?);
                let floating = coverage_only && channels == NativeWriteChannels::All;
                s.writes = if floating {
                    s.format = wgpu::TextureFormat::R32Float;
                    s.blend = None;
                    wgpu::ColorWrites::RED
                } else {
                    match channels {
                        NativeWriteChannels::All => wgpu::ColorWrites::ALL,
                        NativeWriteChannels::Rgb => {
                            wgpu::ColorWrites::RED
                                | wgpu::ColorWrites::GREEN
                                | wgpu::ColorWrites::BLUE
                        }
                        NativeWriteChannels::Alpha => wgpu::ColorWrites::ALPHA,
                    }
                };
                s.fragment = if coverage_only {
                    if masked {
                        "fs_layer_coverage"
                    } else {
                        "fs_layer_coverage_unmasked"
                    }
                } else if linear_byte_opacity {
                    if masked {
                        "fs_layer_linear_unorm"
                    } else {
                        "fs_layer_linear_unorm_unmasked"
                    }
                } else if masked {
                    "fs_main"
                } else {
                    "fs_main_unmasked"
                };
            }
        }
        Ok(s)
    }
    fn create(
        &self,
        device: &wgpu::Device,
        layouts: &NativeRenderLayouts,
        shader: &wgpu::ShaderModule,
    ) -> wgpu::RenderPipeline {
        let binding = |l| match l {
            Layout::Frame => &layouts.frame,
            Layout::Vector => &layouts.vector,
            Layout::Text => &layouts.text,
            Layout::Texture => &layouts.image.texture,
            Layout::TextAtlas => &layouts.text_atlas,
            Layout::Mask => &layouts.image.mask,
            Layout::Chain => &layouts.mask_chain,
            Layout::Effect => &layouts.image.effect_uniform,
            Layout::EffectTexture => &layouts.image.effect_texture,
            Layout::Paint => &layouts.glyph_paint,
            Layout::Empty => &layouts.empty,
        };
        let bindings = self.bindings.map(|l| Some(binding(l)));
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Native original render pipeline layout"),
            bind_group_layouts: &bindings[..self.binding_count],
            immediate_size: 0,
        });
        const VECTOR: [wgpu::VertexAttribute; 8] = wgpu::vertex_attr_array![
            0=>Float32x2, 1=>Float32x4, 2=>Float32x2, 3=>Float32,
            4=>Float32x2, 5=>Float32, 6=>Float32, 7=>Float32];
        const GLYPH: [wgpu::VertexAttribute; 8] = wgpu::vertex_attr_array![
            0=>Float32x2, 1=>Float32x2, 2=>Float32x2, 3=>Float32x4,
            4=>Float32x4, 5=>Float32x4, 6=>Float32x4, 7=>Float32];
        const PAINT: [wgpu::VertexAttribute; 9] = wgpu::vertex_attr_array![
            0=>Float32x2, 1=>Float32x2, 2=>Float32x2, 3=>Float32x4,
            4=>Float32x4, 5=>Float32x4, 6=>Float32x4, 7=>Float32, 8=>Uint32];
        device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Native original demand render pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: shader,
                entry_point: Some(self.vertex),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: if self.glyphs { 96 } else { 56 },
                    step_mode: if self.glyphs {
                        wgpu::VertexStepMode::Instance
                    } else {
                        wgpu::VertexStepMode::Vertex
                    },
                    attributes: if self.painted {
                        &PAINT
                    } else if self.glyphs {
                        &GLYPH
                    } else {
                        &VECTOR
                    },
                })],
            },
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: None,
                ..Default::default()
            },
            depth_stencil: None,
            multisample: wgpu::MultisampleState {
                count: 1,
                mask: u32::MAX as u64,
                alpha_to_coverage_enabled: false,
            },
            fragment: Some(wgpu::FragmentState {
                module: shader,
                entry_point: Some(self.fragment),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: self.format,
                    blend: self.blend,
                    write_mask: self.writes,
                })],
            }),
            multiview_mask: None,
            cache: None,
        })
    }
}
pub fn is_advanced_group_blend(mode: u32) -> bool {
    (BLEND_MULTIPLY..=BLEND_EXCLUSION).contains(&mode)
        || (BLEND_OVERLAY..=BLEND_LUMINOSITY).contains(&mode)
}
fn fixed_group_blend(mode: u32) -> Option<wgpu::BlendState> {
    use wgpu::BlendFactor::*;
    let (src, dst) = match mode {
        BLEND_SRC_OVER => (One, OneMinusSrcAlpha),
        BLEND_SRC => (One, Zero),
        BLEND_DST => (Zero, One),
        BLEND_SRC_IN => (DstAlpha, Zero),
        BLEND_DST_IN => (Zero, SrcAlpha),
        BLEND_SRC_OUT => (OneMinusDstAlpha, Zero),
        BLEND_DST_OUT => (Zero, OneMinusSrcAlpha),
        BLEND_SRC_ATOP => (DstAlpha, OneMinusSrcAlpha),
        BLEND_DST_ATOP => (OneMinusDstAlpha, SrcAlpha),
        BLEND_XOR => (OneMinusDstAlpha, OneMinusSrcAlpha),
        BLEND_DST_OVER => (OneMinusDstAlpha, One),
        BLEND_PLUS => (One, One),
        BLEND_CLEAR => (Zero, Zero),
        BLEND_MODULATE => (Dst, Zero),
        _ => return None,
    };
    let color = wgpu::BlendComponent {
        src_factor: src,
        dst_factor: dst,
        operation: wgpu::BlendOperation::Add,
    };
    let alpha = wgpu::BlendComponent {
        src_factor: if mode == BLEND_MODULATE {
            DstAlpha
        } else {
            src
        },
        ..color
    };
    Some(wgpu::BlendState { color, alpha })
}
