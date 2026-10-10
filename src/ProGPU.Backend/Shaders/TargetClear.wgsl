// Algorithm: Replace the actual scissored attachment storage with one original
// premultiplied clear color. Source masks belong to their enclosing layer pop,
// not this storage operation. No geometry bounds or source transform are used.
// Time complexity: O(P) for P scissored fragments; O(1) per fragment.
// Space complexity: O(1) vertices/uniform storage; no sampled resources.
@group(0) @binding(0) var<uniform> clearColor: vec4<f32>;

@vertex
fn vs_target_clear(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    var point = vec2<f32>(-1.0, -1.0);
    if (index == 1u) { point.x = 3.0; }
    if (index == 2u) { point.y = 3.0; }
    return vec4<f32>(point, 0.0, 1.0);
}

@fragment
fn fs_target_clear() -> @location(0) vec4<f32> { return clearColor; }
