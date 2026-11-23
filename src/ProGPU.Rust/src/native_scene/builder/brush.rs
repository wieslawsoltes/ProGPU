//! Original canonical brush page, preserving local-stop rewriting and solid identity.
use super::*;
use crate::native_scene::semantic::{stored_stop_count, valid_brush_records};

fn valid_input(source: &wire::Brush, stops: &[wire::GradientStop]) -> bool {
    if stops.len() > SCENE_MAX_GRADIENT_STOPS as usize {
        return false;
    }
    let mut local = *source;
    // Native producer input owns this complete local inventory. Its source stop
    // offset is replaced when published, unlike imported semantic table offsets.
    if matches!(source.kind, 1 | 2 | 5 | 6 | 7 | 8 | 10) {
        local.stop_offset = 0;
    }
    stops.len() == stored_stop_count(&local) as usize && valid_brush_records(&local, stops)
}
fn same_color(a: wire::Color, b: wire::Color) -> bool {
    [a.r, a.g, a.b, a.a].map(f32::to_bits) == [b.r, b.g, b.b, b.a].map(f32::to_bits)
}
impl NativeSceneBuilder {
    pub fn add_solid_brush(&mut self, color: wire::Color, opacity: f32) -> BuildResult<u32> {
        if !finite_color(color) || !opacity.is_finite() || !(0.0..=1.0).contains(&opacity) {
            return Err(B::InvalidArgument);
        }
        if let Some(index) = self.brushes.iter().position(|b| {
            b.kind == SCENE_BRUSH_SOLID
                && same_color(b.colors[0], color)
                && b.opacity.to_bits() == opacity.to_bits()
        }) {
            return Ok(index as u32);
        }
        let mut brush = wire::Brush {
            kind: SCENE_BRUSH_SOLID,
            opacity,
            ..Default::default()
        };
        brush.colors[0] = color;
        brush.coordinate_transform0[0] = 1.0;
        brush.coordinate_transform1[1] = 1.0;
        self.add_brush(brush, &[])
    }
    /// Append one complete original native brush and its local owned stop page.
    /// Tile stop fields remain opaque source metadata; all other stored stops
    /// acquire their actual canonical page offset only after every reservation.
    pub fn add_brush(
        &mut self,
        source: wire::Brush,
        stops: &[wire::GradientStop],
    ) -> BuildResult<u32> {
        if !valid_input(&source, stops) {
            return Err(B::InvalidArgument);
        }
        if self.brushes.len() >= SCENE_MAX_BRUSHES as usize
            || stops.len() > SCENE_MAX_GRADIENT_STOPS as usize - self.gradient_stops.len()
        {
            return Err(B::CapacityExceeded);
        }
        let create = self.brush_resource_index == NO_INDEX;
        if create {
            if self.resources.len() >= MAX_RESOURCES as usize {
                return Err(B::CapacityExceeded);
            }
            reserve_append(&mut self.resources, 1)?;
        }
        reserve_append(&mut self.brushes, 1)?;
        reserve_append(&mut self.gradient_stops, stops.len())?;
        if create {
            let mut resource = self.new_resource(SCENE_RESOURCE_BRUSH_TABLE);
            resource.brush_table = true;
            self.brush_resource_index = self.resources.len() as u32;
            self.resources.push(resource);
        }
        let mut brush = source;
        if !stops.is_empty() {
            brush.stop_offset = self.gradient_stops.len() as u32;
            self.gradient_stops.extend_from_slice(stops);
        } else if brush.kind != SCENE_BRUSH_TILE_PATTERN {
            brush.stop_offset = 0;
        }
        let index = self.brushes.len() as u32;
        self.brushes.push(brush);
        Ok(index)
    }
}
