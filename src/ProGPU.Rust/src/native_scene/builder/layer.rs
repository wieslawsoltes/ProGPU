//! Original owned layer recording and independent source-input scopes.
use super::*;
use crate::native_scene::semantic::values;
mod admission;
mod clear;
mod masks;
mod shader;
pub use masks::NativeSceneCompositeMask;

impl NativeSceneBuilder {
    pub fn push_layer(&mut self, layer: wire::Layer) -> BuildResult<()> {
        self.push_layer_with_source(layer, NativeSceneLayerHitTestMode::Unspecified, None)
    }

    pub fn push_layer_with_source(
        &mut self,
        source: wire::Layer,
        hit_mode: NativeSceneLayerHitTestMode,
        source_content_to_parent: Option<wire::Affine2d>,
    ) -> BuildResult<()> {
        use NativeSceneLayerHitTestMode as H;
        let source_opacity = hit_mode == H::SourceOpacity;
        let source_effect = hit_mode == H::SourceIdentityEffect;
        let source_cache = hit_mode == H::SourceLocalCache;
        let source_mask = hit_mode == H::SourceOpacityMask;
        let source_geometry = source_opacity || source_effect || source_cache || source_mask;
        if source_cache != source_content_to_parent.is_some() {
            return Err(B::InvalidArgument);
        }
        if let Some(frame) = source_content_to_parent {
            let required = SCENE_LAYER_CACHE_CONTENT | SCENE_LAYER_CACHE_LOCAL_SPACE;
            let allowed = required
                | SCENE_LAYER_BOUNDS
                | SCENE_LAYER_CACHE_SHARED
                | SCENE_LAYER_CACHE_NEAREST
                | SCENE_LAYER_CACHE_FANT;
            let determinant = f64::from(frame.m11) * f64::from(frame.m22)
                - f64::from(frame.m12) * f64::from(frame.m21);
            if source.flags & required != required
                || source.flags & !allowed != 0
                || source.blend_mode != BLEND_SRC_OVER
                || source.mask_resource_index != NO_INDEX
                || source.effect_resource_index != NO_INDEX
                || !finite_transform(frame)
                || !determinant.is_finite()
                || determinant == 0.0
            {
                return Err(B::InvalidArgument);
            }
        }
        if (source_opacity || source_mask)
            && (source.flags
                & !(SCENE_LAYER_FORCE_ISOLATION
                    | SCENE_LAYER_BOUNDS
                    | SCENE_LAYER_LINEAR_BYTE_OPACITY)
                != 0
                || source.blend_mode != BLEND_SRC_OVER
                || !source_mask && source.mask_resource_index != NO_INDEX
                || source.effect_resource_index != NO_INDEX)
        {
            return Err(B::InvalidArgument);
        }
        if source_effect
            && (source.flags
                & !(SCENE_LAYER_FORCE_ISOLATION | SCENE_LAYER_BOUNDS | SCENE_LAYER_COMPOSITE_STATE)
                != 0
                || source.blend_mode != BLEND_SRC_OVER)
        {
            return Err(B::InvalidArgument);
        }
        let mut layer = source;
        layer.struct_size = wire::Layer::WIRE_SIZE as u32;
        let local = layer.flags & SCENE_LAYER_CACHE_LOCAL_SPACE != 0;
        let explicit = layer.flags & SCENE_LAYER_COMPOSITE_STATE != 0;
        let tile = layer.flags & SCENE_LAYER_CACHE_TILE != 0;
        if !local && !explicit {
            layer.reserved0 = 0;
        }
        if !tile {
            layer.reserved1 = 0;
        }
        if !values::layer(&layer)
            || tile
                && (layer.reserved1 == NO_INDEX
                    || !self.optional_resource(layer.reserved1, SCENE_RESOURCE_TILE_COMPOSITE))
            || !self.optional_resource(layer.mask_resource_index, SCENE_RESOURCE_LAYER_MASK)
            || !self.optional_resource(layer.effect_resource_index, SCENE_RESOURCE_EFFECT_CHAIN)
                && !self.optional_resource(
                    layer.effect_resource_index,
                    SCENE_RESOURCE_WPF_SHADER_EFFECT,
                )
            || !self.layer_composite_state(&layer, local, explicit, tile)
        {
            return Err(B::InvalidArgument);
        }
        if layer.effect_resource_index != NO_INDEX
            && self.resources[layer.effect_resource_index as usize]
                .record
                .kind
                == SCENE_RESOURCE_WPF_SHADER_EFFECT
            && (layer.flags & SCENE_LAYER_BOUNDS == 0
                || layer.flags & (SCENE_LAYER_BACKDROP | SCENE_LAYER_CACHE_CONTENT) != 0)
        {
            return Err(B::InvalidArgument);
        }
        if layer.mask_resource_index != NO_INDEX {
            let geometry = self.resources[layer.mask_resource_index as usize].source_geometry_clip;
            if source_mask && geometry || source_effect && !geometry {
                return Err(B::InvalidArgument);
            }
        }
        if source_effect && layer.effect_resource_index != NO_INDEX {
            let resource = &self.resources[layer.effect_resource_index as usize];
            if resource.record.kind != SCENE_RESOURCE_EFFECT_CHAIN {
                return Err(B::InvalidArgument);
            }
            for bytes in resource
                .auxiliary
                .chunks_exact(wire::GroupEffect::WIRE_SIZE)
            {
                let effect = wire::GroupEffect::read(bytes).ok_or(B::InvalidState)?;
                if !matches!(
                    effect.kind,
                    GROUP_EFFECT_GAUSSIAN_BLUR | GROUP_EFFECT_BOX_BLUR | GROUP_EFFECT_DROP_SHADOW
                ) {
                    return Err(B::InvalidArgument);
                }
            }
        }
        let materialized = materialized(&layer);
        if self.stack_depth >= MAX_STACK_DEPTH
            || materialized
                && self.materialized_layer_depth >= super::super::MAX_MATERIALIZED_LAYERS
            || self.commands.len() >= MAX_COMMANDS as usize
        {
            return Err(B::CapacityExceeded);
        }
        reserve_append(&mut self.commands, 1)?;
        let mut command = self.new_command(
            SCENE_COMMAND_PUSH_LAYER,
            wire::ImageRect::default(),
            NO_INDEX,
            NO_INDEX,
        );
        command.payload = encode(&[layer])?;
        if source_geometry
            && self.source_geometry_hit_layers.len() == self.source_geometry_hit_layers.capacity()
        {
            let len = self.source_geometry_hit_layers.len();
            let capacity = len.checked_mul(2).ok_or(B::OutOfMemory)?.max(16);
            self.source_geometry_hit_layers
                .try_reserve_exact(capacity - len)
                .map_err(|_| B::OutOfMemory)?;
        }
        if source_geometry {
            self.source_geometry_hit_layers.push(SourceHitLayer {
                command_index: self.commands.len(),
                changes_frame: source_cache,
                content_to_parent: source_content_to_parent
                    .unwrap_or_else(Self::identity_transform),
                geometry_clip: if source_effect {
                    layer.mask_resource_index
                } else {
                    NO_INDEX
                },
            });
        }
        self.commands.push(command);
        let scope = self.stack_depth as usize;
        self.layer_command_indices[scope] = self.commands.len() - 1;
        self.stack_kinds[scope] = if materialized { 3 } else { 2 };
        self.stack_depth += 1;
        self.materialized_layer_depth += u32::from(materialized);
        self.maximum_stack_depth = self.maximum_stack_depth.max(self.stack_depth);
        Ok(())
    }

    pub fn pop_layer(&mut self) -> BuildResult<()> {
        if self.stack_depth == 0 {
            return Err(B::UnbalancedStack);
        }
        let scope = self.stack_depth as usize - 1;
        let kind = self.stack_kinds[scope];
        if !matches!(kind, 2 | 3) {
            return Err(B::UnbalancedStack);
        }
        if self.commands.len() >= MAX_COMMANDS as usize {
            return Err(B::CapacityExceeded);
        }
        reserve_append(&mut self.commands, 1)?;
        self.commands.push(self.new_command(
            SCENE_COMMAND_POP_LAYER,
            wire::ImageRect::default(),
            NO_INDEX,
            NO_INDEX,
        ));
        self.stack_depth -= 1;
        self.stack_kinds[scope] = 0;
        self.layer_command_indices[scope] = 0;
        self.materialized_layer_depth -= u32::from(kind == 3);
        Ok(())
    }

    fn optional_resource(&self, index: u32, kind: u32) -> bool {
        index == NO_INDEX
            || self
                .resources
                .get(index as usize)
                .is_some_and(|resource| resource.record.kind == kind)
    }
    fn layer_composite_state(
        &self,
        layer: &wire::Layer,
        local: bool,
        explicit: bool,
        tile: bool,
    ) -> bool {
        if !local && !explicit {
            return true;
        }
        let Some(resource) = self.resources.get(layer.reserved0 as usize) else {
            return false;
        };
        if resource.record.kind != SCENE_RESOURCE_STATE
            || resource.payload.len() != wire::State::WIRE_SIZE
        {
            return false;
        }
        let Some(state) = wire::State::read(&resource.payload) else {
            return false;
        };
        let allowed = SCENE_STATE_CLIP_RECT | if local { SCENE_STATE_GUIDELINE_SET } else { 0 };
        let guideline_valid = state.flags & SCENE_STATE_GUIDELINE_SET == 0
            || self
                .resources
                .get(state.guideline_resource_index as usize)
                .is_some_and(|r| {
                    r.record.kind == SCENE_RESOURCE_GUIDELINE_SET
                        && wire::GuidelineSet::read(&r.payload)
                            .is_some_and(|g| g.flags & SCENE_GUIDELINE_PER_POINT == 0)
                });
        let canonical = local && !tile || state.transform == Self::identity_transform();
        state.flags & !allowed == 0
            && canonical
            && state.opacity == 1.0
            && state.mask_resource_index == 0
            && guideline_valid
            && (!tile || state.flags == state.flags & SCENE_STATE_CLIP_RECT)
    }
}
fn materialized(layer: &wire::Layer) -> bool {
    layer.flags
        & (SCENE_LAYER_BACKDROP
            | SCENE_LAYER_FORCE_ISOLATION
            | SCENE_LAYER_CACHE_CONTENT
            | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND
            | SCENE_LAYER_IGNORE_ALPHA)
        != 0
        || layer.opacity != 1.0
        || layer.blend_mode != BLEND_SRC_OVER
        || layer.mask_resource_index != NO_INDEX
        || layer.effect_resource_index != NO_INDEX
}
