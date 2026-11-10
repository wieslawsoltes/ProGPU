//! Original SKFont metrics, nominal glyph mapping and CPU geometry facade.
//! These APIs do not perform shaping or infer a system-default font provider.
use crate::{
    Error,
    font::{FontError, ManagedFont},
    paint::{Paint, PaintStyle},
    vector::{PathAddMode, PathBuilder, RetainedPath},
};
mod encoding;
mod paths;
pub use encoding::TextEncoding;
pub use paths::TextAlign;
type Result<T> = std::result::Result<T, CanvasFontError>;

/// Actual owned font/geometry failures, separate from malformed text's original
/// empty-result policy.
#[derive(Debug, thiserror::Error)]
pub enum CanvasFontError {
    /// Selected original font/table/outline failure.
    #[error(transparent)]
    Font(#[from] FontError),
    /// Original geometry/capacity contract failure.
    #[error(transparent)]
    Geometry(#[from] Error),
}
/// Original retained hinting preference; outline queries remain unhinted.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FontHinting {
    /// No requested hinting.
    None,
    /// Slight requested hinting.
    Slight,
    /// Original default.
    #[default]
    Normal,
    /// Full requested hinting.
    Full,
}
/// Original retained edge preference, independent of source raster qualification.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FontEdging {
    /// Binary edges.
    Alias,
    /// Original grayscale preference.
    #[default]
    Antialias,
    /// Original subpixel preference.
    SubpixelAntialias,
}
/// Original font scalar settings and exact selected immutable font owner.
/// `None` is an explicit empty face; it is not an invented system default.
#[derive(Clone, Debug)]
pub struct CanvasFont {
    /// Actual selected source and variation instance.
    pub typeface: Option<ManagedFont>,
    /// Original nominal em size.
    pub size: f32,
    /// Original independent horizontal scaling.
    pub scale_x: f32,
    /// Original independent skew.
    pub skew_x: f32,
    /// Original hinting metadata.
    pub hinting: FontHinting,
    /// Original edge metadata.
    pub edging: FontEdging,
    /// Original subpixel metadata.
    pub subpixel: bool,
    /// Original default true baseline metadata.
    pub baseline_snap: bool,
    /// Original default false auto-hint metadata.
    pub force_auto_hinting: bool,
    /// Original linear-metric metadata.
    pub linear_metrics: bool,
    /// Original explicit synthetic-bold geometry selection.
    pub embolden: bool,
    /// Original embedded-bitmap metadata.
    pub embedded_bitmaps: bool,
}
/// Complete original scaled font metrics, retaining missing optional records.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct CanvasFontMetrics {
    /// Full upper font envelope.
    pub top: f32,
    /// Baseline-relative ascent.
    pub ascent: f32,
    /// Baseline-relative descent.
    pub descent: f32,
    /// Full lower font envelope.
    pub bottom: f32,
    /// Original line gap.
    pub leading: f32,
    /// Advance of source lowercase x, independent of ScaleX.
    pub average_character_width: f32,
    /// Nonnegative full source X envelope.
    pub max_character_width: f32,
    /// Original scaled minimum X.
    pub x_min: f32,
    /// Original scaled maximum X.
    pub x_max: f32,
    /// Source x-height or original half-em fallback.
    pub x_height: f32,
    /// Source cap height or original 0.7-em fallback.
    pub cap_height: f32,
    /// Original optional underline thickness.
    pub underline_thickness: Option<f32>,
    /// Original optional Y-down underline position.
    pub underline_position: Option<f32>,
    /// Original optional strikeout thickness.
    pub strikeout_thickness: Option<f32>,
    /// Original optional Y-down strikeout position.
    pub strikeout_position: Option<f32>,
}
impl CanvasFontMetrics {
    /// Original line spacing, independent of layout or completed-line rounding.
    pub fn spacing(self) -> f32 {
        self.descent - self.ascent + self.leading
    }
}
impl CanvasFont {
    /// Retain a caller-selected real face. System discovery remains a provider API.
    pub fn new(typeface: ManagedFont, size: f32) -> Self {
        Self {
            typeface: Some(typeface),
            ..Self::empty(size)
        }
    }
    /// Explicit empty-face descriptor with original scalar defaults.
    pub fn empty(size: f32) -> Self {
        Self {
            typeface: None,
            size,
            scale_x: 1.0,
            skew_x: 0.0,
            hinting: FontHinting::Normal,
            edging: FontEdging::Antialias,
            subpixel: false,
            baseline_snap: true,
            force_auto_hinting: false,
            linear_metrics: false,
            embolden: false,
            embedded_bitmaps: false,
        }
    }
    /// Original nominal character mapping; out-of-range values return glyph zero.
    pub fn glyph(&self, codepoint: i32) -> Result<u16> {
        if !(0..=0x10ffff).contains(&codepoint) {
            return Ok(0);
        }
        self.typeface.as_ref().map_or(Ok(0), |font| {
            font.glyph_index(codepoint as u32).map_err(Into::into)
        })
    }
    /// Original nonzero nominal glyph test.
    pub fn contains_glyph(&self, codepoint: i32) -> Result<bool> {
        Ok(self.glyph(codepoint)? != 0)
    }
    /// Original selected face advance and separate horizontal scale multiplication.
    pub fn glyph_advance(&self, glyph: u16) -> Result<f32> {
        self.typeface.as_ref().map_or(Ok(0.0), |font| {
            Ok(font.advance_width(glyph, self.size)? * self.scale_x)
        })
    }
    /// Original whole-font metric scaling, with independent optional fields.
    pub fn metrics(&self) -> Result<CanvasFontMetrics> {
        let Some(font) = &self.typeface else {
            return Ok(CanvasFontMetrics::default());
        };
        let m = font.metadata();
        let scale = self.size / f32::from(m.units_per_em.max(1));
        let ascent = -f32::from(m.ascender) * scale;
        let descent = -f32::from(m.descender) * scale;
        let x_min = f32::from(m.bounds[0]) * scale;
        let x_max = f32::from(m.bounds[2]) * scale;
        Ok(CanvasFontMetrics {
            top: minimum(ascent, -f32::from(m.bounds[3]) * scale),
            ascent,
            descent,
            bottom: maximum(descent, -f32::from(m.bounds[1]) * scale),
            leading: f32::from(m.line_gap) * scale,
            average_character_width: font
                .advance_width(font.glyph_index(u32::from('x'))?, self.size)?,
            max_character_width: maximum(0.0, x_max - x_min),
            x_min,
            x_max,
            x_height: m.x_height.map_or(self.size * 0.5, |v| f32::from(v) * scale),
            cap_height: m
                .cap_height
                .map_or(self.size * 0.7, |v| f32::from(v) * scale),
            underline_thickness: m.underline_thickness.map(|v| f32::from(v) * scale),
            underline_position: m.underline_position.map(|v| f32::from(v) * -scale),
            strikeout_thickness: m.strikeout_thickness.map(|v| f32::from(v) * scale),
            strikeout_position: m.strikeout_position.map(|v| f32::from(v) * -scale),
        })
    }
    /// Original current-pen positions, retaining ordered cumulative addition.
    pub fn glyph_positions(&self, glyphs: &[u16], origin: [f32; 2]) -> Result<Vec<[f32; 2]>> {
        let mut out = allocate(glyphs.len(), [0.0; 2])?;
        self.write_glyph_positions(glyphs, &mut out, origin)?;
        Ok(out)
    }
    /// Write the used position prefix only; too-short output rejects before writes.
    pub fn write_glyph_positions(
        &self,
        glyphs: &[u16],
        out: &mut [[f32; 2]],
        origin: [f32; 2],
    ) -> Result<()> {
        if out.len() < glyphs.len() {
            return Err(Error::Capacity.into());
        }
        let mut x = origin[0];
        for (&glyph, point) in glyphs.iter().zip(out) {
            *point = [x, origin[1]];
            x += self.glyph_advance(glyph)?;
        }
        Ok(())
    }
    /// Original scalar-offset equivalent of glyph positions.
    pub fn glyph_offsets(&self, glyphs: &[u16], origin: f32) -> Result<Vec<f32>> {
        let mut out = allocate(glyphs.len(), 0.0)?;
        let mut x = origin;
        for (&glyph, slot) in glyphs.iter().zip(&mut out) {
            *slot = x;
            x += self.glyph_advance(glyph)?;
        }
        Ok(out)
    }
    /// Original width inventory, with no shaping, kerning or paint-dependent advance.
    pub fn glyph_widths(&self, glyphs: &[u16]) -> Result<Vec<f32>> {
        let mut out = allocate(glyphs.len(), 0.0)?;
        for (&glyph, slot) in glyphs.iter().zip(&mut out) {
            *slot = self.glyph_advance(glyph)?;
        }
        Ok(out)
    }
    /// Original independently optional width/bounds outputs. All capacities are
    /// admitted before font execution; unused caller tails remain unchanged.
    pub fn write_glyph_widths(
        &self,
        glyphs: &[u16],
        widths: &mut [f32],
        bounds: &mut [[f32; 4]],
        paint: Option<&Paint>,
    ) -> Result<()> {
        if (!widths.is_empty() && widths.len() < glyphs.len())
            || (!bounds.is_empty() && bounds.len() < glyphs.len())
        {
            return Err(Error::Capacity.into());
        }
        for (i, &glyph) in glyphs.iter().enumerate() {
            if !widths.is_empty() {
                widths[i] = self.glyph_advance(glyph)?;
            }
            if !bounds.is_empty() {
                bounds[i] = self
                    .measured_glyph_bounds(glyph, paint)?
                    .unwrap_or([0.0; 4]);
            }
        }
        Ok(())
    }
    /// Original full nominal advance and bounds, preserving per-glyph float sums.
    pub fn measure_glyphs(&self, glyphs: &[u16], paint: Option<&Paint>) -> Result<(f32, [f32; 4])> {
        let mut advance = 0.0;
        let mut bounds = None;
        for &glyph in glyphs {
            if let Some(mut b) = self.measured_glyph_bounds(glyph, paint)? {
                b[0] += advance;
                b[2] += advance;
                union(&mut bounds, b);
            }
            advance += self.glyph_advance(glyph)?;
        }
        Ok((advance, bounds.unwrap_or([0.0; 4])))
    }
    pub(super) fn measured_glyph_bounds(
        &self,
        glyph: u16,
        paint: Option<&Paint>,
    ) -> Result<Option<[f32; 4]>> {
        if let Some(paint) = paint.filter(|p| p.style != PaintStyle::Fill) {
            let Some(path) = self.glyph_path(glyph)? else {
                return Ok(None);
            };
            let Some(fill) = paint.fill_path(&path, 1.0)? else {
                return Ok(None);
            };
            if fill.is_empty()? {
                return Ok(None);
            }
            let [l, t, r, b] = fill.bounds();
            let bounds = [l.floor(), t.floor(), r.ceil(), b.ceil()];
            return Ok(nonempty(bounds).then_some(bounds));
        }
        let Some(font) = &self.typeface else {
            return Ok(None);
        };
        let Some([l, t, r, b]) = font.glyph_bounds(glyph)? else {
            return Ok(None);
        };
        let scale = self.size / f32::from(font.metadata().units_per_em.max(1));
        let left = f32::from(l) * scale;
        let top = -f32::from(b) * scale;
        let right = f32::from(r) * scale;
        let bottom = -f32::from(t) * scale;
        let x = (wide::f32x4::from([left, right, left, right]) * wide::f32x4::splat(self.scale_x)
            + wide::f32x4::from([top, top, bottom, bottom]) * wide::f32x4::splat(self.skew_x))
        .to_array();
        let bounds = [
            minimum(minimum(x[0], x[1]), minimum(x[2], x[3])).floor() - 1.0,
            top.floor() - 1.0,
            maximum(maximum(x[0], x[1]), maximum(x[2], x[3])).ceil() + 1.0,
            bottom.ceil() + 1.0,
        ];
        Ok(nonempty(bounds).then_some(bounds))
    }
}
pub(super) fn allocate<T: Clone>(count: usize, value: T) -> Result<Vec<T>> {
    let mut out = Vec::new();
    out.try_reserve_exact(count).map_err(|_| Error::Capacity)?;
    out.resize(count, value);
    Ok(out)
}
pub(super) fn nonempty(bounds: [f32; 4]) -> bool {
    // Original SKRect.IsEmpty is equality with the all-zero rectangle.
    bounds != [0.0; 4]
}
pub(super) fn union(bounds: &mut Option<[f32; 4]>, b: [f32; 4]) {
    *bounds = Some(bounds.map_or(b, |a| {
        [
            minimum(a[0], b[0]),
            minimum(a[1], b[1]),
            maximum(a[2], b[2]),
            maximum(a[3], b[3]),
        ]
    }));
}
fn minimum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() | b.to_bits())
    } else {
        a.min(b)
    }
}
fn maximum(a: f32, b: f32) -> f32 {
    if a.is_nan() {
        a
    } else if b.is_nan() {
        b
    } else if a == 0.0 && b == 0.0 {
        f32::from_bits(a.to_bits() & b.to_bits())
    } else {
        a.max(b)
    }
}
