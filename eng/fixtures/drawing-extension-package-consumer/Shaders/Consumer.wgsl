// Algorithm: A single full-target triangle emits an opaque constant test color.
// Time complexity: O(P) fragment work for P covered pixels; three vertex invocations.
// Space complexity: O(1) private storage, no textures or buffers, one RGBA output per pixel.
// Coverage uses the active render pass and its physical viewport; no sampling or loops.
@vertex fn vs_main(@builtin(vertex_index) vertex: u32) -> @builtin(position) vec4f {
    let x = select(-1.0, 3.0, vertex == 1u);
    let y = select(-1.0, 3.0, vertex == 2u);
    return vec4f(x, y, 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4f { return vec4f(0.1, 0.8, 0.3, 1.0); }
