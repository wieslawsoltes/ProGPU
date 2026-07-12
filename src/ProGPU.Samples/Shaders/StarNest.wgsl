// Algorithm: Accumulate a folded-space volumetric star field with fixed outer and inner iteration counts.
// Time complexity: O(V*F) per fragment for V volume steps and F fold iterations (currently 12*10).
// Space complexity: O(1) local storage.
// Star Nest (Cosmic Space Folding)
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;
    var dir = vec3<f32>(uv * 0.8, 1.0);
    let time = inputs.iTime * 0.05;

    // Rotate camera based on mouse or time
    var s = sin(time * 0.3);
    var c = cos(time * 0.3);
    if (inputs.iMouse.z > 0.0) {
        let mouseNorm = inputs.iMouse.xy / inputs.iResolution.xy;
        s = sin(mouseNorm.x * 3.14);
        c = cos(mouseNorm.x * 3.14);
    }

    dir = vec3<f32>(dir.x * c - dir.z * s, dir.y, dir.x * s + dir.z * c);

    var startPos = vec3<f32>(1.0, 0.5, 0.5);
    startPos += vec3<f32>(time * 2.0, time, -2.0);

    // Volumetric rendering loop
    var s_val = 0.1;
    var fade = 0.5;
    var v = vec3<f32>(0.0);

    for (var r: i32 = 0; r < 12; r = r + 1) {
        var p = startPos + f32(r) * dir * s_val;
        // Float floor-modulo replacement for WebGPU portability
        p = abs(vec3<f32>(0.85) - (p - floor(p / 1.7) * 1.7));

        var pa = 0.0;
        var a = 0.0;
        for (var i: i32 = 0; i < 10; i = i + 1) {
            p = abs(p) / dot(p, p) - vec3<f32>(0.53);
            let len = length(p);
            a = a + abs(len - pa);
            pa = len;
        }

        let dm = max(0.0, 0.85 - a * a * 0.001);
        var a_val = a * a * a;
        v = v + vec3<f32>(dm, dm, dm) * fade;
        v = v + vec3<f32>(s_val, s_val * s_val, s_val * s_val * s_val) * a_val * fade * 0.0003;
        fade = fade * 0.86;
    }

    let intensity = length(v);
    var col = mix(vec3<f32>(intensity * 0.01), v * 0.1, 0.5);
    col = col * 0.35;

    return vec4<f32>(col, 1.0);
}
