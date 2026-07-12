// Algorithm: Evaluate layered trigonometric plasma waves and an optional mouse-centered pulse.
// Time complexity: O(1) per fragment with a fixed number of trigonometric evaluations.
// Space complexity: O(1) local storage.
// Rainbow Plasma / Cosmic Waves
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = fragCoord / inputs.iResolution.xy;
    let t = inputs.iTime * 1.5;

    let r = 0.5 + 0.5 * sin(uv.x * 10.0 + t + sin(uv.y * 5.0 + t));
    let g = 0.5 + 0.5 * sin(uv.y * 10.0 - t + cos(uv.x * 5.0 + t));
    let b = 0.5 + 0.5 * sin((uv.x + uv.y) * 5.0 + t + sin(t));
    var col = vec3<f32>(r, g, b);

    // Pulse circle at mouse position if left-clicked
    let mouse = inputs.iMouse;
    if (mouse.z > 0.0) {
        let dist = distance(fragCoord, mouse.xy);
        let circle = smoothstep(60.0, 58.0, dist);
        col = mix(col, vec3<f32>(1.0, 1.0, 1.0), circle * 0.8);
    }

    return vec4<f32>(col, 1.0);
}
