// Algorithm: Deform water and foliage with bounded analytic waves, then apply wetness, day/night tint, and water highlights per voxel surface.
// Time complexity: O(1) per vertex and fragment with two sine evaluations and fixed material branches.
// Space complexity: O(1) private storage with no texture or storage-buffer samples.
fn progpu_voxel_deform(input: ProGpuVoxelMaterialInput) -> vec3<f32> {
    var position = input.position;
    let wind = normalize(vec2<f32>(
        uniforms.windAndDeformation.x,
        uniforms.windAndDeformation.y) + vec2<f32>(0.0001));
    let windStrength = uniforms.windAndDeformation.z;
    let deformation = uniforms.windAndDeformation.w;
    let phase = dot(position.xz, wind) * 0.72 + input.time * (1.2 + windStrength);

    if (input.material == 7u && input.normal.y > 0.5) {
        position.y += sin(phase * 1.8) * 0.075 * deformation;
        position.y += sin(phase * 3.1 + position.x * 0.37) * 0.025 * deformation;
    } else if (input.material == 6u) {
        let anchored = smoothstep(0.0, 1.0, fract(position.y));
        let sway = wind * sin(phase + position.y * 0.61) *
            (0.035 + 0.06 * windStrength) * deformation * anchored;
        position.x += sway.x;
        position.z += sway.y;
    }
    return position;
}

fn progpu_voxel_shade(input: ProGpuVoxelMaterialInput, baseColor: vec3<f32>) -> vec3<f32> {
    let rain = clamp(uniforms.weatherAndTimeOfDay.x, 0.0, 1.0);
    let wetness = clamp(uniforms.weatherAndTimeOfDay.y, 0.0, 1.0);
    let dayPhase = fract(uniforms.weatherAndTimeOfDay.z);
    let daylight = smoothstep(0.02, 0.28, sin(dayPhase * 6.2831853) * 0.5 + 0.5);
    let nightTint = vec3<f32>(0.34, 0.47, 0.72);
    var color = baseColor * mix(nightTint, vec3<f32>(1.0), 0.34 + daylight * 0.66);
    color *= 1.0 - wetness * rain * 0.16;

    if (input.material == 7u) {
        let crest = 0.5 + 0.5 * sin(
            (input.position.x + input.position.z) * 2.6 + input.time * 2.0);
        color += vec3<f32>(0.05, 0.12, 0.18) * crest * input.normal.y;
    }
    return color;
}
