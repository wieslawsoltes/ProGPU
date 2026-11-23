use super::*;
use crate::native_scene::engine_resources::advanced_blend::{
    NativeAdvancedBlendMode, NativeAdvancedBlendSampling, NativeAxisClipBounds,
};
mod quad;
#[derive(Clone, Copy, Debug)]
pub struct NativeReplayAdvanced {
    pub first_resolve_vertex: u32,
    pub first_copy_vertex: u32,
    pub uniform_offset: u32,
    pub target_size: [u32; 2],
    pub sampling: NativeAdvancedBlendSampling,
}
#[derive(Clone)]
pub struct NativeReplayComposite {
    pub first_vertex: u32,
    pub scissor: NativeScissor,
    pub drawable: bool,
    pub blend_mode: u32,
    pub nearest: bool,
    pub linear_byte_opacity: bool,
    pub replaces_axis_clip_background: bool,
    pub axis_clip_composite: bool,
    pub mask: Option<Arc<NativeMaskRequest>>,
    pub advanced: Option<NativeReplayAdvanced>,
    pub composite_state: Option<wire::State>,
}
#[allow(clippy::too_many_arguments)]
pub(super) fn prepare(
    source: &NativeSceneResourcePlan,
    targets: &NativeLayerTargetPlan,
    occurrence: &NativeLayerOccurrence,
    frame: &NativeCommandFrame,
    source_id: NativeLayerTargetId,
    vertices: &mut Vec<VectorVertex>,
) -> Result<NativeReplayComposite, Error> {
    let layer = occurrence.layer;
    let root = source.frame();
    let presentation = source.layers().presentation();
    let target = frame.target;
    let extent = occurrence.target;
    let size = allocation(targets, source_id)?.size;
    let local = layer.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0;
    let cursor = NativeStateCursor::new(
        source.scene().clone(),
        [presentation.dpi_scale_x, presentation.dpi_scale_y],
    );
    let composite_state = if local || layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0 {
        Some(cursor.read_composite_state(layer.reserved0)?)
    } else {
        None
    };
    let mut scissor = NativeScissor {
        x: 0,
        y: 0,
        width: target.width,
        height: target.height,
        drawable: target.drawable,
    };
    if let Some(state) = composite_state {
        if state.flags & SCENE_STATE_CLIP_RECT != 0 {
            scissor = resolve_target_scissor(
                &state,
                target,
                [root.width, root.height],
                frame.presentation,
            );
        }
    }
    if layer.flags & SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS != 0 {
        scissor = scissor.intersection(resolve_aliased_composite_scissor(
            layer.bounds,
            target,
            frame.presentation,
        ));
    }
    let mut quad = if layer.flags & SCENE_LAYER_CACHE_TILE != 0 {
        let resource = source
            .scene()
            .envelope()
            .resource(layer.reserved1)
            .ok_or(Error::InvalidInput)?;
        let tile = wire::TileComposite::read(resource.payload).ok_or(Error::InvalidInput)?;
        let mut quad = quad::tile(
            [
                tile.output_x,
                tile.output_y,
                tile.output_width,
                tile.output_height,
            ],
            wire::Affine2d {
                m11: tile.m11,
                m12: tile.m12,
                m21: tile.m21,
                m22: tile.m22,
                m31: tile.m31,
                m32: tile.m32,
            },
            [extent.width, extent.height],
            size,
            [tile.address_u, tile.address_v],
            sampling(layer.flags),
            layer.opacity,
        )?;
        for v in &mut quad {
            let p = localize_point(
                wire::Point {
                    x: v.position[0],
                    y: v.position[1],
                },
                target,
                frame.presentation,
                root.dpi_scale,
            );
            v.position = [p.x, p.y];
        }
        quad
    } else if local {
        let state = composite_state.ok_or(Error::InvalidInput)?;
        let legacy = frame.presentation.viewport_x == 0
            && frame.presentation.viewport_y == 0
            && presentation.dpi_scale_x == root.dpi_scale
            && presentation.dpi_scale_y == root.dpi_scale;
        let mut quad = native_transformed_layer_quad(
            extent,
            if legacy {
                target
            } else {
                NativeScissor::default()
            },
            size,
            root.dpi_scale,
            layer.opacity,
            state.transform,
            [presentation.dpi_scale_x, presentation.dpi_scale_y],
        )?;
        if !legacy || state.flags & SCENE_STATE_GUIDELINE_SET != 0 {
            let target_x = target.x as f32 / root.dpi_scale;
            let target_y = target.y as f32 / root.dpi_scale;
            for v in &mut quad {
                let mut p = wire::Point {
                    x: v.position[0] + if legacy { target_x } else { 0.0 },
                    y: v.position[1] + if legacy { target_y } else { 0.0 },
                };
                cursor.snap_composite_point(&state, &mut p)?;
                if legacy {
                    v.position = [p.x - target_x, p.y - target_y];
                } else {
                    p = localize_point(p, target, frame.presentation, root.dpi_scale);
                    v.position = [p.x, p.y];
                }
            }
        }
        quad
    } else {
        native_layer_quad(extent, target, size, root.dpi_scale, layer.opacity)?
    };
    if layer.flags & SCENE_LAYER_CACHE_TILE == 0
        && source.options().engine_flags & ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING != 0
        && extent.width != 0
        && extent.height != 0
    {
        quad::captured(
            &mut quad,
            [extent.width, extent.height],
            size,
            sampling(layer.flags),
            layer.opacity,
        )
        .map_err(|_| Error::UnsupportedExecution)?;
    } else if layer.flags & (SCENE_LAYER_CACHE_FANT | SCENE_LAYER_CACHE_TILE)
        == SCENE_LAYER_CACHE_FANT
    {
        for v in &mut quad {
            v.brush_index = -1.0;
            v.shape_size[0] =
                if source.options().engine_flags & ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING != 0 {
                    -256.0
                } else {
                    -32.0
                };
            v.corner_radius = 0.0;
            v.stroke_thickness = 0.0;
        }
    }
    Ok(NativeReplayComposite {
        first_vertex: append_quad(vertices, quad)?,
        scissor,
        drawable: scissor.drawable,
        blend_mode: layer.blend_mode,
        nearest: layer.flags & SCENE_LAYER_CACHE_NEAREST != 0,
        linear_byte_opacity: layer.flags & SCENE_LAYER_LINEAR_BYTE_OPACITY != 0
            && source.options().format.is_linear_byte_color(),
        replaces_axis_clip_background: occurrence.replaces_axis_clip_background,
        axis_clip_composite: occurrence.byte_axis_clip,
        mask: None,
        advanced: None,
        composite_state,
    })
}
#[allow(clippy::too_many_arguments)]
pub(super) fn finish(
    source: &NativeSceneResourcePlan,
    targets: &NativeLayerTargetPlan,
    occurrence: &NativeLayerOccurrence,
    frame: &NativeCommandFrame,
    source_id: NativeLayerTargetId,
    target_id: NativeLayerTargetId,
    ancestors: &[wire::Layer],
    composite: &mut NativeReplayComposite,
    vertices: &mut Vec<VectorVertex>,
    metrics: &mut NativeReplayMetrics,
) -> Result<(), Error> {
    let layer = occurrence.layer;
    let extent = occurrence.target;
    let target = frame.target;
    let root = source.frame();
    let coverage = composite.replaces_axis_clip_background || composite.axis_clip_composite;
    if composite.drawable && layer.mask_resource_index != SCENE_NO_INDEX {
        let deform = layer.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0
            && composite
                .composite_state
                .is_some_and(|s| s.flags & SCENE_STATE_GUIDELINE_SET != 0);
        composite.mask = mask_request(
            source,
            layer.mask_resource_index,
            frame,
            if coverage { extent } else { target },
            if deform {
                composite.composite_state
            } else {
                None
            },
        )?;
        metrics.mask_request_count += 1;
    }
    let advanced = (BLEND_MULTIPLY..=BLEND_EXCLUSION).contains(&layer.blend_mode)
        || (BLEND_OVERLAY..=BLEND_LUMINOSITY).contains(&layer.blend_mode)
        || coverage;
    if advanced {
        let first_resolve = append_quad(
            vertices,
            native_layer_quad(
                extent,
                extent,
                allocation(targets, source_id)?.size,
                root.dpi_scale,
                layer.opacity,
            )?,
        )?;
        let target_size = allocation(targets, target_id)?.size;
        let full = NativeScissor {
            x: target.x,
            y: target.y,
            width: target_size[0],
            height: target_size[1],
            drawable: true,
        };
        let first_copy = append_quad(
            vertices,
            native_layer_quad(full, full, [root.width, root.height], root.dpi_scale, 1.0)?,
        )?;
        let mode = if composite.replaces_axis_clip_background {
            NativeAdvancedBlendMode::ReplaceAxisBackground {
                linear_byte: source.options().format.is_linear_byte_color(),
            }
        } else if composite.axis_clip_composite {
            NativeAdvancedBlendMode::AxisClipComposite
        } else {
            NativeAdvancedBlendMode::Normal
        };
        let axis = if coverage {
            axis_bounds(source, &layer, ancestors, target, frame.presentation)
        } else {
            None
        };
        let sampling =
            NativeAdvancedBlendSampling::new(extent, target, layer.blend_mode, mode, axis)?;
        composite.advanced = Some(NativeReplayAdvanced {
            first_resolve_vertex: first_resolve,
            first_copy_vertex: first_copy,
            uniform_offset: metrics
                .advanced_count
                .checked_mul(256)
                .ok_or(Error::Capacity)?,
            target_size,
            sampling,
        });
        metrics.advanced_count += 1;
    }
    Ok(())
}
fn sampling(flags: u32) -> u32 {
    if flags & SCENE_LAYER_CACHE_NEAREST != 0 {
        IMAGE_SAMPLING_NEAREST
    } else if flags & SCENE_LAYER_CACHE_FANT != 0 {
        IMAGE_SAMPLING_FANT
    } else {
        IMAGE_SAMPLING_LINEAR
    }
}
fn axis_bounds(
    source: &NativeSceneResourcePlan,
    child: &wire::Layer,
    ancestors: &[wire::Layer],
    target: NativeScissor,
    presentation: wire::Presentation,
) -> Option<NativeAxisClipBounds> {
    let bounds = |layer: &wire::Layer| {
        let r = source
            .scene()
            .envelope()
            .resource(layer.mask_resource_index)?;
        if r.record.kind != SCENE_RESOURCE_LAYER_MASK
            || r.payload.len() != wire::LayerMask::WIRE_SIZE
        {
            return None;
        }
        let m = wire::LayerMask::read(r.payload)?;
        if m.kind != SCENE_LAYER_MASK_ROUNDED_RECTANGLE
            || m.flags != SCENE_LAYER_MASK_AXIS_CLIP_AREA
        {
            return None;
        }
        resolve_axis_clip_pixel_bounds(m.bounds, target, presentation)
    };
    let child = bounds(child)?;
    let mut parents = ancestors.iter().rev();
    let mut parent = bounds(parents.next()?)?;
    for ancestor in parents {
        let Some(b) = bounds(ancestor) else {
            break;
        };
        parent = [
            cpp_max(parent[0], b[0]),
            cpp_max(parent[1], b[1]),
            cpp_min(parent[2], b[2]),
            cpp_min(parent[3], b[3]),
        ];
    }
    Some(NativeAxisClipBounds { parent, child })
}
fn cpp_max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
fn cpp_min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
