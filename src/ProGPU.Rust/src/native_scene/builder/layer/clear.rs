//! Exact source AA-clip Clear promotion with allocation-free publication.
use super::*;
#[derive(Clone, Copy, Default)]
struct Pending {
    scope: usize,
    command: usize,
    value: wire::Layer,
}
impl NativeSceneBuilder {
    pub fn isolate_current_layer(&mut self) -> BuildResult<()> {
        self.prepare_antialiased_clear_layers(0, true)
    }

    pub fn prepare_antialiased_clear_layers(
        &mut self,
        antialiased_layer_count: u32,
        has_ordinary_owner: bool,
    ) -> BuildResult<()> {
        if antialiased_layer_count > MAX_STACK_DEPTH
            || antialiased_layer_count == 0 && !has_ordinary_owner
        {
            return Err(B::InvalidArgument);
        }
        let count = antialiased_layer_count as usize + usize::from(has_ordinary_owner);
        let mut pending = [Pending::default(); MAX_STACK_DEPTH as usize];
        if count > pending.len() {
            return Err(B::UnbalancedStack);
        }
        let mut scope = self.stack_depth as usize;
        for (i, item) in pending[..count].iter_mut().enumerate() {
            while scope != 0 && self.stack_kinds[scope - 1] == 1 {
                scope -= 1;
            }
            if scope == 0 || !matches!(self.stack_kinds[scope - 1], 2 | 3) {
                return Err(B::UnbalancedStack);
            }
            scope -= 1;
            item.scope = scope;
            item.command = self.layer_command_indices[scope];
            let command = self.commands.get(item.command).ok_or(B::InvalidState)?;
            if command.record.kind != SCENE_COMMAND_PUSH_LAYER
                || command.payload.len() != wire::Layer::WIRE_SIZE
            {
                return Err(B::InvalidState);
            }
            item.value = wire::Layer::read(&command.payload).ok_or(B::InvalidState)?;
            if i >= antialiased_layer_count as usize {
                continue;
            }
            let layer = &item.value;
            let allowed = SCENE_LAYER_BOUNDS | SCENE_LAYER_INITIALIZE_FROM_BACKGROUND;
            if layer.flags & SCENE_LAYER_BOUNDS == 0
                || layer.flags & !allowed != 0
                || layer.opacity != 1.0
                || layer.blend_mode != BLEND_SRC_OVER
                || layer.effect_resource_index != NO_INDEX
                || layer.mask_resource_index as usize >= self.resources.len()
                || layer.content_revision != 0
                || layer.composite_revision != 0
                || layer.reserved0 != 0
                || layer.reserved1 != 0
                || self.stack_kinds[scope] != 3
            {
                return Err(B::InvalidArgument);
            }
            let resource = &self.resources[layer.mask_resource_index as usize];
            if resource.record.kind != SCENE_RESOURCE_LAYER_MASK
                || resource.payload.len() != wire::LayerMask::WIRE_SIZE
            {
                return Err(B::InvalidArgument);
            }
            let mask = wire::LayerMask::read(&resource.payload).ok_or(B::InvalidState)?;
            if mask.kind != SCENE_LAYER_MASK_ROUNDED_RECTANGLE
                || mask.opacity != 1.0
                || mask.flags & !SCENE_LAYER_MASK_AXIS_CLIP_AREA != 0
                || !same_transform_bits(mask.transform, Self::identity_transform())
                || !same_bounds_bits(mask.bounds, layer.bounds)
                || !mask
                    .corner_radii_x
                    .into_iter()
                    .chain(mask.corner_radii_y)
                    .all(|v| v == 0.0)
                || self
                    .source_geometry_hit_layers
                    .iter()
                    .any(|v| v.command_index == item.command)
            {
                return Err(B::InvalidArgument);
            }
        }
        if !has_ordinary_owner {
            while scope != 0 && self.stack_kinds[scope - 1] == 1 {
                scope -= 1;
            }
            if scope != 0 {
                return Err(B::UnbalancedStack);
            }
        }
        let promote = has_ordinary_owner
            && self.stack_kinds[pending[antialiased_layer_count as usize].scope] == 2;
        if promote {
            let owner = pending[antialiased_layer_count as usize];
            let ancestors = self.stack_kinds[..owner.scope]
                .iter()
                .filter(|&&kind| kind == 3)
                .count();
            let mut children = [0usize; MAX_STACK_DEPTH as usize];
            let mut materialized_children = [false; MAX_STACK_DEPTH as usize];
            let (mut depth, mut live, mut peak) = (0usize, 0usize, 0usize);
            for (index, command) in self.commands.iter().enumerate().skip(owner.command + 1) {
                if command.record.kind == SCENE_COMMAND_PUSH_LAYER {
                    if depth == children.len() || command.payload.len() != wire::Layer::WIRE_SIZE {
                        return Err(B::InvalidState);
                    }
                    let child = wire::Layer::read(&command.payload).ok_or(B::InvalidState)?;
                    let active = materialized(&child);
                    children[depth] = index;
                    materialized_children[depth] = active;
                    depth += 1;
                    live += usize::from(active);
                    peak = peak.max(live);
                } else if command.record.kind == SCENE_COMMAND_POP_LAYER {
                    if depth == 0 {
                        return Err(B::InvalidState);
                    }
                    depth -= 1;
                    live -= usize::from(materialized_children[depth]);
                }
            }
            let mut open = 0;
            for i in owner.scope + 1..self.stack_depth as usize {
                if self.stack_kinds[i] == 1 {
                    continue;
                }
                if open >= depth || children[open] != self.layer_command_indices[i] {
                    return Err(B::InvalidState);
                }
                open += 1;
            }
            if open != depth {
                return Err(B::InvalidState);
            }
            if ancestors + 1 + peak > super::super::super::MAX_MATERIALIZED_LAYERS as usize {
                return Err(B::CapacityExceeded);
            }
        }
        // All destinations and historical/live scopes were proved above. These
        // exact fixed writes cannot allocate or partially publish a rejected set.
        for item in &mut pending[..antialiased_layer_count as usize] {
            item.value.flags |= SCENE_LAYER_INITIALIZE_FROM_BACKGROUND;
            item.value
                .write_into(&mut self.commands[item.command].payload)
                .expect("admitted layer destination");
        }
        if promote {
            let owner = &mut pending[antialiased_layer_count as usize];
            owner.value.flags |= SCENE_LAYER_FORCE_ISOLATION;
            owner
                .value
                .write_into(&mut self.commands[owner.command].payload)
                .expect("admitted layer destination");
            self.stack_kinds[owner.scope] = 3;
            self.materialized_layer_depth += 1;
        }
        Ok(())
    }
}
fn same_transform_bits(a: wire::Affine2d, b: wire::Affine2d) -> bool {
    [a.m11, a.m12, a.m21, a.m22, a.m31, a.m32]
        .into_iter()
        .zip([b.m11, b.m12, b.m21, b.m22, b.m31, b.m32])
        .all(|(x, y)| x.to_bits() == y.to_bits())
}
fn same_bounds_bits(a: wire::ImageRect, b: wire::ImageRect) -> bool {
    [a.x, a.y, a.width, a.height]
        .into_iter()
        .zip([b.x, b.y, b.width, b.height])
        .all(|(x, y)| x.to_bits() == y.to_bits())
}
