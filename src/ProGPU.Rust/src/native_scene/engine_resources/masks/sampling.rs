//! Exact native analytic, sampled and post-cache picture mask coordinates.
use super::*;
fn affine_uniform(values: [f64; 6], opacity: f32, channel: f32) -> Result<MaskUniforms, Error> {
    if values
        .iter()
        .any(|v| !v.is_finite() || v.abs() > f64::from(f32::MAX))
    {
        return Err(Error::InvalidInput);
    }
    let mut result = MaskUniforms::zeroed();
    result.coordinate0 = [values[0] as f32, values[1] as f32, values[2] as f32, 0.0];
    result.coordinate1 = [values[3] as f32, values[4] as f32, values[5] as f32, 0.0];
    result.options = [1.0, opacity, 1.0, channel];
    Ok(result)
}
pub(super) fn full(size: [u32; 2], opacity: f32) -> MaskUniforms {
    let mut result = MaskUniforms::zeroed();
    result.coordinate1[0] = 1.0 / size[0] as f32;
    result.coordinate1[1] = 1.0 / size[1] as f32;
    result.options = [1.0, opacity, 0.0, 1.0];
    result
}
pub(super) fn analytic(
    source: &wire::LayerMask,
    target: NativeMaskTarget,
) -> Result<MaskUniforms, Error> {
    let mut result = MaskUniforms::zeroed();
    if source.flags & SCENE_LAYER_MASK_AXIS_CLIP_AREA != 0 {
        result.coordinate0[0] = 1.0;
        result.coordinate1[1] = 1.0;
        result.bounds =
            resolve_axis_clip_pixel_bounds(source.bounds, target.extent, target.presentation)
                .ok_or(Error::InvalidInput)?;
        result.options = [5.0, 1.0, 0.0, 0.0];
        return Ok(result);
    }
    let t = localize_transform(
        source.transform,
        target.extent,
        target.presentation,
        target.dpi_scale,
    );
    let dpi = f64::from(target.dpi_scale);
    let [a, b, c, d, x, y] = [t.m11, t.m12, t.m21, t.m22, t.m31, t.m32].map(|v| f64::from(v) * dpi);
    let det = a * d - b * c;
    if !det.is_finite() || det.abs() <= 0.000001 {
        return Err(Error::InvalidInput);
    }
    let inverse = 1.0 / det;
    let values = [
        d * inverse,
        -c * inverse,
        (c * y - d * x) * inverse,
        -b * inverse,
        a * inverse,
        (b * x - a * y) * inverse,
    ];
    result = affine_uniform(values, source.opacity, 0.0)?;
    result.bounds = [
        source.bounds.x,
        source.bounds.y,
        source.bounds.x + source.bounds.width,
        source.bounds.y + source.bounds.height,
    ];
    result.radii_x = source.corner_radii_x;
    result.radii_y = source.corner_radii_y;
    let mut scale = 1.0f32;
    for (available, requested) in [
        (source.bounds.width, result.radii_x[0] + result.radii_x[1]),
        (source.bounds.width, result.radii_x[3] + result.radii_x[2]),
        (source.bounds.height, result.radii_y[0] + result.radii_y[3]),
        (source.bounds.height, result.radii_y[1] + result.radii_y[2]),
    ] {
        if requested > 0.0 {
            scale = scale.min(available / requested);
        }
    }
    scale = scale.clamp(0.0, 1.0);
    for index in 0..4 {
        result.radii_x[index] *= scale;
        result.radii_y[index] *= scale;
    }
    result.options = [2.0, source.opacity, 0.0, 0.0];
    Ok(result)
}
pub(super) fn coverage(
    source: &wire::LayerCoverageMask,
    target: NativeMaskTarget,
) -> Result<MaskUniforms, Error> {
    let values = resolve_mask_uv(
        source.transform,
        source.bounds,
        target.extent,
        target.presentation,
        target.dpi_scale,
    )
    .ok_or(Error::InvalidInput)?;
    affine_uniform(values, source.opacity, 1.0)
}
pub(super) fn picture(
    source: &wire::LayerPictureMask,
    scene: Arc<NativeScene>,
    request: &NativeMaskRequest,
) -> Result<(NativePictureMaskSource, MaskUniforms), Error> {
    let target = request.target;
    let mut frame = resolve_picture_frame(
        source,
        target.extent,
        target.dpi_scale,
        Some(target.presentation),
    )
    .ok_or(Error::InvalidInput)?;
    let header = scene.envelope().header();
    frame.scene_id = header.scene_id;
    frame.generation = header.generation;
    let source_extent = source.flags & SCENE_PICTURE_MASK_SOURCE_EXTENT != 0;
    let mut sampling = if source_extent {
        affine_uniform(
            resolve_mask_uv(
                source.transform,
                source.bounds,
                target.extent,
                target.presentation,
                target.dpi_scale,
            )
            .ok_or(Error::InvalidInput)?,
            source.opacity,
            2.0,
        )?
    } else {
        let mut sampling = MaskUniforms::zeroed();
        sampling.coordinate0[0] = 1.0 / frame.width as f32;
        sampling.coordinate1[1] = 1.0 / frame.height as f32;
        sampling.coordinate0[2] = target.extent.x as f32 / frame.width as f32;
        sampling.coordinate1[2] = target.extent.y as f32 / frame.height as f32;
        sampling.options = [1.0, source.opacity, 1.0, 2.0];
        sampling
    };
    if let Some(state) = request.composite_state {
        let cursor = request.cursor();
        if state.flags & SCENE_STATE_GUIDELINE_SET != 0
            && !cursor.has_per_point_guidelines(&state)?
        {
            let mut bounds = source.bounds;
            if source_extent {
                if source.transform.m12 != 0.0 || source.transform.m21 != 0.0 {
                    return Err(Error::InvalidInput);
                }
                let x0 = bounds.x * source.transform.m11 + source.transform.m31;
                let y0 = bounds.y * source.transform.m22 + source.transform.m32;
                let x1 = (bounds.x + bounds.width) * source.transform.m11 + source.transform.m31;
                let y1 = (bounds.y + bounds.height) * source.transform.m22 + source.transform.m32;
                bounds = wire::ImageRect {
                    x: x0.min(x1),
                    y: y0.min(y1),
                    width: (x1 - x0).abs(),
                    height: (y1 - y0).abs(),
                };
            }
            let (inverse, visible) = cursor
                .composite_rectangle_inverse(&state, bounds)?
                .ok_or(Error::InvalidInput)?;
            if !visible {
                sampling.options[1] = 0.0;
            }
            let origin_x =
                (f64::from(target.presentation.viewport_x) - f64::from(target.extent.x)) as f32;
            let origin_y =
                (f64::from(target.presentation.viewport_y) - f64::from(target.extent.y)) as f32;
            let tx = inverse.m31 * target.presentation.dpi_scale_x + (1.0 - inverse.m11) * origin_x;
            let ty = inverse.m32 * target.presentation.dpi_scale_y + (1.0 - inverse.m22) * origin_y;
            for row in [&mut sampling.coordinate0, &mut sampling.coordinate1] {
                row[2] += row[0] * tx + row[1] * ty;
                row[0] *= inverse.m11;
                row[1] *= inverse.m22;
                if row[..3].iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
            }
            sampling.options[2] = 1.0;
        }
    }
    Ok((
        NativePictureMaskSource {
            scene,
            descriptor: *source,
            frame,
        },
        sampling,
    ))
}
