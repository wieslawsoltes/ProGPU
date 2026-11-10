//! Original retained factory identities, immutable tables and ordered composition.
use super::*;
use crate::scene::LayerBlend;
use std::sync::OnceLock;
const IDENTITY: [u8; 256] = {
    let mut table = [0; 256];
    let mut i = 0;
    while i < 256 {
        table[i] = i as u8;
        i += 1;
    }
    table
};
/// Original compact table ownership layout, independent of the expanded GPU buffer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ColorTableLayout {
    /// Four implicit identity tables, no retained byte allocation.
    Identity,
    /// One shared table for all four channels.
    Shared,
    /// Owned alpha table, implicit identity RGB.
    AlphaIdentityRgb,
    /// One alpha table and one RGB table.
    AlphaSharedRgb,
    /// Four owned tables in original A/R/G/B order.
    Packed,
}
/// Exact immutable table bytes and original compact-layout identity.
#[derive(Clone, Debug)]
pub struct ColorTables {
    layout: ColorTableLayout,
    data: Arc<[u8]>,
}
impl ColorTables {
    /// Copy a complete shared table once; identity tables retain no data bytes.
    pub fn shared(table: &[u8]) -> Result<Self, Error> {
        if table.len() != 256 {
            return Err(Error::InvalidInput);
        }
        Ok(if table == IDENTITY {
            Self {
                layout: ColorTableLayout::Identity,
                data: Arc::from([]),
            }
        } else {
            Self {
                layout: ColorTableLayout::Shared,
                data: Arc::from(table),
            }
        })
    }
    /// Preserve the original first matching A/R/G/B compaction policy.
    pub fn channels(alpha: &[u8], red: &[u8], green: &[u8], blue: &[u8]) -> Result<Self, Error> {
        if [alpha, red, green, blue].iter().any(|t| t.len() != 256) {
            return Err(Error::InvalidInput);
        }
        let identity_rgb = red == IDENTITY && green == IDENTITY && blue == IDENTITY;
        if alpha == IDENTITY && identity_rgb {
            return Self::shared(&IDENTITY);
        }
        if alpha == red && alpha == green && alpha == blue {
            return Self::shared(alpha);
        }
        if identity_rgb {
            return Ok(Self {
                layout: ColorTableLayout::AlphaIdentityRgb,
                data: Arc::from(alpha),
            });
        }
        let (layout, data) = if red == green && red == blue {
            (ColorTableLayout::AlphaSharedRgb, [alpha, red].concat())
        } else {
            (ColorTableLayout::Packed, [alpha, red, green, blue].concat())
        };
        Ok(Self {
            layout,
            data: Arc::from(data),
        })
    }
    /// Original compact layout, including distinct identity tables.
    pub fn layout(&self) -> ColorTableLayout {
        self.layout
    }
    /// Original retained bytes; zero bytes for implicit identity.
    pub fn bytes(&self) -> &[u8] {
        &self.data
    }
    /// Original independent alpha/red/green/blue views without allocation.
    pub fn views(&self) -> [&[u8]; 4] {
        match self.layout {
            ColorTableLayout::Identity => [&IDENTITY; 4],
            ColorTableLayout::Shared => [&self.data; 4],
            ColorTableLayout::AlphaIdentityRgb => [&self.data, &IDENTITY, &IDENTITY, &IDENTITY],
            ColorTableLayout::AlphaSharedRgb => [
                &self.data[..256],
                &self.data[256..],
                &self.data[256..],
                &self.data[256..],
            ],
            ColorTableLayout::Packed => [
                &self.data[..256],
                &self.data[256..512],
                &self.data[512..768],
                &self.data[768..],
            ],
        }
    }
    /// Original compute buffer order is red/green/blue/alpha, each u32 0..255.
    pub fn gpu_values(&self) -> [u32; 1024] {
        let [a, r, g, b] = self.views();
        let channels = [r, g, b, a];
        std::array::from_fn(|i| u32::from(channels[i / 256][i % 256]))
    }
}
/// Original high-contrast invert policy; no brightness/lightness aliasing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum InvertStyle {
    #[default]
    /// Preserve original channels.
    None = 0,
    /// Invert linear RGB brightness.
    Brightness = 1,
    /// Invert HSL lightness in linear space.
    Lightness = 2,
}
/// Original public high-contrast descriptor; finite contrast lies in -1..=1.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct HighContrast {
    /// Convert linear RGB to luminance before inversion/contrast.
    pub grayscale: bool,
    /// Original independent inversion policy.
    pub invert: InvertStyle,
    /// Original source contrast; endpoints are retained, then bounded in uniforms.
    pub contrast: f32,
}
impl HighContrast {
    /// Original constructor admission, before retaining a filter generation.
    pub fn validate(self) -> Result<(), Error> {
        if (-1.0..=1.0).contains(&self.contrast) {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
    /// Original finite parameter builder and machine-epsilon contrast limits.
    pub fn uniforms(self) -> NonlinearColorUniforms {
        NonlinearColorUniforms::high_contrast(self.grayscale, self.invert as u32, self.contrast)
    }
}
/// Original immutable node content; public views cannot mutate retained data.
#[derive(Clone, Debug)]
pub enum ColorFilterKind {
    /// Retained constant straight color blended against the input.
    Blend {
        /// Original RGBA8 constant.
        color: [u8; 4],
        /// Original semantic blend mode.
        mode: LayerBlend,
    },
    /// Original compact lookup tables.
    Table(ColorTables),
    /// Original normalized RGBA matrix.
    Matrix(ImageColorMatrix),
    /// Original luminance multiplied by input alpha, with zero output RGB.
    Luma,
    /// Apply inner first, then outer, preserving intermediate clamp/quantization.
    Compose {
        /// Applied after the inner result.
        outer: ColorFilter,
        /// Applied to the original input.
        inner: ColorFilter,
    },
    /// Evaluate both retained children on the same source, then premultiplied lerp.
    Lerp {
        /// Original weight strictly between zero and one.
        weight: f32,
        /// Original filter zero/background.
        filter0: ColorFilter,
        /// Original filter one/foreground.
        filter1: ColorFilter,
    },
    /// Original HSLA round trip and matrix.
    Hsla(ImageColorMatrix),
    /// Original linear-light high-contrast node.
    HighContrast(HighContrast),
    /// Original six straight RGBA8 palette entries.
    Overdraw([[u8; 4]; 6]),
    /// Actual original runtime source and bindings. The managed image path
    /// preserves its input; single-color evaluation keeps its original Clear.
    Runtime(crate::runtime_effect::RuntimeEffectInstance),
}
#[derive(Debug)]
struct Node {
    kind: ColorFilterKind,
    depth: u16,
}
/// Shared immutable original color-filter generation. Factories do no GPU work.
#[derive(Clone, Debug)]
pub struct ColorFilter(Arc<Node>);
impl ColorFilter {
    pub(crate) fn from_runtime_instance(
        instance: crate::runtime_effect::RuntimeEffectInstance,
    ) -> Self {
        Self::leaf(ColorFilterKind::Runtime(instance))
    }
    fn node(kind: ColorFilterKind, depth: u16) -> Result<Self, Error> {
        if depth > 256 {
            return Err(Error::Capacity);
        }
        Ok(Self(Arc::new(Node { kind, depth })))
    }
    fn leaf(kind: ColorFilterKind) -> Self {
        Self(Arc::new(Node { kind, depth: 1 }))
    }
    /// Original node content and source-owner identities.
    pub fn kind(&self) -> &ColorFilterKind {
        &self.0.kind
    }
    /// Actual original reflected runtime instance, without executing source text.
    pub fn runtime_effect(&self) -> Option<&crate::runtime_effect::RuntimeEffectInstance> {
        match self.kind() {
            ColorFilterKind::Runtime(instance) => Some(instance),
            _ => None,
        }
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
    /// Original matrix factory; rejects nonfinite values, copies exactly 20 floats.
    pub fn matrix(values: [f32; 20]) -> Result<Self, Error> {
        Ok(Self::leaf(ColorFilterKind::Matrix(
            ImageColorMatrix::from_managed(values)?,
        )))
    }
    /// Original HSLA matrix factory with independent GPU/CPU evaluation policy.
    pub fn hsla_matrix(values: [f32; 20]) -> Result<Self, Error> {
        Ok(Self::leaf(ColorFilterKind::Hsla(
            ImageColorMatrix::from_managed(values)?,
        )))
    }
    /// Retain one complete shared table.
    pub fn table(table: &[u8]) -> Result<Self, Error> {
        Ok(Self::leaf(ColorFilterKind::Table(ColorTables::shared(
            table,
        )?)))
    }
    /// Retain original independent A/R/G/B channel tables and compact identity.
    pub fn tables(alpha: &[u8], red: &[u8], green: &[u8], blue: &[u8]) -> Result<Self, Error> {
        Ok(Self::leaf(ColorFilterKind::Table(ColorTables::channels(
            alpha, red, green, blue,
        )?)))
    }
    /// Original luma node; no matrix approximation or precomputed image.
    pub fn luma() -> Self {
        Self::leaf(ColorFilterKind::Luma)
    }
    /// Original blend simplification. None represents the producer's no-op/null result.
    pub fn blend(mut color: [u8; 4], mut mode: LayerBlend) -> Option<Self> {
        if mode == LayerBlend::Clear {
            color = [0; 4];
            mode = LayerBlend::Src;
        } else if mode == LayerBlend::SrcOver {
            mode = match color[3] {
                0 => LayerBlend::Dst,
                255 => LayerBlend::Src,
                _ => mode,
            };
        }
        let no_op = mode == LayerBlend::Dst
            || color[3] == 0
                && matches!(
                    mode,
                    LayerBlend::SrcOver
                        | LayerBlend::DstOver
                        | LayerBlend::DstOut
                        | LayerBlend::SrcAtop
                        | LayerBlend::Xor
                        | LayerBlend::Darken
                )
            || color[3] == 255 && mode == LayerBlend::DstIn;
        (!no_op).then(|| Self::leaf(ColorFilterKind::Blend { color, mode }))
    }
    /// Original lighting factory, ignoring multiply/add alpha exactly as source.
    pub fn lighting(multiply: [u8; 4], add: [u8; 4]) -> Self {
        if add[..3] == [0; 3] {
            return Self::leaf(ColorFilterKind::Blend {
                color: [multiply[0], multiply[1], multiply[2], 255],
                mode: LayerBlend::Modulate,
            });
        }
        let mut matrix = ImageColorMatrix::default();
        matrix.red[0] = f32::from(multiply[0]) / 255.0;
        matrix.green[1] = f32::from(multiply[1]) / 255.0;
        matrix.blue[2] = f32::from(multiply[2]) / 255.0;
        matrix.offset = [
            f32::from(add[0]) / 255.0,
            f32::from(add[1]) / 255.0,
            f32::from(add[2]) / 255.0,
            0.0,
        ];
        Self::leaf(ColorFilterKind::Matrix(matrix))
    }
    /// Compose actual immutable children; never multiply matrices across clamps.
    /// The Rust ownership guard bounds nesting to 256 nodes before retaining input.
    pub fn compose(outer: Self, inner: Self) -> Result<Self, Error> {
        let depth = outer.0.depth.max(inner.0.depth) + 1;
        Self::node(ColorFilterKind::Compose { outer, inner }, depth)
    }
    /// Preserve original NaN rejection, endpoint/identity selection and child order.
    pub fn lerp(weight: f32, filter0: Self, filter1: Self) -> Result<Self, Error> {
        if weight.is_nan() {
            return Err(Error::InvalidInput);
        }
        if Arc::ptr_eq(&filter0.0, &filter1.0) || weight <= 0.0 {
            return Ok(filter0);
        }
        if weight >= 1.0 {
            return Ok(filter1);
        }
        let depth = filter0.0.depth.max(filter1.0.depth) + 1;
        Self::node(
            ColorFilterKind::Lerp {
                weight,
                filter0,
                filter1,
            },
            depth,
        )
    }
    /// Retain the original validated high-contrast descriptor.
    pub fn high_contrast(config: HighContrast) -> Result<Self, Error> {
        config.validate()?;
        Ok(Self::leaf(ColorFilterKind::HighContrast(config)))
    }
    /// Copy exactly six original overdraw colors once.
    pub fn overdraw(colors: [[u8; 4]; 6]) -> Self {
        Self::leaf(ColorFilterKind::Overdraw(colors))
    }
    /// Shared immutable original 8-bit sRGB-to-linear transfer table.
    pub fn srgb_to_linear() -> Self {
        static FILTER: OnceLock<ColorFilter> = OnceLock::new();
        FILTER
            .get_or_init(|| gamma(super::cpu::srgb_to_linear))
            .clone()
    }
    /// Shared immutable original 8-bit linear-to-sRGB transfer table.
    pub fn linear_to_srgb() -> Self {
        static FILTER: OnceLock<ColorFilter> = OnceLock::new();
        FILTER
            .get_or_init(|| gamma(super::cpu::linear_to_srgb))
            .clone()
    }
    /// Direct original Texture matrix lowering. Composition stays ordered passes.
    pub fn image_matrix(&self) -> Option<ImageColorMatrix> {
        match self.kind() {
            ColorFilterKind::Matrix(m) => Some(*m),
            ColorFilterKind::Luma => Some(ImageColorMatrix {
                luminance_to_alpha: true,
                ..Default::default()
            }),
            _ => None,
        }
    }
    /// Original managed per-color brush/gradient evaluation, including byte
    /// quantization after every retained node. This is not CPU image filtering.
    pub fn apply_color(&self, rgba: [u8; 4]) -> [u8; 4] {
        super::cpu::apply(self, rgba)
    }
    /// Original brush boundary narrows to RGBA8 before filtering and expands after.
    pub fn apply_brush_color(&self, rgba: [f32; 4]) -> [f32; 4] {
        self.apply_color(rgba.map(super::cpu::to_byte))
            .map(|v| f32::from(v) / 255.0)
    }
}
fn gamma(transfer: fn(f32) -> f32) -> ColorFilter {
    let mut data = Vec::with_capacity(512);
    data.extend_from_slice(&IDENTITY);
    for i in 0..256 {
        data.push(super::cpu::to_byte(transfer(i as f32 / 255.0)));
    }
    ColorFilter::leaf(ColorFilterKind::Table(ColorTables {
        layout: ColorTableLayout::AlphaSharedRgb,
        data: Arc::from(data),
    }))
}
