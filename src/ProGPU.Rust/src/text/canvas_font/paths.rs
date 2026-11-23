//! Original glyph-path scaling, synthetic bold and analytic contour morphing.
use super::*;
use crate::vector::{EffectMatrix, MeasuredPath, PathMeasure, PathVerb};
use std::collections::HashMap;
use wide::f32x4 as V;
/// Original text alignment factor; nominal mapping is independent of bidi/shaping.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum TextAlign {
    /// Original zero alignment factor.
    #[default]
    Left = 0,
    /// Original half factor.
    Center = 1,
    /// Original unit factor.
    Right = 2,
}
impl CanvasFont {
    /// Original actual flipped outline with independent Size/ScaleX/SkewX order.
    /// Synthetic bold retains the original CPU stroke-and-winding producer.
    pub fn glyph_path(&self, glyph: u16) -> Result<Option<RetainedPath>> {
        let Some(font) = &self.typeface else {
            return Ok(None);
        };
        let Some(outline) = font.flipped_outline(glyph)? else {
            return Ok(None);
        };
        let scale_y = self.size / f32::from(font.metadata().units_per_em);
        let scale_x = scale_y * self.scale_x;
        let map = |point: [f32; 2]| {
            let v = (V::from([point[0], point[1], 0.0, 0.0])
                * V::from([scale_x, scale_y, 0.0, 0.0])
                + V::from([point[1], 0.0, 0.0, 0.0]) * V::splat(scale_y) * V::splat(self.skew_x))
            .to_array();
            // Preserve independent Y expression even when SkewX is nonfinite.
            [v[0], point[1] * scale_y]
        };
        let mut path = PathBuilder::new();
        for figure in outline.figures() {
            path.move_to(map(figure.start))?;
            for segment in &figure.segments {
                match segment.kind {
                    0 => path.line_to(map(segment.points[1]))?,
                    1 => path.quadratic_to([map(segment.points[1]), map(segment.points[2])])?,
                    2 => path.cubic_to([
                        map(segment.points[1]),
                        map(segment.points[2]),
                        map(segment.points[3]),
                    ])?,
                    _ => return Err(Error::UnsupportedExecution.into()),
                }
            }
            path.close()?;
        }
        let source = path.detach();
        if !self.embolden || !self.size.is_finite() || self.size <= 0.0 {
            return Ok(Some(source));
        }
        let mut paint = Paint::default();
        paint.style = PaintStyle::Stroke;
        paint.set_stroke_width(self.size * (1.0 / 32.0));
        let Some(stroke) = paint.fill_path(&source, 1.0)? else {
            return Ok(Some(source));
        };
        let stroke = Paint::normalize_stroke_winding(&source, &stroke)?;
        let mut output = source.to_builder()?;
        output.add_path(&stroke, [0.0; 2], PathAddMode::Append)?;
        Ok(Some(output.detach()))
    }
    /// Original 64-unit callback paths and independent final scale matrix. The
    /// callback receives an owned temporary path; no raw borrowed pointers escape.
    pub fn glyph_paths(
        &self,
        glyphs: &[u16],
        mut visit: impl FnMut(Option<RetainedPath>, EffectMatrix) -> Result<()>,
    ) -> Result<()> {
        let mut canonical = self.clone();
        canonical.size = 64.0;
        canonical.baseline_snap = true;
        let scale = self.size / 64.0;
        let matrix = EffectMatrix([scale, 0.0, 0.0, 0.0, scale, 0.0, 0.0, 0.0, 1.0]);
        for &glyph in glyphs {
            visit(canonical.glyph_path(glyph)?, matrix)?;
        }
        Ok(())
    }
    /// Original warped outline path on the first source contour. Glyph widths
    /// and positions retain the same selected font and nominal cumulative order.
    pub fn text_path_on_path(
        &self,
        glyphs: &[u16],
        path: &RetainedPath,
        align: TextAlign,
        origin: [f32; 2],
    ) -> Result<RetainedPath> {
        if glyphs.is_empty() {
            return Ok(PathBuilder::new().detach());
        }
        let widths = self.glyph_widths(glyphs)?;
        let positions = self.glyph_positions(glyphs, origin)?;
        self.positioned_path_on_path(glyphs, &widths, &positions, path, align)
    }
    /// Original explicit width/position variant with whole-input count admission.
    pub fn positioned_path_on_path(
        &self,
        glyphs: &[u16],
        widths: &[f32],
        positions: &[[f32; 2]],
        path: &RetainedPath,
        align: TextAlign,
    ) -> Result<RetainedPath> {
        if widths.len() != glyphs.len() || positions.len() != glyphs.len() {
            return Err(Error::InvalidInput.into());
        }
        let mut output = PathBuilder::new();
        if glyphs.is_empty() {
            return Ok(output.detach());
        }
        let geometry = path.geometry()?;
        let measured = MeasuredPath::from_geometry(&geometry)?;
        let measure = PathMeasure::new(Some(&measured), false, 4.0)?;
        let last = glyphs.len() - 1;
        let text_width = positions[last][0] + widths[last];
        let alignment_factor = align as u8 as f32 * 0.5;
        let aligned_origin = positions[0][0] + (measure.length() - text_width) * alignment_factor;
        let mut cache: HashMap<u16, Option<RetainedPath>> = HashMap::new();
        for (i, &glyph) in glyphs.iter().enumerate() {
            let start = aligned_origin + positions[i][0];
            let end = start + widths[i];
            if end < 0.0 || start > measure.length() {
                continue;
            }
            if !cache.contains_key(&glyph) {
                let outline = self.glyph_path(glyph)?;
                cache.try_reserve(1).map_err(|_| Error::Capacity)?;
                cache.insert(glyph, outline);
            }
            if let Some(source) = cache.get(&glyph).and_then(Option::as_ref) {
                morph(&mut output, source, &measure, start, positions[i][1])?;
            }
        }
        Ok(output.detach())
    }
}
fn morph(
    output: &mut PathBuilder,
    source: &RetainedPath,
    measure: &PathMeasure,
    horizontal: f32,
    vertical: f32,
) -> Result<()> {
    let mut raw = source.raw_iterator()?;
    let mut points = [[0.0; 2]; 4];
    let map = |point: [f32; 2]| {
        let distance = horizontal + point[0];
        let offset = vertical + point[1];
        measure
            .position_and_tangent(distance)
            .map_or([0.0; 2], |(position, tangent)| {
                [
                    position[0] - tangent[1] * offset,
                    position[1] + tangent[0] * offset,
                ]
            })
    };
    loop {
        match raw.next_points(&mut points) {
            PathVerb::Move => output.move_to(map(points[0]))?,
            PathVerb::Line => {
                let mid = [
                    (points[0][0] + points[1][0]) * 0.5,
                    (points[0][1] + points[1][1]) * 0.5,
                ];
                output.quadratic_to([map(mid), map(points[1])])?;
            }
            PathVerb::Quadratic => output.quadratic_to([map(points[1]), map(points[2])])?,
            PathVerb::Conic => {
                output.conic_to([map(points[1]), map(points[2])], raw.conic_weight())?
            }
            PathVerb::Cubic => output.cubic_to([map(points[1]), map(points[2]), map(points[3])])?,
            PathVerb::Close => output.close()?,
            PathVerb::Done => break,
        }
    }
    Ok(())
}
