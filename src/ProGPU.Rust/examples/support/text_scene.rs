//! Shared authored mixed text/vector gallery using an explicitly supplied font.
//! The file remains caller-owned input; no platform font or pixel parity is implied.
use progpu::{
    font::ManagedFont,
    geometry::AnalyticPrimitive,
    scene::Scene,
    text::{
        layout::ManagedTextLayout,
        paint::{
            GlyphCoverageTransfer, PositionedGlyphRun, TextDraw, TextHintingMode, TextPaintContext,
            TextRenderingMode,
        },
        preparation::ManagedPreparationOptions,
        unicode::{
            ManagedMarkProfile, grapheme::ManagedGraphemeProfile,
            normalization::ManagedNormalizationProfile,
        },
    },
    vector::{Brush, BrushKind, GradientStop, LinearGradient, Matrix4},
};
use std::sync::Arc;
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

fn preparation() -> ManagedPreparationOptions {
    // Explicit authored sample profiles, never inferred from the browser or OS.
    ManagedPreparationOptions::new(
        ManagedNormalizationProfile::Unicode17_0,
        ManagedMarkProfile::Unicode16_0,
        ManagedGraphemeProfile::Unicode16_0,
    )
}

pub fn text_scene(font: ManagedFont) -> Result<Scene> {
    let mut scene = Scene::default();
    let ink = Arc::new(Brush::new(BrushKind::Solid([0.91, 0.95, 1.0, 1.0])));
    for (row, (label, size, vector)) in [
        ("ProGPU Rust: shaped text + WebGPU", 30.0, false),
        ("Atlas grayscale: AV ffi office 0123456789", 18.0, false),
        ("Vector outlines: AV ffi office 0123456789", 26.0, true),
        ("Explicit grayscale contrast 6", 24.0, false),
        ("Synthetic bold and italic", 24.0, false),
        ("Affine text: ordered atlas / vector / atlas", 24.0, false),
    ]
    .into_iter()
    .enumerate()
    {
        let top = 18.0 + row as f32 * 72.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [16.0, top, 900.0, 62.0],
            [0.07, 0.10, 0.16, 1.0],
        ))?;
        let layout = ManagedTextLayout::new(
            label.encode_utf16().collect::<Vec<_>>(),
            font.clone(),
            size,
            Default::default(),
            preparation(),
            &[] as &[ManagedFont],
        )?;
        let mut draw = TextDraw::layout(Arc::new(layout));
        draw.position = [30.0, top + 8.0];
        draw.brush = Some(ink.clone());
        draw.vector_rendering = vector;
        if row == 3 {
            draw.coverage_transfer = GlyphCoverageTransfer::grayscale_contrast(6)?;
        }
        if row == 4 {
            draw.bold = true;
            draw.italic = true;
        }
        let mut context = TextPaintContext::default();
        if row == 5 {
            context.transform = Matrix4([
                [1.0, 0.02, 0.0, 0.0],
                [-0.12, 1.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0],
                [54.0, -10.0, 0.0, 1.0],
            ]);
        }
        scene.draw_text(draw, context)?;
        // This overlay must stay after this text and before the next row.
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [300.0, top + 34.0, 110.0, 9.0],
            [0.0, 0.7, 0.9, 0.35],
        ))?;
    }
    let gradient = Arc::new(Brush::new(BrushKind::Linear(LinearGradient::new(
        [30.0, 0.0],
        [850.0, 0.0],
        vec![
            GradientStop {
                color: [0.25, 0.8, 1.0, 1.0],
                offset: 0.0,
            },
            GradientStop {
                color: [1.0, 0.5, 0.25, 1.0],
                offset: 1.0,
            },
        ],
    ))));
    let label = "Vector gradient: original brush coordinates";
    let layout = ManagedTextLayout::new(
        label.encode_utf16().collect::<Vec<_>>(),
        font.clone(),
        25.0,
        Default::default(),
        preparation(),
        &[] as &[ManagedFont],
    )?;
    let mut draw = TextDraw::layout(Arc::new(layout));
    draw.position = [30.0, 458.0];
    draw.brush = Some(gradient);
    draw.vector_rendering = true;
    scene.draw_text(draw, TextPaintContext::default())?;

    let mut x = 0.0;
    let mut indices = Vec::new();
    let mut positions = Vec::new();
    let metadata = font.metadata();
    let integer =
        !metadata.has_cff_outlines && !metadata.has_bitmap_glyphs && !metadata.has_color_glyphs;
    let label = if integer {
        "Positioned aliased: integer physical origins"
    } else {
        "Positioned aliased: fixed placement"
    };
    for c in label.chars() {
        let glyph = font.glyph_index(c as u32)?;
        indices.push(glyph);
        positions.push([x, 0.0]);
        x += font.advance_width(glyph, 19.0)?;
    }
    let mut draw = TextDraw::positioned(
        PositionedGlyphRun::new(font, indices.into(), positions.into()),
        19.0,
    );
    draw.position = [30.25, 555.5];
    draw.brush = Some(ink);
    draw.prefer_atlas = true;
    draw.rendering_mode = TextRenderingMode::Aliased;
    draw.hinting_mode = if integer {
        TextHintingMode::IntegerPixel
    } else {
        TextHintingMode::Fixed
    };
    scene.draw_text(draw, TextPaintContext::default())?;
    Ok(scene)
}
