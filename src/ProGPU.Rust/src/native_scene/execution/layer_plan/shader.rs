//! Exact physical capture/final-sample admission from original scene preflight.
use super::*;
use crate::native_scene::{ResourceView, semantic::values};

struct ShaderFields {
    program: wire::ShaderEffect,
    sampler: u32,
    input: u32,
    capture: wire::ShaderCaptureFrame,
    sample: wire::ShaderSampleFrame,
}
// The retained NativeScene has already performed full version, flags, bytecode
// and source-frame admission. Read the original fields without copying bytecode,
// translating WGSL again or reducing a v6 record to a v5 execution identity.
fn fields(resource: ResourceView<'_>) -> Result<ShaderFields, Error> {
    let bytes = resource.payload;
    let mut result = ShaderFields {
        program: Default::default(),
        sampler: SCENE_NO_INDEX,
        input: SCENE_NO_INDEX,
        capture: Default::default(),
        sample: Default::default(),
    };
    match bytes.len() {
        544 => result.program = wire::ShaderEffect::read(bytes).ok_or(Error::InvalidInput)?,
        560 => {
            let source = wire::ShaderEffectPicture::read(bytes).ok_or(Error::InvalidInput)?;
            result.program = source.program;
            result.sampler = source.sampler_resource_index;
        }
        576 => {
            let source = wire::ShaderEffectDerivatives::read(bytes).ok_or(Error::InvalidInput)?;
            result.program = source.program;
            result.sampler = source.sampler_resource_index;
        }
        648 => {
            let source = wire::ShaderEffectCapture::read(bytes).ok_or(Error::InvalidInput)?;
            result.program = source.program;
            result.sampler = source.sampler_resource_index;
            result.capture = source.frame;
        }
        704 => {
            let source = wire::ShaderEffectSamples::read(bytes).ok_or(Error::InvalidInput)?;
            result.program = source.program;
            result.sampler = source.sampler_resource_index;
            result.input = source.input_resource_index;
            result.sample = source.frame;
        }
        720 => {
            let source = wire::ShaderEffectAffine::read(bytes).ok_or(Error::InvalidInput)?;
            result.program = source.program;
            result.sampler = source.sampler_resource_index;
            result.input = source.input_resource_index;
            result.sample = source.frame.placement;
        }
        _ => return Err(Error::InvalidInput),
    }
    Ok(result)
}
pub(super) fn admit(
    scene: &NativeScene,
    command_index: u32,
    layer: &wire::Layer,
    target: NativeScissor,
    presentation: wire::Presentation,
    active_cache_owners: &[u64],
    resource: ResourceView<'_>,
    metrics: &mut NativeLayerPlanMetrics,
) -> Result<(), Error> {
    let shader = fields(resource)?;
    let left = f64::from(layer.bounds.x) * f64::from(presentation.dpi_scale_x)
        + f64::from(presentation.viewport_x);
    let top = f64::from(layer.bounds.y) * f64::from(presentation.dpi_scale_y)
        + f64::from(presentation.viewport_y);
    let width = f64::from(layer.bounds.width) * f64::from(presentation.dpi_scale_x);
    let height = f64::from(layer.bounds.height) * f64::from(presentation.dpi_scale_y);
    let mut complete = left == f64::from(target.x)
        && top == f64::from(target.y)
        && width == f64::from(target.width)
        && height == f64::from(target.height);
    let f = shader.capture;
    if f.capture_width != 0 {
        // Both operations round to the original float before promotion to double.
        let edge = |value: f32, scale: f32, offset: f32| f64::from(value * scale + offset);
        let expected_left = edge(f.local_left, f.source_scale_x, f.source_offset_x);
        let expected_top = edge(f.local_top, f.source_scale_y, f.source_offset_y);
        let expected_right = edge(f.local_right, f.source_scale_x, f.source_offset_x);
        let expected_bottom = edge(f.local_bottom, f.source_scale_y, f.source_offset_y);
        complete = f.source_dpi_x == f64::from(presentation.dpi_scale_x)
            && f.source_dpi_y == f64::from(presentation.dpi_scale_y)
            && left == expected_left + f64::from(presentation.viewport_x)
            && top == expected_top + f64::from(presentation.viewport_y)
            && width == expected_right - expected_left
            && height == expected_bottom - expected_top
            && i64::from(f.final_x) + i64::from(presentation.viewport_x) == i64::from(target.x)
            && i64::from(f.final_y) + i64::from(presentation.viewport_y) == i64::from(target.y)
            && f.capture_width == target.width
            && f.capture_height == target.height
            && layer.mask_resource_index == SCENE_NO_INDEX
            && layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0
            && active_cache_owners.iter().all(|&owner| owner == 0);
        if layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0 {
            let final_resource = scene
                .envelope()
                .resource(layer.reserved0)
                .ok_or(Error::InvalidInput)?;
            let state = wire::State::read(final_resource.payload).ok_or(Error::InvalidInput)?;
            let m = state.transform;
            let coverage = crate::shader_effect::CaptureFrame {
                local: [f.local_left, f.local_top, f.local_right, f.local_bottom],
                scale: [f.source_scale_x, f.source_scale_y],
                offset: [f.source_offset_x, f.source_offset_y],
                dpi: [f.source_dpi_x, f.source_dpi_y],
                capture_origin: [f.capture_x, f.capture_y],
                capture_size: [f.capture_width, f.capture_height],
                final_origin: [f.final_x, f.final_y],
            }
            .output_coverage()?;
            let clip = state.clip_rect;
            let integral = |edge: f32, dpi: f64| {
                let physical = f64::from(edge) * dpi;
                physical.is_finite() && physical == physical.floor()
            };
            complete = complete
                && state.flags == SCENE_STATE_CLIP_RECT
                && m.m11 == 1.0
                && m.m12 == 0.0
                && m.m21 == 0.0
                && m.m22 == 1.0
                && m.m31 == 0.0
                && m.m32 == 0.0
                && state.opacity == 1.0
                && f64::from(clip.x) * f.source_dpi_x >= f64::from(coverage[0])
                && f64::from(clip.y) * f.source_dpi_y >= f64::from(coverage[1])
                && f64::from(clip.x + clip.width) * f.source_dpi_x <= f64::from(coverage[2])
                && f64::from(clip.y + clip.height) * f.source_dpi_y <= f64::from(coverage[3])
                && integral(clip.x, f.source_dpi_x)
                && integral(clip.y, f.source_dpi_y)
                && integral(clip.x + clip.width, f.source_dpi_x)
                && integral(clip.y + clip.height, f.source_dpi_y);
        }
    }
    if shader.input != SCENE_NO_INDEX {
        let mut source_vector_mask = layer.mask_resource_index == SCENE_NO_INDEX;
        if !source_vector_mask {
            let mask = scene
                .envelope()
                .resource(layer.mask_resource_index)
                .ok_or(Error::InvalidInput)?;
            source_vector_mask = mask_kind(mask)? == SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN
                && wire::LayerVectorMask::read(mask.payload).is_some_and(|m| m.opacity == 1.0);
        }
        complete = shader.sample.source_dpi_x as f32 == presentation.dpi_scale_x
            && shader.sample.source_dpi_y as f32 == presentation.dpi_scale_y
            && source_vector_mask
            && layer.flags & SCENE_LAYER_COMPOSITE_STATE == 0
            && layer.blend_mode == BLEND_SRC_OVER
            && layer.opacity == 1.0
            && scene
                .envelope()
                .command(command_index + 1)
                .is_some_and(|c| c.record.kind == SCENE_COMMAND_POP_LAYER)
            && active_cache_owners.iter().all(|&owner| owner == 0);
    }
    if layer.flags & SCENE_LAYER_BOUNDS == 0
        || layer.flags & (SCENE_LAYER_BACKDROP | SCENE_LAYER_CACHE_CONTENT) != 0
        || !complete
        || width <= 0.0
        || height <= 0.0
        || metrics.effect_node_count == MAX_EFFECT_PASSES
        || metrics.effect_pass_count == MAX_EFFECT_PASSES
    {
        return Err(Error::UnsupportedExecution);
    }
    metrics.effect_node_count += 1;
    metrics.effect_pass_count += 1;
    metrics.shader_effect_count += 1;
    metrics.shader_sample_count += u32::from(shader.input != SCENE_NO_INDEX);
    for (i, dependency) in [shader.input, shader.sampler].into_iter().enumerate() {
        if dependency == SCENE_NO_INDEX || i == 1 && dependency == shader.input {
            continue;
        }
        let sampler = scene
            .envelope()
            .resource(dependency)
            .ok_or(Error::InvalidInput)?;
        let (picture, p) =
            values::read_picture_image(sampler.payload).ok_or(Error::UnsupportedExecution)?;
        if i == 0
            && (picture.width != shader.sample.capture_width
                || picture.height != shader.sample.capture_height)
            || p.dpi_scale_x != 1.0
            || p.dpi_scale_y != 1.0
        {
            return Err(Error::UnsupportedExecution);
        }
        let cost = u64::from(picture.width) * u64::from(picture.height) * 4;
        if cost > MAX_TOTAL_COMPILED_BYTES
            || metrics.shader_sampler_bytes > MAX_TOTAL_COMPILED_BYTES - cost
        {
            return Err(Error::Capacity);
        }
        metrics.shader_sampler_bytes += cost;
    }
    metrics.effect_chain_revision = shader.program.revision;
    Ok(())
}
