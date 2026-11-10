//! Original binary clips, storage bounds and target-local pixel domains.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeScissor {
    pub x: u32,
    pub y: u32,
    pub width: u32,
    pub height: u32,
    pub drawable: bool,
}
impl Default for NativeScissor {
    fn default() -> Self {
        Self {
            x: 0,
            y: 0,
            width: 0,
            height: 0,
            drawable: true,
        }
    }
}
impl NativeScissor {
    pub const fn full(size: [u32; 2]) -> Self {
        Self {
            x: 0,
            y: 0,
            width: size[0],
            height: size[1],
            drawable: true,
        }
    }
    pub const fn empty() -> Self {
        Self {
            x: 0,
            y: 0,
            width: 0,
            height: 0,
            drawable: false,
        }
    }
    pub fn intersection(self, other: Self) -> Self {
        let left = self.x.max(other.x);
        let top = self.y.max(other.y);
        let right = (u64::from(self.x) + u64::from(self.width))
            .min(u64::from(other.x) + u64::from(other.width));
        let bottom = (u64::from(self.y) + u64::from(self.height))
            .min(u64::from(other.y) + u64::from(other.height));
        if !self.drawable || !other.drawable || right <= u64::from(left) || bottom <= u64::from(top)
        {
            return Self {
                x: left,
                y: top,
                width: 0,
                height: 0,
                drawable: false,
            };
        }
        Self {
            x: left,
            y: top,
            width: (right - u64::from(left)) as u32,
            height: (bottom - u64::from(top)) as u32,
            drawable: true,
        }
    }
}
fn viewport(p: wire::Presentation) -> NativeScissor {
    NativeScissor {
        x: p.viewport_x,
        y: p.viewport_y,
        width: p.viewport_width,
        height: p.viewport_height,
        drawable: true,
    }
}
pub fn resolve_scissor(
    state: &wire::State,
    size: [u32; 2],
    p: wire::Presentation,
) -> NativeScissor {
    let viewport = viewport(p);
    let mut result = viewport.intersection(NativeScissor::full(size));
    if state.flags & SCENE_STATE_CLIP_RECT == 0 {
        return result;
    }
    if state.flags & SCENE_STATE_CLIP_PIXEL_CENTERS != 0 {
        return resolve_aliased_composite_scissor(state.clip_rect, NativeScissor::full(size), p);
    }
    let edges = presentation_edges(state.clip_rect, p);
    let snap = |v: f32| {
        let rounded = v.round();
        if (v - rounded).abs() < 0.0001 {
            rounded
        } else {
            v
        }
    };
    let left = snap(edges[0].clamp(0.0, size[0] as f32));
    let top = snap(edges[1].clamp(0.0, size[1] as f32));
    let right = snap(edges[2].clamp(0.0, size[0] as f32));
    let bottom = snap(edges[3].clamp(0.0, size[1] as f32));
    if right <= left || bottom <= top {
        result.width = 0;
        result.height = 0;
        result.drawable = false;
        return result;
    }
    result.x = left.floor() as u32;
    result.y = top.floor() as u32;
    result.width = (right.ceil() as u32).wrapping_sub(result.x);
    result.height = (bottom.ceil() as u32).wrapping_sub(result.y);
    result.intersection(viewport)
}
pub fn resolve_scalar_scissor(state: &wire::State, size: [u32; 2], dpi: f32) -> NativeScissor {
    resolve_scissor(state, size, scalar_presentation(size, dpi))
}
pub fn resolve_layer_scissor(
    layer: &wire::Layer,
    size: [u32; 2],
    p: wire::Presentation,
) -> NativeScissor {
    let mut state = identity_state();
    if layer.flags & SCENE_LAYER_BOUNDS != 0 {
        state.flags = SCENE_STATE_CLIP_RECT;
        state.clip_rect = layer.bounds;
    }
    resolve_scissor(&state, size, p)
}
pub fn resolve_scalar_layer_scissor(
    layer: &wire::Layer,
    size: [u32; 2],
    dpi: f32,
) -> NativeScissor {
    resolve_layer_scissor(layer, size, scalar_presentation(size, dpi))
}
pub fn resolve_damage_scissor(damage: wire::ImageRect, p: wire::Presentation) -> NativeScissor {
    let vl = f64::from(p.viewport_x);
    let vt = f64::from(p.viewport_y);
    let vr = vl + f64::from(p.viewport_width);
    let vb = vt + f64::from(p.viewport_height);
    let left = vl.max((vl + f64::from(damage.x) * f64::from(p.dpi_scale_x)).floor());
    let top = vt.max((vt + f64::from(damage.y) * f64::from(p.dpi_scale_y)).floor());
    let right = vr.min((vl + f64::from(damage.x + damage.width) * f64::from(p.dpi_scale_x)).ceil());
    let bottom =
        vb.min((vt + f64::from(damage.y + damage.height) * f64::from(p.dpi_scale_y)).ceil());
    if right <= left || bottom <= top {
        return NativeScissor {
            x: left as u32,
            y: top as u32,
            width: 0,
            height: 0,
            drawable: false,
        };
    }
    NativeScissor {
        x: left as u32,
        y: top as u32,
        width: (right - left) as u32,
        height: (bottom - top) as u32,
        drawable: true,
    }
}
pub fn resolve_target_scissor(
    state: &wire::State,
    target: NativeScissor,
    frame_size: [u32; 2],
    p: wire::Presentation,
) -> NativeScissor {
    let size = [
        target_domain_extent(frame_size[0], target.x, target.width),
        target_domain_extent(frame_size[1], target.y, target.height),
    ];
    let mut clipped = resolve_scissor(state, size, p).intersection(target);
    if !clipped.drawable {
        return NativeScissor::empty();
    }
    clipped.x -= target.x;
    clipped.y -= target.y;
    clipped
}
pub fn resolve_scalar_target_scissor(
    state: &wire::State,
    target: NativeScissor,
    frame_size: [u32; 2],
    dpi: f32,
) -> NativeScissor {
    let size = [
        target_domain_extent(frame_size[0], target.x, target.width),
        target_domain_extent(frame_size[1], target.y, target.height),
    ];
    resolve_target_scissor(state, target, frame_size, scalar_presentation(size, dpi))
}
pub fn resolve_axis_clip_pixel_bounds(
    bounds: wire::ImageRect,
    target: NativeScissor,
    p: wire::Presentation,
) -> Option<[f32; 4]> {
    if !p.dpi_scale_x.is_finite()
        || p.dpi_scale_x <= 0.0
        || !p.dpi_scale_y.is_finite()
        || p.dpi_scale_y <= 0.0
        || ![bounds.x, bounds.y, bounds.width, bounds.height]
            .into_iter()
            .all(f32::is_finite)
        || bounds.width < 0.0
        || bounds.height < 0.0
    {
        return None;
    }
    let mut projected = presentation_edges(bounds, p);
    for (i, v) in projected.iter_mut().enumerate() {
        let local = f64::from(*v) - f64::from(if i % 2 == 0 { target.x } else { target.y });
        if !local.is_finite() || local.abs() > f64::from(f32::MAX) {
            return None;
        }
        *v = local as f32;
    }
    Some(projected)
}
pub fn resolve_aliased_composite_scissor(
    bounds: wire::ImageRect,
    target: NativeScissor,
    p: wire::Presentation,
) -> NativeScissor {
    let edges = presentation_edges(bounds, p);
    let boundary = |v: f32| (f64::from(v) - 0.5).ceil().clamp(0.0, f64::from(u32::MAX)) as u32;
    let [left, top, right, bottom] = edges.map(boundary);
    if right <= left || bottom <= top {
        return NativeScissor::empty();
    }
    let mut clipped = NativeScissor {
        x: left,
        y: top,
        width: right - left,
        height: bottom - top,
        drawable: true,
    }
    .intersection(viewport(p))
    .intersection(target);
    if !clipped.drawable {
        return NativeScissor::empty();
    }
    clipped.x -= target.x;
    clipped.y -= target.y;
    clipped
}
