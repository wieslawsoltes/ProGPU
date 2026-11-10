//! Independent native layer/source/cache flags and group-effect contracts.
use super::*;
pub(crate) fn tile_composite(tile: &wire::TileComposite) -> bool {
    let transform = wire::Affine2d {
        m11: tile.m11,
        m12: tile.m12,
        m21: tile.m21,
        m22: tile.m22,
        m31: tile.m31,
        m32: tile.m32,
    };
    tile.struct_size == wire::TileComposite::WIRE_SIZE as u32
        && tile.reserved == 0
        && tile.reserved0 == 0
        && tile.reserved1 == 0
        && tile.address_u <= IMAGE_ADDRESS_MIRROR_REPEAT
        && tile.address_v <= IMAGE_ADDRESS_MIRROR_REPEAT
        && finite(transform)
        && [
            tile.output_x,
            tile.output_y,
            tile.output_width,
            tile.output_height,
        ]
        .into_iter()
        .all(f32::is_finite)
        && tile.output_width > 0.0
        && tile.output_height > 0.0
        && (tile.output_x + tile.output_width).is_finite()
        && (tile.output_y + tile.output_height).is_finite()
}
pub(crate) fn layer(layer: &wire::Layer) -> bool {
    let known = SCENE_LAYER_BOUNDS
        | SCENE_LAYER_BACKDROP
        | SCENE_LAYER_FORCE_ISOLATION
        | SCENE_LAYER_CACHE_CONTENT
        | SCENE_LAYER_CACHE_LOCAL_SPACE
        | SCENE_LAYER_CACHE_NEAREST
        | SCENE_LAYER_CACHE_FANT
        | SCENE_LAYER_COMPOSITE_STATE
        | SCENE_LAYER_CACHE_TILE
        | SCENE_LAYER_CACHE_SHARED
        | SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS
        | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
        | SCENE_LAYER_IGNORE_ALPHA
        | SCENE_LAYER_LINEAR_BYTE_OPACITY;
    let source_policy = layer.flags
        & (SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | SCENE_LAYER_IGNORE_ALPHA
            | SCENE_LAYER_LINEAR_BYTE_OPACITY)
        != 0;
    let aliased = layer.flags & SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS != 0;
    let local = layer.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0;
    let explicit = layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0;
    let composite = local || explicit;
    let materialized = layer.flags
        & (SCENE_LAYER_BACKDROP
            | SCENE_LAYER_FORCE_ISOLATION
            | SCENE_LAYER_CACHE_CONTENT
            | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | SCENE_LAYER_IGNORE_ALPHA)
        != 0
        || layer.opacity != 1.0
        || layer.blend_mode != BLEND_SRC_OVER
        || layer.mask_resource_index != SCENE_NO_INDEX
        || layer.effect_resource_index != SCENE_NO_INDEX;
    let canonical = layer.flags & SCENE_LAYER_BOUNDS != 0
        || (layer.bounds.x == 0.0
            && layer.bounds.y == 0.0
            && layer.bounds.width == 0.0
            && layer.bounds.height == 0.0);
    layer.struct_size == wire::Layer::WIRE_SIZE as u32
        && layer.flags & !known == 0
        && [
            layer.bounds.x,
            layer.bounds.y,
            layer.bounds.width,
            layer.bounds.height,
        ]
        .into_iter()
        .all(f32::is_finite)
        && layer.bounds.width >= 0.0
        && layer.bounds.height >= 0.0
        && canonical
        && layer.opacity.is_finite()
        && layer.opacity >= 0.0
        && layer.opacity <= 1.0
        && layer.blend_mode <= BLEND_MODULATE
        && (!aliased
            || (layer.flags == (SCENE_LAYER_BOUNDS | SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS)
                && layer.blend_mode == BLEND_SRC
                && layer.opacity == 1.0
                && layer.mask_resource_index == SCENE_NO_INDEX
                && layer.effect_resource_index == SCENE_NO_INDEX
                && layer.content_revision == 0
                && layer.composite_revision == 0))
        && (!source_policy
            || (layer.flags
                & !(SCENE_LAYER_BOUNDS
                    | SCENE_LAYER_FORCE_ISOLATION
                    | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
                    | SCENE_LAYER_IGNORE_ALPHA
                    | SCENE_LAYER_LINEAR_BYTE_OPACITY)
                == 0
                && layer.blend_mode == BLEND_SRC_OVER
                && layer.effect_resource_index == SCENE_NO_INDEX
                && layer.content_revision == 0
                && layer.composite_revision == 0))
        && (!explicit || (!local && materialized))
        && (layer.flags & SCENE_LAYER_CACHE_SHARED == 0 || local)
        && (layer.flags & SCENE_LAYER_CACHE_CONTENT == 0
            || (layer.flags & SCENE_LAYER_BACKDROP == 0
                && layer.content_revision != 0
                && layer.composite_revision != 0))
        && (!local
            || (layer.flags & (SCENE_LAYER_CACHE_CONTENT | SCENE_LAYER_BOUNDS)
                == (SCENE_LAYER_CACHE_CONTENT | SCENE_LAYER_BOUNDS)
                && layer.bounds.x == 0.0
                && layer.bounds.y == 0.0
                && layer.bounds.width > 0.0
                && layer.bounds.height > 0.0
                && layer.blend_mode == BLEND_SRC_OVER
                && layer.effect_resource_index == SCENE_NO_INDEX))
        && (layer.flags & (SCENE_LAYER_CACHE_NEAREST | SCENE_LAYER_CACHE_FANT) == 0 || local)
        && layer.flags & (SCENE_LAYER_CACHE_NEAREST | SCENE_LAYER_CACHE_FANT)
            != (SCENE_LAYER_CACHE_NEAREST | SCENE_LAYER_CACHE_FANT)
        && (composite || layer.reserved0 == 0)
        && if layer.flags & SCENE_LAYER_CACHE_TILE != 0 {
            local && layer.reserved1 != SCENE_NO_INDEX
        } else {
            layer.reserved1 == 0
        }
}
pub(crate) fn effect(effect: &wire::GroupEffect) -> bool {
    if effect.struct_size != wire::GroupEffect::WIRE_SIZE as u32
        || !matches!(
            effect.kind,
            GROUP_EFFECT_GAUSSIAN_BLUR | GROUP_EFFECT_DROP_SHADOW | GROUP_EFFECT_BOX_BLUR
        )
        || effect.flags != 0
        || effect.revision == 0
        || effect.reserved != 0
        || effect.reserved2 != 0
        || ![
            effect.sigma_x,
            effect.sigma_y,
            effect.offset_x,
            effect.offset_y,
            effect.color_r,
            effect.color_g,
            effect.color_b,
            effect.color_a,
        ]
        .into_iter()
        .all(f32::is_finite)
        || effect.sigma_x < 0.0
        || effect.sigma_y < 0.0
    {
        return false;
    }
    if effect.kind == GROUP_EFFECT_GAUSSIAN_BLUR || effect.kind == GROUP_EFFECT_BOX_BLUR {
        return effect.sigma_x > 0.01
            && effect.sigma_y > 0.01
            && effect.offset_x == 0.0
            && effect.offset_y == 0.0
            && effect.color_r == 0.0
            && effect.color_g == 0.0
            && effect.color_b == 0.0
            && effect.color_a == 0.0;
    }
    [
        effect.color_r,
        effect.color_g,
        effect.color_b,
        effect.color_a,
    ]
    .into_iter()
    .all(|v| (0.0..=1.0).contains(&v))
}
