// Algorithm: Sphere-trace a rotating torus signed-distance field and estimate normals with central differences.
// Time complexity: O(M*C) per fragment for at most M=80 march steps and constant-cost SDF evaluation C.
// Space complexity: O(1) local storage.
// Spinning Raymarched Torus SDF
fn sdTorus(p: vec3<f32>, t: vec2<f32>) -> f32 {
    let q = vec2<f32>(length(p.xz) - t.x, p.y);
    return length(q) - t.y;
}

fn map(p: vec3<f32>) -> f32 {
    let t = inputs.iTime * 1.0;
    let c = cos(t);
    let s = sin(t);
    var rp = p;
    rp = vec3<f32>(rp.x * c - rp.y * s, rp.x * s + rp.y * c, rp.z);
    rp = vec3<f32>(rp.x, rp.y * c - rp.z * s, rp.y * s + rp.z * c);
    return sdTorus(rp, vec2<f32>(1.5, 0.5));
}

fn getNormal(p: vec3<f32>) -> vec3<f32> {
    let eps = 0.001;
    let h = vec2<f32>(eps, 0.0);
    return normalize(vec3<f32>(
        map(p + h.xyy) - map(p - h.xyy),
        map(p + h.yxy) - map(p - h.yxy),
        map(p + h.yyx) - map(p - h.yyx)
    ));
}

fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;

    let ro = vec3<f32>(0.0, 0.0, -4.0);
    let rd = normalize(vec3<f32>(uv, 1.0));

    var t = 0.0;
    var d = 0.0;
    var hit = false;
    for (var i: i32 = 0; i < 80; i = i + 1) {
        let p = ro + rd * t;
        d = map(p);
        if (d < 0.001) {
            hit = true;
            break;
        }
        t = t + d;
        if (t > 10.0) {
            break;
        }
    }

    var col = vec3<f32>(0.1, 0.12, 0.15);
    if (hit) {
        let p = ro + rd * t;
        let n = getNormal(p);
        let lightDir = normalize(vec3<f32>(1.0, 2.0, -3.0));

        let diff = max(0.0, dot(n, lightDir));
        let viewDir = normalize(ro - p);
        let reflectDir = reflect(-lightDir, n);
        let spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);

        let baseColor = 0.5 + 0.5 * cos(inputs.iTime + p.xyx + vec3<f32>(0.0, 2.0, 4.0));
        col = baseColor * (diff + 0.1) + vec3<f32>(0.5) * spec;
    }

    return vec4<f32>(col, 1.0);
}
