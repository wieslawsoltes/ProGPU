//! Original source-rebound DAG lowering, crop order and bounded resource inventory.
use super::*;
use crate::{
    effects::BlurUniforms,
    image::{
        ColorFilterOperation, ColorFilterPlan, ColorTables, ImageColorMatrix,
        NonlinearColorUniforms, OverdrawColorUniforms,
    },
    scene::LayerBlend,
    vector::Matrix4,
};
use std::collections::HashMap;
/// Original execution context and explicit bounded Rust work admission.
#[derive(Clone, Copy, Debug)]
pub struct FilterPlanRequest {
    /// Actual original bound source extent.
    pub source_size: [u32; 2],
    /// Actual mip inventory retained by zero-radius morphology copies.
    pub source_mip_levels: u32,
    /// Original canvas destination extent, independent of source rebindings.
    pub canvas_size: [u32; 2],
    /// Original logical canvas dimensions used by the full-crop bypass policy.
    pub canvas_logical_size: [f32; 2],
    /// Complete original source-to-device filter frame.
    pub transform: FilterTransform,
    /// Maximum distinct source-bound graph visits and emitted original operations,
    /// each independently bounded, including crop/source conversion.
    pub maximum_steps: usize,
    /// Maximum tile/merge capture draw inventory.
    pub maximum_draws: u64,
    /// Maximum private RGBA8-equivalent intermediate storage.
    pub maximum_texture_bytes: u64,
}
impl FilterPlanRequest {
    /// Retain explicit source/canvas extents and frame, with bounded defaults.
    pub fn new(source_size: [u32; 2], canvas_size: [u32; 2], transform: FilterTransform) -> Self {
        Self {
            source_size,
            source_mip_levels: 1,
            canvas_size,
            canvas_logical_size: canvas_size.map(|v| v as f32),
            transform,
            maximum_steps: 16384,
            maximum_draws: 1_048_576,
            maximum_texture_bytes: 512 * 1024 * 1024,
        }
    }
    fn validate(self) -> Result<(), Error> {
        if self.source_size.contains(&0)
            || self.canvas_size.contains(&0)
            || self
                .source_size
                .iter()
                .chain(&self.canvas_size)
                .any(|v| *v > i32::MAX as u32)
            || self.source_mip_levels == 0
            || self.source_mip_levels
                > 32 - self.source_size[0].max(self.source_size[1]).leading_zeros()
            || self
                .canvas_logical_size
                .iter()
                .any(|v| !v.is_finite() || *v <= 0.0)
            || !self.transform.is_finite()
        {
            return Err(Error::InvalidInput);
        }
        if self.maximum_steps == 0
            || self.maximum_steps > u32::MAX as usize
            || self.maximum_draws == 0
        {
            return Err(Error::Capacity);
        }
        Ok(())
    }
}
/// Original lowered GPU operation. Source captures remain typed source owners,
/// not callbacks, fabricated pixels or an identity replacement for missing work.
#[derive(Clone, Debug)]
pub enum ImageFilterOperation {
    /// Actual transparent render target, including original invalid-frame output.
    Transparent,
    /// Original two-axis Gaussian kernels with exact 16-byte records.
    GaussianBlur([BlurUniforms; 2]),
    /// Original two-axis morphology, including true copy semantics when both zero.
    Morphology([MorphologyParams; 2]),
    /// Original complete convolution and retained source coefficients.
    Convolution {
        /// Exact original 48-byte shader record.
        parameters: MatrixConvolutionParams,
        /// Immutable original row-major coefficients.
        kernel: Arc<[f32]>,
    },
    /// Original complete displacement basis; step extent includes canvas policy.
    Displacement(DisplacementMapParams),
    /// Original bounded lens parameters after the pre-crop operation.
    Magnifier(MagnifierParams),
    /// Original six lighting policies, lowered to the exact shared record.
    Lighting(ImageLightingParams),
    /// Original managed image-effect matrix/luma route, preserving neutral math.
    ColorMatrix(ImageColorMatrix),
    /// Original channel tables and straight/premultiplied shader boundary.
    ColorTable(ColorTables),
    /// Original HSLA/high-contrast compute.
    NonlinearColor(NonlinearColorUniforms),
    /// Original overdraw compute.
    Overdraw(OverdrawColorUniforms),
    /// Actual full-size original solid-brush foreground capture.
    Solid([u8; 4]),
    /// Original premultiplied source/background image blend.
    Blend(LayerBlend),
    /// Original four-term arithmetic operation.
    Arithmetic(FilterArithmetic),
    /// Ordered source-over image captures with original independent offsets.
    Composite {
        /// Actual transformed XY offset for each ordered input.
        offsets: Arc<[[f32; 2]]>,
    },
    /// Original full 4x4 device-frame image draw.
    MatrixTransform {
        /// Complete resolved device matrix.
        matrix: Matrix4,
        /// Original independent source reconstruction.
        sampling: FilterSampling,
    },
    /// Original clip rectangle and complete source transform, never its envelope.
    Crop {
        /// Original logical crop endpoints.
        rect: FilterRect,
        /// Actual complete crop frame.
        transform: FilterTransform,
    },
    /// Original ordered tile quads and destination clip.
    Tile {
        /// Original source tile endpoints.
        source: FilterRect,
        /// Independent original destination clip.
        destination: FilterRect,
        /// Start X/Y and exclusive end X/Y; visit Y outer, X inner.
        range: [i32; 4],
    },
    /// Actual image source sampling; destination extent comes from bound source.
    ImageSource(FilterImageData),
    /// Actual original picture recording, target clip and filter frame.
    PictureSource {
        /// Actual retained source recording and clip.
        source: FilterPictureSource,
        /// Complete original source replay frame.
        transform: FilterTransform,
    },
    /// Actual original shader source and frame; unsupported source owners remain
    /// explicit through `requires_source_capture` and renderer admission.
    ShaderSource {
        /// Actual source generation and wrapper inventory.
        source: Arc<FilterShaderSource>,
        /// Original filter frame; procedural sources apply their separate policy.
        transform: FilterTransform,
        /// Retained original ignored dither metadata.
        dither: bool,
    },
    /// Original selected-source-to-sRGB byte table, bypassed when policy is sRGB.
    TransferToSrgb(ImageFilterColorSpace),
    /// Original evaluator rejects this runtime blender; it is never SrcOver.
    UnsupportedRuntimeBlender(Arc<str>),
    /// Original reflected runtime blender with complete retained bindings.
    RuntimeBlender(crate::runtime_effect::RuntimeEffectInstance),
}
impl ImageFilterOperation {
    /// Whether this operation requires original retained source replay rather
    /// than an already bound image or compute kernel.
    pub fn requires_source_capture(&self) -> bool {
        matches!(
            self,
            Self::ImageSource(_) | Self::PictureSource { .. } | Self::ShaderSource { .. }
        )
    }
    /// Original explicitly unsupported runtime owner, without claiming execution.
    pub fn is_explicitly_unsupported(&self) -> bool {
        match self {
            Self::UnsupportedRuntimeBlender(_) | Self::RuntimeBlender(_) => true,
            Self::ShaderSource { source, .. } => !matches!(source.runtime_support(), Ok(true)),
            _ => false,
        }
    }
}
/// One immutable result. Input zero always means the actual caller source;
/// operation n produces result n+1, retaining each rebound generation.
#[derive(Clone, Debug)]
pub struct ImageFilterStep {
    /// Ordered actual source result indices, including null-input source aliases.
    pub inputs: Arc<[u32]>,
    /// Original output extent, independent of logical source bounds.
    pub size: [u32; 2],
    /// Original output mip inventory; ordinary capture/compute outputs have one.
    pub mip_levels: u32,
    /// Original owned operation and parameter bytes.
    pub operation: ImageFilterOperation,
}
/// Bounded preparation inventory; actual device metrics remain the executor's job.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ImageFilterResources {
    /// Actual planned original render/capture passes.
    pub render_passes: u32,
    /// Actual planned canonical compute passes.
    pub compute_passes: u32,
    /// Original zero-radius copy operations.
    pub copy_passes: u32,
    /// Original source replay requests, included in render passes.
    pub source_captures: u32,
    /// Planned full intermediate mip/texel storage at RGBA8-equivalent size.
    pub texture_bytes: u64,
    /// Planned output and original two-pass scratch texture count.
    pub textures: u32,
    /// Original uniform/table parameter payload, excluding target frame bindings.
    pub parameter_bytes: u64,
    /// Original retained convolution coefficient bytes.
    pub kernel_bytes: u64,
    /// Original capture quad/draw count, including all tile and merge inputs.
    pub capture_draws: u64,
}
/// Complete immutable graph, its source frame and exact final result identity.
#[derive(Clone, Debug)]
pub struct ImageFilterPlan {
    root: ImageFilter,
    request: FilterPlanRequest,
    steps: Vec<ImageFilterStep>,
    output: u32,
    resources: ImageFilterResources,
}
impl ImageFilterPlan {
    /// Expand complete source-bound original operations before GPU mutation.
    pub fn new(root: &ImageFilter, request: FilterPlanRequest) -> Result<Self, Error> {
        request.validate()?;
        let mut builder = Builder {
            request,
            steps: Vec::new(),
            memo: HashMap::new(),
            visits: 0,
            resources: ImageFilterResources::default(),
        };
        let output = builder.visit(root, 0, false)?;
        Ok(Self {
            root: root.clone(),
            request,
            steps: builder.steps,
            output,
            resources: builder.resources,
        })
    }
    /// Complete child-before-parent immutable operations.
    pub fn steps(&self) -> &[ImageFilterStep] {
        &self.steps
    }
    /// Actual final source-result identity.
    pub fn output(&self) -> u32 {
        self.output
    }
    /// Exact caller source/canvas frame and work bounds.
    pub fn request(&self) -> FilterPlanRequest {
        self.request
    }
    /// Whole-plan resource/work inventory, without executing pixels.
    pub fn resources(&self) -> ImageFilterResources {
        self.resources
    }
    /// Retained complete original root generation.
    pub fn source(&self) -> &ImageFilter {
        &self.root
    }
    /// Actual final extent, including source-identity fast paths.
    pub fn output_size(&self) -> [u32; 2] {
        if self.output == 0 {
            self.request.source_size
        } else {
            self.steps[self.output as usize - 1].size
        }
    }
    /// Source replay inventory; renderer integration must handle or explicitly reject it.
    pub fn source_captures(&self) -> impl Iterator<Item = &ImageFilterStep> {
        self.steps
            .iter()
            .filter(|s| s.operation.requires_source_capture())
    }
}
struct Builder {
    request: FilterPlanRequest,
    steps: Vec<ImageFilterStep>,
    memo: HashMap<(usize, u32, bool), u32>,
    visits: usize,
    resources: ImageFilterResources,
}
impl Builder {
    fn size(&self, id: u32) -> [u32; 2] {
        if id == 0 {
            self.request.source_size
        } else {
            self.steps[id as usize - 1].size
        }
    }
    fn levels(&self, id: u32) -> u32 {
        if id == 0 {
            self.request.source_mip_levels
        } else {
            self.steps[id as usize - 1].mip_levels
        }
    }
    fn optional(
        &mut self,
        input: Option<&ImageFilter>,
        source: u32,
        preserve: bool,
    ) -> Result<u32, Error> {
        match input {
            Some(i) => self.visit(i, source, preserve),
            None => Ok(source),
        }
    }
    fn maximum_size(&self, inputs: &[u32], initial: [u32; 2]) -> [u32; 2] {
        inputs.iter().fold(initial, |size, input| {
            let n = self.size(*input);
            [size[0].max(n[0]), size[1].max(n[1])]
        })
    }
    fn emit(
        &mut self,
        inputs: &[u32],
        size: [u32; 2],
        levels: u32,
        operation: ImageFilterOperation,
    ) -> Result<u32, Error> {
        if self.steps.len() >= self.request.maximum_steps {
            return Err(Error::Capacity);
        }
        let mut pixels = 0u64;
        let mut extent = size;
        for _ in 0..levels {
            pixels = pixels
                .checked_add(u64::from(extent[0]) * u64::from(extent[1]))
                .ok_or(Error::Capacity)?;
            extent = extent.map(|v| (v / 2).max(1));
        }
        let mut textures = 1u32;
        let mut render = 0u32;
        let mut compute = 0u32;
        let mut copy = 0u32;
        let mut parameters = 0u64;
        let mut kernel = 0u64;
        let mut draws = 0u64;
        match &operation {
            ImageFilterOperation::GaussianBlur(_) => {
                textures = 2;
                compute = 2;
                parameters = 32;
            }
            ImageFilterOperation::Morphology(p) => {
                if p.iter().all(|p| p.radius == 0) {
                    copy = 1;
                } else {
                    textures = 2;
                    compute = 2;
                    parameters = 32;
                }
            }
            ImageFilterOperation::Convolution { kernel: k, .. } => {
                compute = 1;
                parameters = 48;
                kernel = (k.len() as u64).checked_mul(4).ok_or(Error::Capacity)?;
            }
            ImageFilterOperation::Displacement(_) => {
                compute = 1;
                parameters = 32;
            }
            ImageFilterOperation::Magnifier(_) | ImageFilterOperation::Lighting(_) => {
                compute = 1;
                parameters = 80;
            }
            ImageFilterOperation::ColorTable(_) | ImageFilterOperation::TransferToSrgb(_) => {
                compute = 1;
                parameters = 4096;
            }
            ImageFilterOperation::NonlinearColor(_) | ImageFilterOperation::Overdraw(_) => {
                compute = 1;
                parameters = 96;
            }
            ImageFilterOperation::Blend(_) => {
                compute = 1;
                parameters = 16;
            }
            ImageFilterOperation::Arithmetic(_) => {
                compute = 1;
                parameters = 32;
            }
            ImageFilterOperation::Transparent => render = 1,
            ImageFilterOperation::Composite { offsets } => {
                render = 1;
                draws = offsets.len() as u64;
            }
            ImageFilterOperation::Tile { range, .. } => {
                render = 1;
                draws = u64::try_from(i64::from(range[2]) - i64::from(range[0]))
                    .map_err(|_| Error::Capacity)?
                    .checked_mul(
                        u64::try_from(i64::from(range[3]) - i64::from(range[1]))
                            .map_err(|_| Error::Capacity)?,
                    )
                    .ok_or(Error::Capacity)?;
            }
            ImageFilterOperation::UnsupportedRuntimeBlender(_)
            | ImageFilterOperation::RuntimeBlender(_) => {
                textures = 0;
            }
            _ => {
                render = 1;
                draws = 1;
            }
        }
        if matches!(operation, ImageFilterOperation::ColorMatrix(_)) {
            parameters = 288;
        }
        let bytes = pixels
            .checked_mul(4)
            .and_then(|v| v.checked_mul(u64::from(textures)))
            .ok_or(Error::Capacity)?;
        let r = &mut self.resources;
        macro_rules! add {
            ($field:ident,$value:expr) => {
                r.$field = r.$field.checked_add($value).ok_or(Error::Capacity)?;
            };
        }
        add!(texture_bytes, bytes);
        add!(textures, textures);
        add!(render_passes, render);
        add!(compute_passes, compute);
        add!(copy_passes, copy);
        add!(parameter_bytes, parameters);
        add!(kernel_bytes, kernel);
        add!(capture_draws, draws);
        if operation.requires_source_capture() {
            add!(source_captures, 1);
        }
        if r.texture_bytes > self.request.maximum_texture_bytes
            || r.capture_draws > self.request.maximum_draws
        {
            return Err(Error::Capacity);
        }
        self.steps.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.steps.push(ImageFilterStep {
            inputs: Arc::from(inputs),
            size,
            mip_levels: levels,
            operation,
        });
        Ok(self.steps.len() as u32)
    }
    fn crop(&mut self, input: u32, crop: Option<FilterRect>) -> Result<u32, Error> {
        let Some(rect) =
            crop.filter(|r| r.is_valid() && !r.is_full_canvas(self.request.canvas_logical_size))
        else {
            return Ok(input);
        };
        self.emit(
            &[input],
            self.size(input),
            1,
            ImageFilterOperation::Crop {
                rect,
                transform: self.request.transform,
            },
        )
    }
    fn blur(&mut self, input: u32, sigma: [f32; 2]) -> Result<u32, Error> {
        if sigma.iter().all(|v| !v.is_finite() || *v <= 0.01) {
            return Ok(input);
        }
        let parameters = sigma.map(|sigma| {
            let sigma = if sigma.is_finite() {
                sigma.max(0.0)
            } else {
                0.0
            };
            BlurUniforms {
                sigma,
                radius: ((sigma * 3.0).ceil() as i32).clamp(0, 128) as u32,
                kernel_type: 0,
                padding: 0,
            }
        });
        self.emit(
            &[input],
            self.size(input),
            1,
            ImageFilterOperation::GaussianBlur(parameters),
        )
    }
    fn convert(
        &mut self,
        input: u32,
        color: Option<&ImageFilterColorSpace>,
        preserve: bool,
    ) -> Result<u32, Error> {
        let Some(color) = color.filter(|c| !c.is_srgb() && !preserve) else {
            return Ok(input);
        };
        self.emit(
            &[input],
            self.size(input),
            1,
            ImageFilterOperation::TransferToSrgb(color.clone()),
        )
    }
    fn color(&mut self, input: u32, filter: &crate::image::ColorFilter) -> Result<u32, Error> {
        let plan = ColorFilterPlan::new(filter, self.request.maximum_steps)?;
        let mut mapping = vec![input];
        for step in plan.steps() {
            let first = mapping[step.inputs[0] as usize];
            let size = self.size(first);
            let next = match &step.operation {
                ColorFilterOperation::Matrix(m) => {
                    self.emit(&[first], size, 1, ImageFilterOperation::ColorMatrix(*m))?
                }
                ColorFilterOperation::Table(t) => self.emit(
                    &[first],
                    size,
                    1,
                    ImageFilterOperation::ColorTable(t.clone()),
                )?,
                ColorFilterOperation::Nonlinear(p) => {
                    self.emit(&[first], size, 1, ImageFilterOperation::NonlinearColor(*p))?
                }
                ColorFilterOperation::Overdraw(p) => {
                    self.emit(&[first], size, 1, ImageFilterOperation::Overdraw(*p))?
                }
                ColorFilterOperation::Blend { color, mode } => {
                    let solid = self.emit(&[], size, 1, ImageFilterOperation::Solid(*color))?;
                    let inputs = [first, solid];
                    self.emit(
                        &inputs,
                        self.maximum_size(&inputs, [0; 2]),
                        1,
                        ImageFilterOperation::Blend(*mode),
                    )?
                }
                ColorFilterOperation::Lerp { weight } => {
                    let inputs = [first, mapping[step.inputs[1] as usize]];
                    self.emit(
                        &inputs,
                        self.maximum_size(&inputs, [0; 2]),
                        1,
                        ImageFilterOperation::Arithmetic(FilterArithmetic {
                            coefficients: [0.0, *weight, 1.0 - *weight, 0.0],
                            enforce_premultiplied: true,
                        }),
                    )?
                }
            };
            mapping.push(next);
        }
        Ok(mapping[plan.output() as usize])
    }
    fn visit(&mut self, filter: &ImageFilter, source: u32, preserve: bool) -> Result<u32, Error> {
        let key = (filter.identity(), source, preserve);
        if let Some(&id) = self.memo.get(&key) {
            return Ok(id);
        }
        if self.visits >= self.request.maximum_steps {
            return Err(Error::Capacity);
        }
        self.visits += 1;
        use ImageFilterKind as K;
        use ImageFilterOperation as O;
        let transform = self.request.transform;
        let mut result = match filter.kind() {
            K::Compose { outer, inner } => {
                let inner = self.visit(inner, source, preserve)?;
                self.visit(outer, inner, preserve)?
            }
            K::Blur { sigma, .. } => {
                let input = self.optional(filter.input(), source, preserve)?;
                let scale = transform.axis_scales();
                self.blur(input, [sigma[0] * scale[0], sigma[1] * scale[1]])?
            }
            K::DropShadow(shadow) => {
                let input = self.optional(filter.input(), source, preserve)?;
                let size = self.size(input);
                let color = shadow.color.map(|v| f32::from(v) / 255.0);
                let tinted = self.emit(
                    &[input],
                    size,
                    1,
                    O::ColorMatrix(ImageColorMatrix {
                        red: [0.0; 4],
                        green: [0.0; 4],
                        blue: [0.0; 4],
                        alpha: [0.0, 0.0, 0.0, color[3]],
                        offset: [color[0], color[1], color[2], 0.0],
                        luminance_to_alpha: false,
                    }),
                )?;
                let scale = transform.axis_scales();
                let blurred = self.blur(
                    tinted,
                    [shadow.sigma[0] * scale[0], shadow.sigma[1] * scale[1]],
                )?;
                let offset = transform.vector(shadow.offset);
                if shadow.shadow_only {
                    self.emit(
                        &[blurred],
                        size,
                        1,
                        O::Composite {
                            offsets: Arc::from([offset]),
                        },
                    )?
                } else {
                    self.emit(
                        &[blurred, input],
                        size,
                        1,
                        O::Composite {
                            offsets: Arc::from([offset, [0.0; 2]]),
                        },
                    )?
                }
            }
            K::ColorFilter(color) => {
                let input = self.optional(filter.input(), source, preserve)?;
                self.color(input, color)?
            }
            K::Offset(offset) => {
                let input = self.optional(filter.input(), source, preserve)?;
                self.emit(
                    &[input],
                    self.size(input),
                    1,
                    O::Composite {
                        offsets: Arc::from([transform.vector(*offset)]),
                    },
                )?
            }
            K::Dilate(radius) | K::Erode(radius) => {
                let input = self.optional(filter.input(), source, preserve)?;
                let scale = transform.axis_scales();
                let radius = [radius[0] * scale[0], radius[1] * scale[1]]
                    .map(|r| (r.ceil() as i32).clamp(0, 128) as u32);
                let dilate = matches!(filter.kind(), K::Dilate(_));
                let levels = if radius == [0; 2] {
                    self.levels(input)
                } else {
                    1
                };
                self.emit(
                    &[input],
                    self.size(input),
                    levels,
                    O::Morphology([
                        MorphologyParams::new([1, 0], radius[0], dilate),
                        MorphologyParams::new([0, 1], radius[1], dilate),
                    ]),
                )?
            }
            K::Merge(children) => {
                if children.len() as u64 > self.request.maximum_draws {
                    return Err(Error::Capacity);
                }
                let mut inputs = Vec::new();
                inputs
                    .try_reserve_exact(children.len())
                    .map_err(|_| Error::Capacity)?;
                for child in children.iter() {
                    inputs.push(self.optional(child.as_ref(), source, preserve)?);
                }
                let size = self.maximum_size(&inputs, self.size(source));
                self.emit(
                    &inputs,
                    size,
                    1,
                    O::Composite {
                        offsets: Arc::from(vec![[0.0; 2]; inputs.len()]),
                    },
                )?
            }
            K::Arithmetic {
                parameters,
                background,
                foreground,
            } => {
                let inputs = [
                    self.optional(background.as_ref(), source, preserve)?,
                    self.optional(foreground.as_ref(), source, preserve)?,
                ];
                self.emit(
                    &inputs,
                    self.maximum_size(&inputs, [0; 2]),
                    1,
                    O::Arithmetic(*parameters),
                )?
            }
            K::BlendMode {
                blender,
                background,
                foreground,
            } => {
                let inputs = [
                    self.optional(background.as_ref(), source, preserve)?,
                    self.optional(foreground.as_ref(), source, preserve)?,
                ];
                let size = self.maximum_size(&inputs, [0; 2]);
                let operation = match blender {
                    FilterBlender::Mode(m) => O::Blend(*m),
                    FilterBlender::Arithmetic(p) => O::Arithmetic(*p),
                    FilterBlender::UnsupportedRuntime(owner) => {
                        O::UnsupportedRuntimeBlender(owner.clone())
                    }
                    FilterBlender::Runtime(owner) => O::RuntimeBlender(owner.clone()),
                };
                self.emit(&inputs, size, 1, operation)?
            }
            K::DisplacementMap {
                channels,
                scale,
                displacement,
            } => {
                let input = self.optional(filter.input(), source, preserve)?;
                let displacement = self.visit(
                    displacement,
                    source,
                    preserve
                        || displacement.try_has_linear_image_source(self.request.maximum_steps)?,
                )?;
                let size = self.maximum_size(&[input], self.request.canvas_size);
                self.emit(
                    &[input, displacement],
                    size,
                    1,
                    O::Displacement(DisplacementMapParams::new(
                        transform.displacement(*scale),
                        channels[0] as u32,
                        channels[1] as u32,
                    )),
                )?
            }
            K::MatrixConvolution(data) => {
                let input = self.optional(filter.input(), source, preserve)?;
                if data.size.iter().any(|v| *v <= 0 || *v > 64)
                    || data.kernel.len()
                        < (data.size[0] as usize).saturating_mul(data.size[1] as usize)
                {
                    input
                } else {
                    let size = self.size(input);
                    let mut tile = data.tile_mode;
                    let mut origin = [0; 2];
                    let mut extent = size.map(|v| v.min(i32::MAX as u32) as i32);
                    if tile != FilterTileMode::Decal {
                        let (o, e, valid) = transform.convolution_domain(filter.crop_rect(), size);
                        origin = o;
                        extent = e;
                        if !valid {
                            tile = FilterTileMode::Decal;
                        }
                    }
                    let parameters = MatrixConvolutionParams::new(
                        data.size,
                        data.offset,
                        data.gain,
                        data.bias,
                        tile as u32,
                        data.convolve_alpha,
                        origin,
                        extent,
                    );
                    self.emit(
                        &[input],
                        size,
                        1,
                        O::Convolution {
                            parameters,
                            kernel: data.kernel.clone(),
                        },
                    )?
                }
            }
            K::MatrixTransform { matrix, sampling } => {
                let input = self.optional(filter.input(), source, preserve)?;
                if *matrix == Matrix4::IDENTITY {
                    input
                } else if let Some(matrix) = transform.device_matrix(*matrix) {
                    self.emit(
                        &[input],
                        self.size(input),
                        1,
                        O::MatrixTransform {
                            matrix,
                            sampling: *sampling,
                        },
                    )?
                } else {
                    self.emit(&[], self.size(input), 1, O::Transparent)?
                }
            }
            K::Magnifier(data) => {
                let input = self.optional(filter.input(), source, preserve)?;
                let input = self.crop(input, filter.crop_rect())?;
                let size = self.size(input);
                if data.zoom <= 1.0 {
                    input
                } else {
                    let lens = transform.bounds(data.lens);
                    let texture = FilterRect::texture(size);
                    let visible = lens.intersect(texture);
                    let available = filter
                        .crop_rect()
                        .filter(|r| r.is_valid())
                        .map_or(texture, |crop| transform.bounds(crop).intersect(texture));
                    let output = visible.intersect(available);
                    if !lens.is_valid()
                        || !visible.is_valid()
                        || !available.is_valid()
                        || !output.is_valid()
                    {
                        self.emit(&[], size, 1, O::Transparent)?
                    } else {
                        let scales = transform.axis_scales();
                        let inset = [data.inset * scales[0], data.inset * scales[1]].map(|v| {
                            if v > 0.0 && v.is_finite() {
                                1.0 / v
                            } else {
                                0.0
                            }
                        });
                        self.emit(
                            &[input],
                            size,
                            1,
                            O::Magnifier(MagnifierParams::new(
                                lens.0,
                                output.0,
                                magnifier_zoom(lens, visible, available, data.zoom),
                                inset,
                                data.sampling.magnifier_mode(),
                                if data.sampling.mode == crate::image::ImageSampling::Cubic {
                                    data.sampling.cubic
                                } else {
                                    [0.0; 2]
                                },
                            )),
                        )?
                    }
                }
            }
            K::DistantLitDiffuse(light)
            | K::DistantLitSpecular(light)
            | K::PointLitDiffuse(light)
            | K::PointLitSpecular(light)
            | K::SpotLitDiffuse(light)
            | K::SpotLitSpecular(light) => {
                let input = self.optional(filter.input(), source, preserve)?;
                let specular = matches!(
                    filter.kind(),
                    K::DistantLitSpecular(_) | K::PointLitSpecular(_) | K::SpotLitSpecular(_)
                );
                self.emit(
                    &[input],
                    self.size(input),
                    1,
                    O::Lighting(light.parameters(transform, specular)),
                )?
            }
            K::Image(image) => {
                let output = self.emit(&[], self.size(source), 1, O::ImageSource(image.clone()))?;
                self.convert(output, image.source.color_space.as_ref(), preserve)?
            }
            K::Picture(picture) => {
                let output = self.emit(
                    &[],
                    self.size(source),
                    1,
                    O::PictureSource {
                        source: picture.clone(),
                        transform,
                    },
                )?;
                self.convert(
                    output,
                    picture
                        .try_uniform_color_space(self.request.maximum_steps)?
                        .as_ref(),
                    preserve,
                )?
            }
            K::Shader {
                source: shader,
                dither,
            } => match shader {
                None => self.emit(&[], self.size(source), 1, O::Transparent)?,
                Some(shader) => self.emit(
                    &[],
                    self.size(source),
                    1,
                    O::ShaderSource {
                        source: shader.clone(),
                        transform,
                        dither: *dither,
                    },
                )?,
            },
            K::Tile {
                source: rect,
                destination,
            } => {
                let input = self.optional(filter.input(), source, preserve)?;
                if rect.width() <= 0.0
                    || rect.height() <= 0.0
                    || destination.width() <= 0.0
                    || destination.height() <= 0.0
                {
                    input
                } else {
                    let range = [
                        ((destination.0[0] - rect.0[0]) / rect.width()).floor(),
                        ((destination.0[1] - rect.0[1]) / rect.height()).floor(),
                        ((destination.0[2] - rect.0[0]) / rect.width()).ceil(),
                        ((destination.0[3] - rect.0[1]) / rect.height()).ceil(),
                    ];
                    if range
                        .iter()
                        .any(|v| !v.is_finite() || *v < i32::MIN as f32 || *v >= i32::MAX as f32)
                    {
                        return Err(Error::Capacity);
                    }
                    self.emit(
                        &[input],
                        self.size(input),
                        1,
                        O::Tile {
                            source: *rect,
                            destination: *destination,
                            range: range.map(|v| v as i32),
                        },
                    )?
                }
            }
        };
        if !matches!(filter.kind(), K::Magnifier(_)) {
            result = self.crop(result, filter.crop_rect())?;
        }
        self.memo.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.memo.insert(key, result);
        Ok(result)
    }
}
