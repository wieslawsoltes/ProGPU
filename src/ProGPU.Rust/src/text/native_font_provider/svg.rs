//! Native OpenType SVG XML-to-layer producer. Source bytes stay independent of
//! the managed XML/UTF-8 policy. Geometry/paint execution uses retained canonical
//! path and material records; this is not a general-purpose browser SVG engine.
use crate::{
    font::{FontError, SfntFace},
    geometry::Affine,
    path::{FillRule, Segment},
    vector::{GpuBrush, GpuGradientStop},
};
use std::sync::Arc;
mod layers;
mod number;
mod path;
mod render;
mod values;
mod xml;
pub use path::{native_decode_svg_path, native_svg_path_requirements};
pub use render::NativeSvgGlyphDraw;
type Result<T> = std::result::Result<T, FontError>;
fn fail<T>() -> Result<T> {
    Err(FontError::InvalidGlyph)
}
fn push<T>(output: &mut Vec<T>, value: T) -> Result<()> {
    output.try_reserve(1).map_err(|_| FontError::Capacity)?;
    output.push(value);
    Ok(())
}
fn copy<T: Copy>(values: &[T]) -> Result<Vec<T>> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(values.len())
        .map_err(|_| FontError::Capacity)?;
    output.extend_from_slice(values);
    Ok(output)
}
/// Arithmetic actually selected by this safe portable implementation. The native
/// source's platform `long double` pow/narrowing is a separate qualification gate.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeSvgNumberArithmetic {
    HostF64,
}
/// Complete native auto-closed path inventory and original control-point bounds.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeSvgPathRequirements {
    pub segment_count: usize,
    /// Original minimum X/Y, maximum X/Y; not recomputed tight curve bounds.
    pub bounds: [f32; 4],
    pub fill_rule: FillRule,
}
impl Default for NativeSvgPathRequirements {
    fn default() -> Self {
        Self {
            segment_count: 0,
            bounds: [0.0; 4],
            fill_rule: FillRule::NonZero,
        }
    }
}
/// A canonical layer range. Geometry remains local; gradient points were already
/// transformed into the original glyph coordinate system by the native producer.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeSvgGlyphLayer {
    pub segment_offset: usize,
    pub segment_count: usize,
    pub bounds: [f32; 4],
    pub transform: Affine,
    pub brush_index: u32,
    pub fill_rule: FillRule,
}
impl Default for NativeSvgGlyphLayer {
    fn default() -> Self {
        Self {
            segment_offset: 0,
            segment_count: 0,
            bounds: [0.0; 4],
            transform: Affine::IDENTITY,
            brush_index: 0,
            fill_rule: FillRule::NonZero,
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeSvgGlyphRequirements {
    pub layer_count: usize,
    pub segment_count: usize,
    pub brush_count: usize,
    pub gradient_stop_count: usize,
}
/// Actual immutable XML source and its complete canonical decoded generation.
/// Owned-font construction also retains the selected original SfntFace lease.
#[derive(Clone, Debug)]
pub struct NativeSvgGlyph {
    inner: Arc<GlyphData>,
}
#[derive(Debug)]
struct GlyphData {
    source: Arc<[u8]>,
    font: Option<SfntFace>,
    glyph: u16,
    units: u16,
    layers: Arc<[NativeSvgGlyphLayer]>,
    segments: Arc<[Segment]>,
    brushes: Arc<[GpuBrush]>,
    stops: Arc<[GpuGradientStop]>,
}
impl NativeSvgGlyph {
    /// Decode original XML bytes. Only a selected `glyph{index}` element and
    /// nonempty emitted layers succeed; unsupported SVG elements remain ignored.
    pub fn decode(xml: Arc<[u8]>, glyph: u16, units_per_em: u16) -> Result<Self> {
        Self::decode_source(xml, glyph, units_per_em, None)
    }
    fn decode_source(
        xml: Arc<[u8]>,
        glyph: u16,
        units: u16,
        font: Option<SfntFace>,
    ) -> Result<Self> {
        let decoded = layers::decode(&xml, glyph, units)?;
        Ok(Self {
            inner: Arc::new(GlyphData {
                source: xml,
                font,
                glyph,
                units,
                layers: decoded.layers.into(),
                segments: decoded.segments.into(),
                brushes: decoded.brushes.into(),
                stops: decoded.stops.into(),
            }),
        })
    }
    pub fn source_bytes(&self) -> &[u8] {
        &self.inner.source
    }
    pub fn source_font(&self) -> Option<&SfntFace> {
        self.inner.font.as_ref()
    }
    pub fn glyph(&self) -> u16 {
        self.inner.glyph
    }
    pub fn units_per_em(&self) -> u16 {
        self.inner.units
    }
    pub fn layers(&self) -> &[NativeSvgGlyphLayer] {
        &self.inner.layers
    }
    pub fn segments(&self) -> &[Segment] {
        &self.inner.segments
    }
    pub fn brushes(&self) -> &[GpuBrush] {
        &self.inner.brushes
    }
    pub fn gradient_stops(&self) -> &[GpuGradientStop] {
        &self.inner.stops
    }
    pub fn arithmetic(&self) -> NativeSvgNumberArithmetic {
        NativeSvgNumberArithmetic::HostF64
    }
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.inner, &other.inner)
    }
    pub fn requirements(&self) -> NativeSvgGlyphRequirements {
        NativeSvgGlyphRequirements {
            layer_count: self.inner.layers.len(),
            segment_count: self.inner.segments.len(),
            brush_count: self.inner.brushes.len(),
            gradient_stop_count: self.inner.stops.len(),
        }
    }
    /// All four capacities are admitted before any output byte is overwritten.
    /// Prefixes are copied exactly and caller tail slots remain untouched.
    pub fn copy_into(
        &self,
        layers: &mut [NativeSvgGlyphLayer],
        segments: &mut [Segment],
        brushes: &mut [GpuBrush],
        stops: &mut [GpuGradientStop],
    ) -> Result<NativeSvgGlyphRequirements> {
        let required = self.requirements();
        admit(
            required,
            layers.len(),
            segments.len(),
            brushes.len(),
            stops.len(),
        )?;
        layers[..required.layer_count].copy_from_slice(self.layers());
        segments[..required.segment_count].copy_from_slice(self.segments());
        brushes[..required.brush_count].copy_from_slice(self.brushes());
        stops[..required.gradient_stop_count].copy_from_slice(self.gradient_stops());
        Ok(required)
    }
}
fn admit(
    required: NativeSvgGlyphRequirements,
    layers: usize,
    segments: usize,
    brushes: usize,
    stops: usize,
) -> Result<()> {
    if layers < required.layer_count
        || segments < required.segment_count
        || brushes < required.brush_count
        || stops < required.gradient_stop_count
    {
        Err(FontError::Capacity)
    } else {
        Ok(())
    }
}
/// Original cold two-pass query decodes the complete bounded XML/reference graph.
pub fn native_svg_glyph_requirements(
    xml: &[u8],
    glyph: u16,
    units_per_em: u16,
) -> Result<NativeSvgGlyphRequirements> {
    Ok(layers::decode(xml, glyph, units_per_em)
        .map_err(|_| FontError::InvalidGlyph)?
        .requirements())
}
/// Native caller-output boundary. `required` resets on malformed XML and is
/// populated before Capacity; every caller output remains untouched on failure.
#[allow(clippy::too_many_arguments)]
pub fn native_decode_svg_glyph(
    xml: &[u8],
    glyph: u16,
    units_per_em: u16,
    layers: &mut [NativeSvgGlyphLayer],
    segments: &mut [Segment],
    brushes: &mut [GpuBrush],
    stops: &mut [GpuGradientStop],
    required: &mut NativeSvgGlyphRequirements,
) -> Result<()> {
    *required = NativeSvgGlyphRequirements::default();
    let decoded = layers::decode(xml, glyph, units_per_em).map_err(|_| FontError::InvalidGlyph)?;
    *required = decoded.requirements();
    admit(
        *required,
        layers.len(),
        segments.len(),
        brushes.len(),
        stops.len(),
    )?;
    layers[..required.layer_count].copy_from_slice(&decoded.layers);
    segments[..required.segment_count].copy_from_slice(&decoded.segments);
    brushes[..required.brush_count].copy_from_slice(&decoded.brushes);
    stops[..required.gradient_stop_count].copy_from_slice(&decoded.stops);
    Ok(())
}
impl SfntFace {
    /// Raw native table selection and gzip framing feed the native XML producer,
    /// retaining the actual selected face independently of source disposal.
    pub fn native_svg_glyph_layers(&self, glyph: u16) -> Result<NativeSvgGlyph> {
        let units = self
            .header_metrics()
            .ok_or(FontError::InvalidFace)?
            .units_per_em;
        let document = self.native_svg_glyph_document(glyph)?;
        let size = document.decoded_size()?;
        // XML's original maximum is admitted before allocating an untrusted gzip ISIZE.
        if size == 0 || size > 16 * 1024 * 1024 {
            return fail();
        }
        let mut xml = Vec::new();
        xml.try_reserve_exact(size)
            .map_err(|_| FontError::Capacity)?;
        xml.resize(size, 0);
        let written = document.decode(&mut xml)?;
        xml.truncate(written);
        NativeSvgGlyph::decode_source(xml.into(), glyph, units, Some(self.clone()))
    }
}
