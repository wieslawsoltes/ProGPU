//! Original nominal strings, retained blobs and first-contour text placement.
use super::*;
use crate::text::{
    canvas_font::{CanvasFont, CanvasFontError, TextAlign},
    paint::{PositionedGlyphRun, TextDraw, TextPaintContext},
    text_blob::{TextBlob, TextBlobRun},
};
use crate::vector::RetainedPath;
type Result<T> = std::result::Result<T, CanvasFontError>;

/// Original path fallback metadata, separate from already transformed outlines.
#[derive(Clone, Copy, Debug)]
pub(super) struct TextPathInfo {
    pub font_size: f32,
    pub font_transform: [f32; 2],
}
fn finite_transform(font: &CanvasFont) -> [f32; 2] {
    [
        if font.scale_x.is_finite() {
            font.scale_x
        } else {
            1.0
        },
        if font.skew_x.is_finite() {
            font.skew_x
        } else {
            0.0
        },
    ]
}
impl TextPathInfo {
    fn font(font: &CanvasFont) -> Self {
        Self {
            font_size: font.size.abs(),
            font_transform: finite_transform(font),
        }
    }
    fn blob(blob: &TextBlob) -> Self {
        let mut result = Self {
            font_size: 0.0,
            font_transform: [1.0, 0.0],
        };
        let mut selected = false;
        for run in blob.runs() {
            let size = run.font().size.abs();
            // MathF.Max propagates NaN; Rust's numeric max does not.
            result.font_size = if size.is_nan() || result.font_size.is_nan() {
                f32::NAN
            } else {
                result.font_size.max(size)
            };
            let transform = finite_transform(run.font());
            if !selected && (transform[0] < 0.0 || transform[1].abs() > 0.0001) {
                result.font_transform = transform;
                selected = true;
            }
        }
        result
    }
}
impl Canvas {
    /// Original nominal UTF-8 scalar mapping, sequential advances and alignment.
    /// This overload does not shape, reorder or discover fallback fonts.
    pub fn draw_string(
        &mut self,
        text: &str,
        origin: [f32; 2],
        align: TextAlign,
        font: &CanvasFont,
        paint: &Paint,
    ) -> Result<()> {
        let glyphs = font.glyphs(text)?;
        if glyphs.is_empty() {
            return Ok(());
        }
        let mut positions = Vec::new();
        positions
            .try_reserve_exact(glyphs.len())
            .map_err(|_| Error::Capacity)?;
        let mut advance = 0.0;
        for &glyph in &glyphs {
            positions.push([advance, 0.0]);
            advance += font.glyph_advance(glyph)?;
        }
        let offset = match align {
            TextAlign::Left => 0.0,
            TextAlign::Center => -advance * 0.5,
            TextAlign::Right => -advance,
        };
        for position in &mut positions {
            position[0] += offset;
        }
        let run = TextBlobRun::new(font.clone(), glyphs.into(), positions.into(), None)?;
        let blob = TextBlob::from_runs(vec![run])?;
        self.draw_text_blob(&blob, origin, paint)?;
        Ok(())
    }
    /// Replay immutable original run arrays. Ordinary fills share their actual
    /// positioned arrays; rotation-scale runs retain one transformed glyph draw
    /// per slot. Shader, stroke and synthetic-bold paths retain path metadata.
    pub fn draw_text_blob(
        &mut self,
        blob: &TextBlob,
        origin: [f32; 2],
        paint: &Paint,
    ) -> Result<()> {
        if paint.shader.is_some() || paint.style != PaintStyle::Fill || blob.has_emboldened_runs() {
            return self.record_text_path(&blob.path(origin)?, paint, TextPathInfo::blob(blob));
        }
        let brush = paint.fill_brush()?;
        let blend = paint.canvas_blend()?;
        let mut scene = Scene::default();
        if blend != LayerBlend::SrcOver {
            scene.push_blend(blend)?;
        }
        for run in blob.runs() {
            let font = run.font();
            let Some(typeface) = &font.typeface else {
                continue;
            };
            let record = |scene: &mut Scene,
                          source: PositionedGlyphRun,
                          position,
                          transform|
             -> Result<()> {
                let mut draw = TextDraw::positioned(source, font.size);
                draw.position = position;
                draw.brush = Some(brush.clone());
                draw.bold = font.embolden;
                draw.font_transform = Some(finite_transform(font));
                draw.vector_rendering = true;
                scene.draw_text(
                    draw,
                    TextPaintContext {
                        transform,
                        semantic_opacity: self.opacity,
                        ..TextPaintContext::default()
                    },
                )?;
                Ok(())
            };
            if let Some(matrices) = run.transforms() {
                for (&glyph, matrix) in run.glyphs().iter().zip(matrices) {
                    let source = PositionedGlyphRun::new(
                        typeface.clone(),
                        Arc::from([glyph]),
                        Arc::from([[0.0; 2]]),
                    );
                    let transform = matrix.matrix().matrix4()
                        * CanvasMatrix::translation(origin).matrix4()
                        * self.matrix.matrix4();
                    record(&mut scene, source, [0.0; 2], transform)?;
                }
            } else if let Some(source) = run.positioned_run() {
                record(&mut scene, source, origin, self.matrix.matrix4())?;
            }
        }
        if blend != LayerBlend::SrcOver {
            scene.pop_blend()?;
        }
        self.push(Command::Leaf(scene))?;
        Ok(())
    }
    /// Original first-contour placement: either morph actual glyph controls or
    /// place their retained rotation-scale matrices at original advance midpoints.
    pub fn draw_string_on_path(
        &mut self,
        text: &str,
        path: &RetainedPath,
        origin: [f32; 2],
        warp_glyphs: bool,
        align: TextAlign,
        font: &CanvasFont,
        paint: &Paint,
    ) -> Result<()> {
        let glyphs = font.glyphs(text)?;
        if warp_glyphs {
            let source = font.text_path_on_path(&glyphs, path, align, origin)?;
            self.record_text_path(&source, paint, TextPathInfo::font(font))
        } else {
            if let Some(blob) = TextBlob::path_positioned(font, &glyphs, path, align, origin)? {
                self.draw_text_blob(&blob, [0.0; 2], paint)?;
            }
            Ok(())
        }
    }
    fn record_text_path(
        &mut self,
        path: &RetainedPath,
        paint: &Paint,
        text: TextPathInfo,
    ) -> Result<()> {
        self.push(Command::Path {
            geometry: path.geometry()?,
            matrix: self.matrix,
            paint: paint.snapshot(),
            inverse: path.is_inverse(),
            text: Some(text),
        })?;
        Ok(())
    }
}
