//! Original immutable filter factories and actual source-owner descriptors.
use super::*;
use crate::{
    image::{ColorFilter, ImageSampling, ImageSource},
    scene::{LayerBlend, Scene},
    vector::{Brush, Matrix4},
};
/// Original clamp/repeat/mirror/decal policies, retained even by factories whose
/// original evaluator ignores that field.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FilterTileMode {
    /// Extend the nearest domain edge.
    #[default]
    Clamp = 0,
    /// Repeat the original domain.
    Repeat = 1,
    /// Reflect alternate repetitions of the original domain.
    Mirror = 2,
    /// Transparent outside the original image domain.
    Decal = 3,
}
/// Original independent color-channel selectors.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FilterColorChannel {
    /// Straight red component.
    #[default]
    Red = 0,
    /// Straight green component.
    Green = 1,
    /// Straight blue component.
    Blue = 2,
    /// Original alpha component.
    Alpha = 3,
}
/// Original sampling record, retaining cubic coefficients and anisotropy.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FilterSampling {
    /// Actual source reconstruction policy.
    pub mode: ImageSampling,
    /// Original cubic B/C, used only when cubic is selected.
    pub cubic: [f32; 2],
    /// Original anisotropy; zero means it was not requested.
    pub anisotropy: i32,
}
impl Default for FilterSampling {
    fn default() -> Self {
        Self::NEAREST
    }
}
impl FilterSampling {
    /// Original default SKSamplingOptions.
    pub const NEAREST: Self = Self {
        mode: ImageSampling::Nearest,
        cubic: [0.0; 2],
        anisotropy: 0,
    };
    /// Original explicit linear interpolation.
    pub const LINEAR: Self = Self {
        mode: ImageSampling::Linear,
        cubic: [0.0; 2],
        anisotropy: 0,
    };
    /// Original no-sampling-argument Image factory default.
    pub const MITCHELL: Self = Self {
        mode: ImageSampling::Cubic,
        cubic: [1.0 / 3.0, 1.0 / 3.0],
        anisotropy: 0,
    };
    /// Original magnifier nearest/linear/cubic lowering; mip/anisotropic modes
    /// select its independent linear kernel exactly as the source does.
    pub fn magnifier_mode(self) -> u32 {
        if self.mode == ImageSampling::Cubic {
            2
        } else if self.mode == ImageSampling::Nearest && self.anisotropy <= 0 {
            0
        } else {
            1
        }
    }
}
/// Original immutable selected image and its independent transfer metadata.
#[derive(Clone, Debug)]
pub struct FilterImageSource {
    /// Actual immutable pixels or retained device-bound texture generation.
    pub image: ImageSource,
    /// Actual selected source color-space transfer; None is unavailable/default.
    pub color_space: Option<ImageFilterColorSpace>,
}
/// Exact source-picture color inventory used by original transfer detection.
#[derive(Clone, Debug)]
pub enum PictureColorEntry {
    /// Actual retained image transfer, with None rejecting uniform inference.
    Image(Option<ImageFilterColorSpace>),
    /// Original nested picture's complete inventory.
    Picture(Arc<[PictureColorEntry]>),
    /// Original clip/opacity/blend push or pop, which does not select a transfer.
    Scope,
    /// Any other original paint command prevents homogeneous-image inference.
    Other,
}
/// Owned original picture recording and explicit source metadata. This is not a
/// substitute pre-rendered bitmap; the renderer must replay the actual scene.
#[derive(Clone, Debug)]
pub struct FilterPictureSource {
    /// Actual immutable retained recording.
    pub scene: Arc<Scene>,
    /// Original source target rectangle, independent of filter crop.
    pub target: FilterRect,
    /// Matching original command metadata inventory; an absent inventory rejects
    /// color-space inference rather than inventing a transfer.
    pub color_inventory: Arc<[PictureColorEntry]>,
}
impl FilterPictureSource {
    /// Original all-image homogeneous transfer detection. Scope-only/empty,
    /// missing-transfer, other-paint and conflicting nested inventories reject.
    pub fn uniform_color_space(&self) -> Option<ImageFilterColorSpace> {
        self.try_uniform_color_space(16_384).ok().flatten()
    }
    /// Complete original source inspection with explicit excess-work rejection.
    pub fn try_uniform_color_space(
        &self,
        maximum_entries: usize,
    ) -> Result<Option<ImageFilterColorSpace>, Error> {
        fn visit(
            entries: &[PictureColorEntry],
            depth: u16,
            count: &mut usize,
            maximum: usize,
        ) -> Result<Option<ImageFilterColorSpace>, Error> {
            if depth >= 256 {
                return Err(Error::Capacity);
            }
            let mut candidate: Option<ImageFilterColorSpace> = None;
            for entry in entries {
                *count = count.checked_add(1).ok_or(Error::Capacity)?;
                if *count > maximum {
                    return Err(Error::Capacity);
                }
                let next = match entry {
                    PictureColorEntry::Image(Some(c)) => c.clone(),
                    PictureColorEntry::Picture(p) => match visit(p, depth + 1, count, maximum)? {
                        Some(c) => c,
                        None => return Ok(None),
                    },
                    PictureColorEntry::Scope => continue,
                    _ => return Ok(None),
                };
                if candidate
                    .as_ref()
                    .is_some_and(|c| c.transfer() != next.transfer())
                {
                    return Ok(None);
                }
                candidate = Some(next);
            }
            Ok(candidate)
        }
        visit(&self.color_inventory, 0, &mut 0, maximum_entries)
    }
}
/// Original shader-source owners. Every branch remains a source-capture request;
/// it cannot be mislabeled as an already-rendered filter result.
#[derive(Clone, Debug)]
pub enum FilterShaderSource {
    /// Original empty shader.
    Empty,
    /// Actual immutable procedural/solid/gradient material, including Perlin.
    Brush(Arc<Brush>),
    /// Actual image shader with independent tile modes, sampling and local matrix.
    Image {
        /// Actual selected source owner.
        source: FilterImageSource,
        /// Original horizontal/vertical tile modes.
        tile: [FilterTileMode; 2],
        /// Original reconstruction descriptor.
        sampling: FilterSampling,
        /// Original shader local frame.
        matrix: Matrix4,
        /// Original raw-image policy bypassing selected-source color conversion.
        is_raw: bool,
    },
    /// Actual picture shader recording and its original local frame/tile bounds.
    Picture {
        /// Actual recording, not an output image.
        source: FilterPictureSource,
        /// Original shader local frame.
        matrix: Matrix4,
        /// Original tile rectangle.
        tile: FilterRect,
        /// Independent original horizontal and vertical tile modes.
        tile_modes: [FilterTileMode; 2],
        /// Original picture tile nearest/linear reconstruction.
        sampling: crate::image::ImageSampling,
    },
    /// Ordered original composed shader children.
    Compose {
        /// Original destination child.
        background: Arc<FilterShaderSource>,
        /// Original source child.
        foreground: Arc<FilterShaderSource>,
        /// Original shader blend mode, used when arithmetic is absent.
        mode: LayerBlend,
        /// Original independent arithmetic-composed shader policy.
        arithmetic: Option<FilterArithmetic>,
    },
    /// Retained local-matrix wrapper; never flattened into child metadata.
    LocalMatrix {
        /// Actual source child.
        source: Arc<FilterShaderSource>,
        /// Original local transform.
        matrix: Matrix4,
    },
    /// Original shader color-filter wrapper and independent filter generation.
    ColorFilter {
        /// Actual source child.
        source: Arc<FilterShaderSource>,
        /// Original immutable filter.
        filter: ColorFilter,
    },
    /// Explicit unresolved original runtime source and immutable bindings. This
    /// factory retains ownership only; it does not pretend WPF bytecode is SkSL.
    UnsupportedRuntime {
        /// Actual original source text.
        source: Arc<str>,
        /// Original immutable uniform bytes.
        uniforms: Arc<[u8]>,
        /// Original ordered child shader owners.
        children: Arc<[FilterShaderSource]>,
    },
}
impl FilterShaderSource {
    /// Whether every retained shader owner has an implemented source category.
    /// Runtime language sources stay unsupported through arbitrary wrappers.
    /// Bounded traversal rejects excess depth/work rather than inventing paint.
    pub fn runtime_support(&self) -> Result<bool, Error> {
        fn visit(
            source: &FilterShaderSource,
            depth: u16,
            count: &mut usize,
        ) -> Result<bool, Error> {
            *count = count.checked_add(1).ok_or(Error::Capacity)?;
            if depth >= 256 || *count > 16_384 {
                return Err(Error::Capacity);
            }
            match source {
                FilterShaderSource::UnsupportedRuntime { .. } => Ok(false),
                FilterShaderSource::Compose {
                    background,
                    foreground,
                    ..
                } => {
                    let background = visit(background, depth + 1, count)?;
                    let foreground = visit(foreground, depth + 1, count)?;
                    Ok(background && foreground)
                }
                FilterShaderSource::LocalMatrix { source, .. }
                | FilterShaderSource::ColorFilter { source, .. } => visit(source, depth + 1, count),
                _ => Ok(true),
            }
        }
        visit(self, 0, &mut 0)
    }
}
/// Original image/source rectangles and selected sampling.
#[derive(Clone, Debug)]
pub struct FilterImageData {
    /// Actual original source owner and transfer.
    pub source: FilterImageSource,
    /// Original source endpoints.
    pub source_rect: FilterRect,
    /// Original destination endpoints.
    pub destination: FilterRect,
    /// Original independent sampling metadata.
    pub sampling: FilterSampling,
}
/// Original shadow descriptor; capture, blur and source-over remain distinct steps.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FilterShadow {
    /// Original offset before filter-frame normal transformation.
    pub offset: [f32; 2],
    /// Independent original axis sigmas.
    pub sigma: [f32; 2],
    /// Original straight RGBA8 shadow color.
    pub color: [u8; 4],
    /// Omit only the final source overlay.
    pub shadow_only: bool,
}
/// Original arithmetic coefficients and premultiplied enforcement flag.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FilterArithmetic {
    /// Original k1/k2/k3/k4.
    pub coefficients: [f32; 4],
    /// Original independent output-color enforcement.
    pub enforce_premultiplied: bool,
}
/// Original supported image-blender policies, retaining explicit unsupported
/// runtime ownership instead of choosing an invented semantic blend mode.
#[derive(Clone, Debug)]
pub enum FilterBlender {
    /// Original fixed blend policy.
    Mode(LayerBlend),
    /// Original independent arithmetic expression.
    Arithmetic(FilterArithmetic),
    /// Actual unresolved runtime source; execution rejects this owner.
    UnsupportedRuntime(Arc<str>),
}
/// Original convolution data, copied atomically by its factory.
#[derive(Clone, Debug)]
pub struct FilterConvolution {
    /// Original signed kernel dimensions; retained invalid dimensions select the
    /// producer's input-preserving path during planning.
    pub size: [i32; 2],
    /// Original exact coefficient inventory in row-major order.
    pub kernel: Arc<[f32]>,
    /// Original raw gain.
    pub gain: f32,
    /// Original raw byte bias.
    pub bias: f32,
    /// Original signed kernel anchor.
    pub offset: [i32; 2],
    /// Original edge sampling policy.
    pub tile_mode: FilterTileMode,
    /// Original alpha policy.
    pub convolve_alpha: bool,
}
impl FilterConvolution {
    /// Preserve checked signed size multiplication and exact original array length.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        size: [i32; 2],
        kernel: &[f32],
        gain: f32,
        bias: f32,
        offset: [i32; 2],
        tile_mode: FilterTileMode,
        convolve_alpha: bool,
    ) -> Result<Self, Error> {
        let count = size[0].checked_mul(size[1]).ok_or(Error::Capacity)?;
        if count < 0 || kernel.len() != count as usize {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            size,
            kernel: Arc::from(kernel),
            gain,
            bias,
            offset,
            tile_mode,
            convolve_alpha,
        })
    }
}
/// Original magnifier factory data, before device-frame lens resolution.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FilterMagnifier {
    /// Original lens endpoints.
    pub lens: FilterRect,
    /// Original positive zoom amount.
    pub zoom: f32,
    /// Original nonnegative logical inset.
    pub inset: f32,
    /// Original complete sampling request.
    pub sampling: FilterSampling,
}
/// Original light source geometry; shader type stays distinct from light mode.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum FilterLightSource {
    /// Original normal-vector distant direction.
    Distant([f32; 3]),
    /// Original point-light location.
    Point([f32; 3]),
    /// Original spot geometry and exponent/cutoff policy.
    Spot {
        /// Original spot origin.
        location: [f32; 3],
        /// Independent original spot target.
        target: [f32; 3],
        /// Original spot exponent before parameter normalization.
        exponent: f32,
        /// Original cutoff in degrees before parameter normalization.
        cutoff: f32,
    },
}
/// Original lighting data, shared by six independent node kinds.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FilterLight {
    /// Original independent light source.
    pub source: FilterLightSource,
    /// Original straight RGBA8 light color.
    pub color: [u8; 4],
    /// Original alpha-to-height scale.
    pub surface_scale: f32,
    /// Original diffuse/specular coefficient.
    pub constant: f32,
    /// Original specular exponent.
    pub shininess: f32,
}
impl FilterLight {
    /// Original source-to-device lowering and five-vector parameter construction.
    pub fn parameters(self, transform: FilterTransform, specular: bool) -> ImageLightingParams {
        let (position, kind, target, exponent, cutoff) = match self.source {
            FilterLightSource::Distant(v) => (v, 0, [0.0; 3], 0.0, 90.0),
            FilterLightSource::Point(v) => (v, 1, [0.0; 3], 0.0, 90.0),
            FilterLightSource::Spot {
                location,
                target,
                exponent,
                cutoff,
            } => (location, 2, target, exponent, cutoff),
        };
        ImageLightingParams::new(transform.light(ImageLightingInput {
            light_position: position,
            light_type: kind,
            light_target: target,
            spot_exponent: exponent,
            light_color: self.color.map(|v| f32::from(v) / 255.0),
            surface_scale: self.surface_scale,
            lighting_constant: self.constant,
            shininess: self.shininess,
            cutoff_angle: cutoff,
            specular,
        }))
    }
}
/// Complete original SKImageFilter kind inventory. Ordinary optional input and
/// final crop are retained by the owning node independently of variant data.
#[derive(Clone, Debug)]
pub enum ImageFilterKind {
    /// Independent two-axis Gaussian blur.
    Blur {
        /// Original logical axis sigmas.
        sigma: [f32; 2],
        /// Retained original edge metadata; the original blur evaluator ignores it.
        tile_mode: FilterTileMode,
    },
    /// Evaluate inner, then rebind outer's implicit source to that actual result.
    Compose {
        /// Consumer graph.
        outer: ImageFilter,
        /// Source graph.
        inner: ImageFilter,
    },
    /// Original tint, blur, offset and optional source overlay.
    DropShadow(FilterShadow),
    /// Independent two-input arithmetic filter.
    Arithmetic {
        /// Original coefficients and enforcement flag.
        parameters: FilterArithmetic,
        /// Destination child; None selects the current bound source.
        background: Option<ImageFilter>,
        /// Source child; None selects the current bound source.
        foreground: Option<ImageFilter>,
    },
    /// Original immutable mode/arithmetic/runtime blender.
    BlendMode {
        /// Actual selected blender owner.
        blender: FilterBlender,
        /// Destination child or current bound source.
        background: Option<ImageFilter>,
        /// Source child or current bound source.
        foreground: Option<ImageFilter>,
    },
    /// Original color-filter graph, independently source rebound.
    ColorFilter(ColorFilter),
    /// Original logical dilation radii.
    Dilate([f32; 2]),
    /// Independent original displacement-map child and channel selection.
    DisplacementMap {
        /// Original X/Y straight-color selectors.
        channels: [FilterColorChannel; 2],
        /// Original logical displacement scale.
        scale: f32,
        /// Required actual map generation.
        displacement: ImageFilter,
    },
    /// Distant diffuse height-field lighting.
    DistantLitDiffuse(FilterLight),
    /// Distant specular height-field lighting.
    DistantLitSpecular(FilterLight),
    /// Original logical erosion radii.
    Erode([f32; 2]),
    /// Actual image-source generation and source/destination mapping.
    Image(FilterImageData),
    /// Original lens magnifier with pre-crop policy.
    Magnifier(FilterMagnifier),
    /// Original full local matrix and independent image sampling.
    MatrixTransform {
        /// All original matrix components.
        matrix: Matrix4,
        /// Original source reconstruction.
        sampling: FilterSampling,
    },
    /// Original bounded convolution and exact coefficient inventory.
    MatrixConvolution(FilterConvolution),
    /// Original painter-ordered children, retaining None source slots.
    Merge(Arc<[Option<ImageFilter>]>),
    /// Original logical offset, transformed as a normal vector at planning.
    Offset([f32; 2]),
    /// Actual shader source owner; None selects a fresh transparent capture.
    Shader {
        /// Original source generation, including explicit unsupported owners.
        source: Option<Arc<FilterShaderSource>>,
        /// Original dither metadata, retained even though its evaluator ignores it.
        dither: bool,
    },
    /// Actual retained picture and target clip.
    Picture(FilterPictureSource),
    /// Point diffuse height-field lighting.
    PointLitDiffuse(FilterLight),
    /// Point specular height-field lighting.
    PointLitSpecular(FilterLight),
    /// Spot diffuse height-field lighting.
    SpotLitDiffuse(FilterLight),
    /// Spot specular height-field lighting.
    SpotLitSpecular(FilterLight),
    /// Original integer-ordered source tiles clipped to destination bounds.
    Tile {
        /// Original source tile endpoints.
        source: FilterRect,
        /// Original independent destination endpoints.
        destination: FilterRect,
    },
}

#[derive(Debug)]
struct Node {
    kind: ImageFilterKind,
    input: Option<ImageFilter>,
    crop: Option<FilterRect>,
    depth: u16,
}
/// Immutable actual original filter generation; public factories never realize GPU work.
#[derive(Clone, Debug)]
pub struct ImageFilter(Arc<Node>);
impl ImageFilter {
    /// Retain a complete original descriptor with bounded owned nesting. Unknown
    /// runtime source ownership stays explicit and is never evaluated as identity.
    pub fn new(
        kind: ImageFilterKind,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        match &kind {
            ImageFilterKind::DistantLitDiffuse(light)
            | ImageFilterKind::DistantLitSpecular(light)
                if !matches!(light.source, FilterLightSource::Distant(_)) =>
            {
                return Err(Error::InvalidInput);
            }
            ImageFilterKind::PointLitDiffuse(light) | ImageFilterKind::PointLitSpecular(light)
                if !matches!(light.source, FilterLightSource::Point(_)) =>
            {
                return Err(Error::InvalidInput);
            }
            ImageFilterKind::SpotLitDiffuse(light) | ImageFilterKind::SpotLitSpecular(light)
                if !matches!(light.source, FilterLightSource::Spot { .. }) =>
            {
                return Err(Error::InvalidInput);
            }
            ImageFilterKind::Tile { .. } if input.is_none() => return Err(Error::InvalidInput),
            ImageFilterKind::Magnifier(data)
                if !data.lens.is_valid()
                    || !data.zoom.is_finite()
                    || data.zoom <= 0.0
                    || !data.inset.is_finite()
                    || data.inset < 0.0 =>
            {
                return Err(Error::InvalidInput);
            }
            ImageFilterKind::MatrixConvolution(data) => {
                let count = data.size[0]
                    .checked_mul(data.size[1])
                    .ok_or(Error::Capacity)?;
                if count < 0 || data.kernel.len() != count as usize {
                    return Err(Error::InvalidInput);
                }
            }
            ImageFilterKind::Shader {
                source: Some(source),
                ..
            } => {
                source.runtime_support()?;
            }
            _ => {}
        }
        let mut depth = input.as_ref().map_or(0, |v| v.0.depth);
        let mut include = |v: &Self| depth = depth.max(v.0.depth);
        match &kind {
            ImageFilterKind::Compose { outer, inner } => {
                include(outer);
                include(inner);
            }
            ImageFilterKind::Arithmetic {
                background,
                foreground,
                ..
            }
            | ImageFilterKind::BlendMode {
                background,
                foreground,
                ..
            } => {
                for child in [background, foreground].into_iter().flatten() {
                    include(child);
                }
            }
            ImageFilterKind::DisplacementMap { displacement, .. } => include(displacement),
            ImageFilterKind::Merge(children) => {
                for child in children.iter().flatten() {
                    include(child);
                }
            }
            _ => {}
        }
        if depth >= 256 {
            return Err(Error::Capacity);
        }
        Ok(Self(Arc::new(Node {
            kind,
            input,
            crop,
            depth: depth + 1,
        })))
    }
    /// Original distant diffuse-light factory. The source variant is checked
    /// atomically, so a point or spot owner cannot acquire distant identity.
    pub fn distant_diffuse(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::DistantLitDiffuse(light), input, crop)
    }
    /// Original distant specular-light factory.
    pub fn distant_specular(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::DistantLitSpecular(light), input, crop)
    }
    /// Original point diffuse-light factory.
    pub fn point_diffuse(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::PointLitDiffuse(light), input, crop)
    }
    /// Original point specular-light factory.
    pub fn point_specular(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::PointLitSpecular(light), input, crop)
    }
    /// Original spot diffuse-light factory.
    pub fn spot_diffuse(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::SpotLitDiffuse(light), input, crop)
    }
    /// Original spot specular-light factory.
    pub fn spot_specular(
        light: FilterLight,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::SpotLitSpecular(light), input, crop)
    }
    /// Original tile factory requires an actual input generation, retaining source
    /// and destination rectangles without inferring a default child.
    pub fn tile(source: FilterRect, destination: FilterRect, input: Self) -> Result<Self, Error> {
        Self::new(
            ImageFilterKind::Tile {
                source,
                destination,
            },
            Some(input),
            None,
        )
    }
    /// Original whole-image factory with explicit sampling; omitted sampling uses
    /// `FilterSampling::MITCHELL` in `whole_image`.
    pub fn image_with_sampling(
        source: FilterImageSource,
        sampling: FilterSampling,
    ) -> Result<Self, Error> {
        let rect = FilterRect::texture(source.image.size());
        Self::image(FilterImageData {
            source,
            source_rect: rect,
            destination: rect,
            sampling,
        })
    }
    /// Original no-sampling-argument image factory selects Mitchell reconstruction.
    pub fn whole_image(source: FilterImageSource) -> Result<Self, Error> {
        Self::image_with_sampling(source, FilterSampling::MITCHELL)
    }
    /// Exact original variant content.
    pub fn kind(&self) -> &ImageFilterKind {
        &self.0.kind
    }
    /// Actual optional input; None means the currently bound source generation.
    pub fn input(&self) -> Option<&Self> {
        self.0.input.as_ref()
    }
    /// Original independent optional crop metadata, including invalid metadata.
    pub fn crop_rect(&self) -> Option<FilterRect> {
        self.0.crop
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
    /// Original two-axis blur factory; tile mode stays retained even though the
    /// original retained blur evaluator uses its fixed edge policy.
    pub fn blur(
        sigma: [f32; 2],
        tile_mode: FilterTileMode,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Blur { sigma, tile_mode }, input, crop)
    }
    /// Original inner-then-outer rebinding, with no synthetic parameter fusion.
    pub fn compose(outer: Self, inner: Self) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Compose { outer, inner }, None, None)
    }
    /// Original shadow and shadow-only factory.
    pub fn drop_shadow(
        data: FilterShadow,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::DropShadow(data), input, crop)
    }
    /// Original independent background/foreground arithmetic factory.
    pub fn arithmetic(
        parameters: FilterArithmetic,
        background: Option<Self>,
        foreground: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(
            ImageFilterKind::Arithmetic {
                parameters,
                background,
                foreground,
            },
            None,
            crop,
        )
    }
    /// Original mode/arithmetic/runtime blender ownership.
    pub fn blend(
        blender: FilterBlender,
        background: Option<Self>,
        foreground: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(
            ImageFilterKind::BlendMode {
                blender,
                background,
                foreground,
            },
            None,
            crop,
        )
    }
    /// Original retained color-filter child.
    pub fn color_filter(
        filter: ColorFilter,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::ColorFilter(filter), input, crop)
    }
    /// Original dilation factory.
    pub fn dilate(
        radius: [f32; 2],
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Dilate(radius), input, crop)
    }
    /// Original erosion factory.
    pub fn erode(
        radius: [f32; 2],
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Erode(radius), input, crop)
    }
    /// Original required displacement child and optional color child.
    pub fn displacement(
        channels: [FilterColorChannel; 2],
        scale: f32,
        displacement: Self,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(
            ImageFilterKind::DisplacementMap {
                channels,
                scale,
                displacement,
            },
            input,
            crop,
        )
    }
    /// Original exact copied convolution inventory.
    pub fn convolution(
        data: FilterConvolution,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::MatrixConvolution(data), input, crop)
    }
    /// Original merge order, including null slots and empty arrays.
    pub fn merge(children: &[Option<Self>], crop: Option<FilterRect>) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Merge(Arc::from(children)), None, crop)
    }
    /// Original offset factory.
    pub fn offset(
        offset: [f32; 2],
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Offset(offset), input, crop)
    }
    /// Original Crop factory ignores tile mode and retains offset zero plus crop.
    pub fn crop(
        rect: FilterRect,
        _tile_mode: FilterTileMode,
        input: Option<Self>,
    ) -> Result<Self, Error> {
        Self::offset([0.0; 2], input, Some(rect))
    }
    /// Original Empty factory is an empty shader source, not the bound source.
    pub fn empty() -> Self {
        Self(Arc::new(Node {
            kind: ImageFilterKind::Shader {
                source: None,
                dither: false,
            },
            input: None,
            crop: None,
            depth: 1,
        }))
    }
    /// Original shader factory retains dither even though original DrawPaint ignores it.
    pub fn shader(
        source: Option<Arc<FilterShaderSource>>,
        dither: bool,
        crop: Option<FilterRect>,
    ) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Shader { source, dither }, None, crop)
    }
    /// Actual owned picture recording, independent of the bound source.
    pub fn picture(source: FilterPictureSource) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Picture(source), None, None)
    }
    /// Original image factory and exact source/destination sampling metadata.
    pub fn image(source: FilterImageData) -> Result<Self, Error> {
        Self::new(ImageFilterKind::Image(source), None, None)
    }
    /// Original complete matrix descriptor; singular device frames become empty
    /// during planning, not silently discarded transforms.
    pub fn matrix(
        matrix: Matrix4,
        sampling: FilterSampling,
        input: Option<Self>,
    ) -> Result<Self, Error> {
        Self::new(
            ImageFilterKind::MatrixTransform { matrix, sampling },
            input,
            None,
        )
    }
    /// Original magnifier admission and <=1/no-crop null-or-input identity.
    pub fn magnifier(
        data: FilterMagnifier,
        input: Option<Self>,
        crop: Option<FilterRect>,
    ) -> Result<Option<Self>, Error> {
        if !data.lens.is_valid()
            || !data.zoom.is_finite()
            || data.zoom <= 0.0
            || !data.inset.is_finite()
            || data.inset < 0.0
        {
            return Err(Error::InvalidInput);
        }
        if data.zoom <= 1.0 && crop.is_none() {
            return Ok(input);
        }
        Self::new(ImageFilterKind::Magnifier(data), input, crop).map(Some)
    }
    /// Original source traversal order, including early first applicable image/
    /// picture return. This deliberately does not compute an `any` reduction.
    pub fn has_linear_image_source(&self) -> Result<bool, Error> {
        self.try_has_linear_image_source(16_384)
    }
    /// Source search with the caller's explicit bound, preserving original first
    /// applicable selection and rejecting exhausted work before publication.
    pub fn try_has_linear_image_source(&self, maximum_nodes: usize) -> Result<bool, Error> {
        let mut visited = std::collections::HashSet::new();
        let mut pending = vec![self];
        while let Some(current) = pending.pop() {
            if !visited.insert(current.identity()) {
                continue;
            }
            if visited.len() > maximum_nodes {
                return Err(Error::Capacity);
            }
            match current.kind() {
                ImageFilterKind::Image(image) => {
                    return Ok(image
                        .source
                        .color_space
                        .as_ref()
                        .is_some_and(ImageFilterColorSpace::is_linear));
                }
                ImageFilterKind::Picture(picture) => {
                    if let Some(color) = picture.try_uniform_color_space(maximum_nodes)? {
                        return Ok(color.is_linear());
                    }
                }
                _ => {}
            }
            if let Some(input) = current.input() {
                pending.push(input);
            }
            match current.kind() {
                ImageFilterKind::Compose { outer, inner } => {
                    pending.push(outer);
                    pending.push(inner);
                }
                ImageFilterKind::Arithmetic {
                    background,
                    foreground,
                    ..
                }
                | ImageFilterKind::BlendMode {
                    background,
                    foreground,
                    ..
                } => {
                    if let Some(b) = background {
                        pending.push(b);
                    }
                    if let Some(f) = foreground {
                        pending.push(f);
                    }
                }
                ImageFilterKind::DisplacementMap { displacement, .. } => pending.push(displacement),
                ImageFilterKind::Merge(children) => {
                    if children.len() > maximum_nodes.saturating_sub(pending.len()) {
                        return Err(Error::Capacity);
                    }
                    for input in children.iter().flatten() {
                        pending.push(input);
                    }
                }
                _ => {}
            }
        }
        Ok(false)
    }
}
