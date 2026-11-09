use super::*;

pub(super) fn state(source: &TextDraw, context: TextPaintContext) -> State {
    let mut draw = source.clone();
    if context.suppress_cached_cleartype && draw.rendering_mode == TextRenderingMode::ClearType {
        draw.rendering_mode = TextRenderingMode::Grayscale;
    }
    let mut transform = context.transform;
    if draw.rotation.abs() > 0.0001 {
        let c = draw.rotation.cos();
        let s = draw.rotation.sin();
        let rotation = Matrix4([
            [c, s, 0.0, 0.0],
            [-s, c, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ]);
        transform = (translation([-draw.position[0], -draw.position[1]])
            * rotation
            * translation(draw.position))
            * transform;
    }
    let color = match draw.brush.as_ref().map(|b| &b.kind) {
        Some(BrushKind::Solid(c)) => *c,
        _ => [1.0; 4],
    };
    let mut effective_color = color;
    effective_color[3] *= draw.brush.as_ref().map_or(1.0, |b| b.opacity) * context.semantic_opacity;
    let shared_style = !context.use_mvp
        && draw
            .brush
            .as_ref()
            .is_none_or(|b| matches!(b.kind, BrushKind::Solid(_)));
    let dpi = positive(context.dpi_scale);
    let zoom = positive(context.static_zoom);
    let transform_scale = transform.stroke_scales().map_or(1.0, |s| s[0]);
    let physical_size = draw.font_size * dpi;
    let mut raster_size = (physical_size * transform_scale * zoom).clamp(4.0, 128.0);
    let mut atlas_scale = physical_size / maximum(raster_size, 0.0001);
    if draw.logical_atlas_resolution {
        raster_size = (draw.font_size * transform_scale * context.static_zoom).clamp(4.0, 128.0);
        atlas_scale = draw.font_size / maximum(raster_size, 0.0001);
    }
    let effective_dpi = dpi * zoom;
    let raster_scale = if draw.logical_atlas_resolution {
        positive(context.static_zoom)
    } else {
        effective_dpi
    };
    let font = draw.font_transform.unwrap_or([1.0, 0.0]);
    let scale_x = if font[0].is_finite() { font[0] } else { 1.0 };
    let skew_x = if font[1].is_finite() { font[1] } else { 0.0 };
    let italic = (if draw.italic { 0.22 } else { 0.0 }) - skew_x;
    // The compositor uses the original static zoom here; ResolveTextRasterization
    // normalizes only its own local copy.
    let atlas_upscale =
        atlas_scale * transform_scale * context.static_zoom * maximum(1.0, scale_x.abs());
    let rotated = transform.0[0][1].abs() > 0.0001
        || transform.0[1][0].abs() > 0.0001
        || transform.0[0][0] < 0.0
        || transform.0[1][1] < 0.0
        || skew_x.abs() > 0.0001
        || scale_x < 0.0;
    let gamma = if skew_x.abs() > 0.0001 || scale_x < 0.0 {
        0.875
    } else {
        let (frame, scale) = if context.gpu_transforms_active {
            let frame = transform * context.camera_view;
            (frame, frame.stroke_scales().map_or(1.0, |s| s[0]))
        } else {
            (transform, transform_scale)
        };
        if frame.0[0][1].abs() > 0.0001
            || frame.0[1][0].abs() > 0.0001
            || frame.0[0][0] < 0.0
            || frame.0[1][1] < 0.0
        {
            0.875
        } else {
            let pixels = draw.font_size.abs() * scale * effective_dpi;
            if pixels.is_finite() && pixels >= 24.0 {
                0.5
            } else {
                0.72
            }
        }
    };
    let policy = TextPathPolicy {
        aliased: draw.rendering_mode == TextRenderingMode::Aliased,
        sample_grid: if draw.rendering_mode == TextRenderingMode::Aliased {
            4
        } else {
            8
        },
        coverage_gamma: draw.coverage_transfer.vector(gamma),
        device_phase_grid: 4,
        quantize_scale: true,
        raster_scale,
        coverage_transfer: draw.coverage_transfer,
    };
    State {
        draw,
        context,
        transform,
        dpi: effective_dpi,
        raster_size,
        atlas_scale,
        scale_x,
        italic,
        rotated,
        atlas_upscale,
        transform_scale,
        color,
        effective_color,
        shared_style,
        policy,
    }
}
pub(super) fn placement(
    position: [f32; 2],
    dpi: f32,
    raster_size: f32,
    rotated: bool,
    mode: TextHintingMode,
) -> Result<(u8, [f32; 2]), Error> {
    if mode == TextHintingMode::IntegerPixel {
        if rotated {
            return Err(Error::UnsupportedExecution);
        }
        return Ok((
            0,
            integer_origin(position, dpi).ok_or(Error::UnsupportedExecution)?,
        ));
    }
    if mode == TextHintingMode::Animated {
        return Ok((0, position));
    }
    let physical = position.map(|v| v * dpi);
    if !rotated && raster_size <= 24.0 {
        let mut integer = physical[0].floor();
        let fraction = physical[0] - integer;
        let mut phase = (fraction * 4.0).round_ties_even() as i32;
        if phase == 4 {
            phase = 0;
            integer += 1.0;
        }
        return Ok((
            phase as u8,
            [integer / dpi, physical[1].round_ties_even() / dpi],
        ));
    }
    if !rotated {
        return Ok((0, physical.map(|v| v.round_ties_even() / dpi)));
    }
    Ok((0, position))
}
pub(super) fn integer_origin(position: [f32; 2], dpi: f32) -> Option<[f32; 2]> {
    if !dpi.is_finite() || dpi <= 0.0 || !finite(position) {
        return None;
    }
    let physical = position.map(|v| v * dpi);
    if !finite(physical) {
        return None;
    }
    let candidate = physical.map(|v| {
        let floor = v.floor();
        (if v - floor >= 0.5 { floor + 1.0 } else { floor }) / dpi
    });
    finite(candidate).then_some(candidate)
}
pub(super) fn integer_descriptor(draw: &TextDraw, frame: Matrix4, dpi: f32) -> bool {
    let TextSource::Positioned(run) = &draw.source else {
        return false;
    };
    let font = run.font.metadata();
    let m = frame.0;
    if !dpi.is_finite()
        || dpi <= 0.0
        || !finite(draw.position)
        || draw.rendering_mode != TextRenderingMode::Aliased
        || !draw.prefer_atlas
        || draw.vector_rendering
        || draw.logical_atlas_resolution
        || draw.rotation != 0.0
        || draw.bold
        || draw.italic
        || draw.font_transform.is_some()
        || font.has_cff_outlines
        || font.has_color_glyphs
        || font.has_bitmap_glyphs
        || !draw.font_size.is_finite()
        || draw.font_size <= 0.0
        || !m[0][0].is_finite()
        || m[0][0] <= 0.0
        || !m[1][1].is_finite()
        || m[1][1] <= 0.0
        || [
            m[0][1], m[0][2], m[0][3], m[1][0], m[1][2], m[1][3], m[2][0], m[2][1], m[2][3],
            m[3][2],
        ]
        .iter()
        .any(|v| *v != 0.0)
        || m[2][2] != 1.0
        || m[3][3] != 1.0
        || !finite([m[3][0], m[3][1]])
        || run.range().is_none()
    {
        return false;
    }
    (draw.font_size * dpi * m[0][0]).is_finite() && (draw.font_size * dpi * m[1][1]).is_finite()
}
pub(super) fn translation(position: [f32; 2]) -> Matrix4 {
    let mut m = Matrix4::IDENTITY;
    m.0[3][0] = position[0];
    m.0[3][1] = position[1];
    m
}
fn positive(value: f32) -> f32 {
    if value.is_finite() && value > 0.0 {
        value
    } else {
        1.0
    }
}
pub(super) fn flags(
    mvp: bool,
    mode: TextRenderingMode,
    color: bool,
    transfer: GlyphCoverageTransfer,
) -> f32 {
    let flag = match mode {
        TextRenderingMode::Aliased => {
            if mvp {
                -2.0
            } else {
                -1.0
            }
        }
        TextRenderingMode::ClearType => {
            if mvp {
                3.0
            } else {
                2.0
            }
        }
        TextRenderingMode::Grayscale => {
            if mvp {
                1.0
            } else {
                0.0
            }
        }
    };
    (if color { flag + 8.0 } else { flag }) + 16.0 * transfer.encoding() as f32
}
