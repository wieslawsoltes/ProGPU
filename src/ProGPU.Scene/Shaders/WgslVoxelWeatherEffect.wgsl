// Algorithm: Reconstruct a camera ray, integrate nine perspective-spaced world precipitation layers, add restrained lens droplets and horizon mist, and optionally gather camera motion in one fused pass.
// Time complexity: O(M + R + L) per fragment for M=1 or 7 texture samples, R=9 fixed world-rain layers, and L=2 fixed lens-droplet layers.
// Space complexity: O(1) private storage and at most seven source-texture samples per fragment; no output-sized intermediates.
fn progpu_weather_hash(value: vec2<f32>) -> f32 {
    let p = fract(value * vec2<f32>(123.34, 456.21));
    let q = p + dot(p, p + 45.32);
    return fract(q.x * q.y);
}

fn progpu_precipitation_layer(
    rayOrigin: vec3<f32>,
    rayDirection: vec3<f32>,
    cameraRight: vec3<f32>,
    distanceFromCamera: f32,
    layerIndex: f32,
    time: f32,
    wind: vec2<f32>,
    density: f32,
    fallSpeed: f32) -> f32 {
    let position = rayOrigin + rayDirection * distanceFromCamera;
    let worldPlane = vec2<f32>(dot(position, cameraRight), position.y);
    let rainScale = vec2<f32>(density * 1.18, density * 0.68);
    let windOffset = vec2<f32>(
        wind.x * (time * 0.24 + position.y * 0.012),
        -time * fallSpeed * (1.15 + layerIndex * 0.075));
    let rainPosition = worldPlane * rainScale - windOffset;
    let cell = floor(rainPosition);
    let random = progpu_weather_hash(cell + vec2<f32>(layerIndex * 17.17, layerIndex * 9.31));
    let random2 = progpu_weather_hash(cell.yx + vec2<f32>(random * 37.0, layerIndex * 4.73));
    let dropCenter = vec2<f32>(0.12 + random * 0.76, 0.08 + random2 * 0.58);
    let local = fract(rainPosition) - dropCenter;
    let head = 1.0 - smoothstep(0.006, 0.032, length(vec2<f32>(local.x, local.y * 0.6)));
    let tailAlong = step(0.0, local.y) * (1.0 - smoothstep(0.025, 0.36, local.y));
    let tailAcross = 1.0 - smoothstep(0.004, 0.018, abs(local.x));
    let tail = tailAlong * tailAcross;
    let occupancy = step(0.38 + distanceFromCamera * 0.0015, random2);
    let nearWeight = mix(0.58, 1.0, smoothstep(4.0, 16.0, distanceFromCamera));
    return max(head, tail * (0.42 + random * 0.34)) * occupancy * nearWeight;
}

fn progpu_lens_droplet(
    uv: vec2<f32>,
    time: f32,
    layer: f32,
    aspect: f32) -> vec3<f32> {
    let scale = vec2<f32>(7.0 + layer * 3.0, 5.0 + layer * 2.0);
    let movingUv = vec2<f32>(
        uv.x,
        uv.y + time * (0.015 + layer * 0.007));
    let cell = floor(movingUv * scale);
    let random = progpu_weather_hash(cell + vec2<f32>(layer * 23.7, layer * 11.3));
    let center = vec2<f32>(
        0.18 + random * 0.64,
        0.16 + progpu_weather_hash(cell.yx + random) * 0.68);
    var delta = fract(movingUv * scale) - center;
    delta.x *= aspect;
    let radius = length(delta);
    let body = smoothstep(0.105, 0.07, radius) * step(0.935, random);
    let normal = select(vec2<f32>(0.0), normalize(delta), radius > 0.0001);
    let glintDelta = delta - vec2<f32>(-0.035, -0.04);
    let glint = body * (1.0 - smoothstep(0.008, 0.032, length(glintDelta)));
    return vec3<f32>(normal * body, glint);
}

fn progpu_effect_main(input: ProGpuEffectInput) -> vec4<f32> {
    let options = progpu_constant(0u);
    let motionAndWind = progpu_constant(1u);
    let tint = progpu_constant(2u);
    let cameraPositionAndFov = progpu_constant(3u);
    let cameraForwardAndAspect = progpu_constant(4u);
    let cameraRight = progpu_constant(5u);
    let cameraUp = progpu_constant(6u);
    let rainQuality = progpu_constant(7u);
    let time = options.x;
    let rainAmount = clamp(options.y, 0.0, 1.0);
    let motionAmount = clamp(options.z, 0.0, 1.0);
    let enabled = options.w;
    if (enabled < 0.5) {
        return input.color;
    }

    let aspect = max(cameraForwardAndAspect.w, 0.01);
    let ndc = input.uv * 2.0 - vec2<f32>(1.0);
    let rayDirection = normalize(
        cameraForwardAndAspect.xyz +
        cameraRight.xyz * ndc.x * aspect * cameraPositionAndFov.w -
        cameraUp.xyz * ndc.y * cameraPositionAndFov.w);

    let lens0 = progpu_lens_droplet(input.uv, time, 0.0, aspect);
    let lens1 = progpu_lens_droplet(input.uv + vec2<f32>(0.037, 0.071), time, 1.0, aspect);
    let lensNormal = (lens0.xy + lens1.xy * 0.65) * rainAmount;
    let lensRim = (lens0.z + lens1.z * 0.6) * rainAmount;
    let lensUv = clamp(
        input.uv + lensNormal * input.pixelSize * 7.0,
        vec2<f32>(0.0),
        vec2<f32>(1.0));

    let velocity = motionAndWind.xy * motionAmount;
    var color: vec4<f32>;
    if (motionAmount > 0.001 && dot(velocity, velocity) > 0.00000001) {
        color = vec4<f32>(0.0);
        // Seven taps keep the sample count deterministic on desktop and browser WebGPU.
        for (var sampleIndex = 0i; sampleIndex < 7i; sampleIndex++) {
            let along = (f32(sampleIndex) - 3.0) / 3.0;
            let sampleUv = clamp(lensUv + velocity * along, vec2<f32>(0.0), vec2<f32>(1.0));
            let weight = 1.0 - abs(along) * 0.35;
            color += progpu_sample_source(sampleUv) * weight;
        }
        color /= 5.6;
    } else {
        color = progpu_sample_source(lensUv);
    }

    var rain = 0.0;
    var distanceFromCamera = max(rainQuality.z, 1.0);
    // Nine logarithmically spaced layers give stable near detail and inexpensive far density.
    for (var layerIndex = 0i; layerIndex < 9i; layerIndex++) {
        let distanceFade = 1.0 - f32(layerIndex) / 11.0;
        rain += progpu_precipitation_layer(
            cameraPositionAndFov.xyz,
            rayDirection,
            cameraRight.xyz,
            distanceFromCamera,
            f32(layerIndex),
            time,
            motionAndWind.zw,
            rainQuality.x,
            rainQuality.y) * distanceFade;
        distanceFromCamera = min(distanceFromCamera * 1.48, rainQuality.w);
    }

    let rainLight = clamp(rain * 0.46, 0.0, 1.0) * rainAmount;
    let horizonMist = exp(-abs(rayDirection.y) * 5.5) * rainAmount;
    let wetDarkening = 1.0 - rainAmount * 0.14;
    var finalRgb = color.rgb * wetDarkening;
    let luminance = dot(finalRgb, vec3<f32>(0.2126, 0.7152, 0.0722));
    let stormGrade = vec3<f32>(luminance * 0.78, luminance * 0.88, luminance);
    finalRgb = mix(finalRgb, stormGrade, rainAmount * 0.14);
    finalRgb = mix(finalRgb, tint.rgb * 0.44, horizonMist * 0.14);
    finalRgb += tint.rgb * rainLight * tint.a * 0.78;
    finalRgb += vec3<f32>(0.52, 0.67, 0.82) * lensRim * 0.13;
    color = vec4<f32>(finalRgb, color.a);
    return vec4<f32>(color.rgb, color.a);
}
