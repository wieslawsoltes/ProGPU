// Algorithm: Transform indexed greedy-chunk vertices through a caller-selectable WGSL material hook and shade procedural voxel materials with environment lighting, selection, and fog.
// Time complexity: O(1) base work per vertex and fragment plus the documented cost of the selected material hook.
// Space complexity: O(1) private storage per invocation with one uniform read per vertex; no storage-buffer or texture samples.
struct VoxelUniforms {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    cameraAndTime: vec4<f32>,
    sunDirectionAndIntensity: vec4<f32>,
    skyColorAndFogStart: vec4<f32>,
    fogEndAndAmbient: vec4<f32>,
    selectedBlock: vec4<f32>,
    windAndDeformation: vec4<f32>,
    weatherAndTimeOfDay: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VoxelUniforms;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) textureCoordinate: vec2<f32>,
    @location(2) material: u32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) worldPosition: vec3<f32>,
    @location(1) textureCoordinate: vec2<f32>,
    @location(2) normal: vec3<f32>,
    @location(3) faceLight: f32,
    @location(4) @interpolate(flat) material: u32,
    @location(5) @interpolate(flat) face: u32,
};

struct ProGpuVoxelMaterialInput {
    position: vec3<f32>,
    normal: vec3<f32>,
    uv: vec2<f32>,
    material: u32,
    face: u32,
    time: f32,
};

fn face_normal(face: u32) -> vec3<f32> {
    switch face {
        case 0u: { return vec3<f32>(1.0, 0.0, 0.0); }
        case 1u: { return vec3<f32>(-1.0, 0.0, 0.0); }
        case 2u: { return vec3<f32>(0.0, 1.0, 0.0); }
        case 3u: { return vec3<f32>(0.0, -1.0, 0.0); }
        case 4u: { return vec3<f32>(0.0, 0.0, 1.0); }
        default: { return vec3<f32>(0.0, 0.0, -1.0); }
    }
}

fn hash21(value: vec2<f32>) -> f32 {
    let p = fract(value * vec2<f32>(123.34, 456.21));
    let q = p + dot(p, p + 45.32);
    return fract(q.x * q.y);
}

fn texel_noise(uv: vec2<f32>, worldPosition: vec3<f32>) -> f32 {
    let texel = floor(fract(uv) * 16.0);
    let seed = texel + floor(worldPosition.xz) * vec2<f32>(7.0, 13.0);
    return hash21(seed);
}

fn material_color(material: u32, face: u32, uv: vec2<f32>, worldPosition: vec3<f32>) -> vec3<f32> {
    let noise = texel_noise(uv, worldPosition);
    if (material == 1u) {
        if (face == 2u) {
            return mix(vec3<f32>(0.16, 0.46, 0.10), vec3<f32>(0.30, 0.66, 0.16), noise);
        }
        let grassBand = step(0.76, fract(uv.y + 0.02 * noise));
        let dirt = mix(vec3<f32>(0.34, 0.20, 0.09), vec3<f32>(0.48, 0.30, 0.13), noise);
        let grass = mix(vec3<f32>(0.13, 0.39, 0.08), vec3<f32>(0.25, 0.58, 0.12), noise);
        return mix(dirt, grass, grassBand);
    }
    if (material == 2u) {
        return mix(vec3<f32>(0.31, 0.18, 0.08), vec3<f32>(0.50, 0.31, 0.14), noise);
    }
    if (material == 3u) {
        let vein = step(0.86, hash21(floor(fract(uv) * 8.0) + vec2<f32>(19.0, 3.0)));
        return mix(
            mix(vec3<f32>(0.34, 0.35, 0.36), vec3<f32>(0.55, 0.56, 0.57), noise),
            vec3<f32>(0.68, 0.69, 0.70),
            vein * 0.45);
    }
    if (material == 4u) {
        return mix(vec3<f32>(0.72, 0.61, 0.34), vec3<f32>(0.91, 0.82, 0.52), noise);
    }
    if (material == 5u) {
        if (face == 2u || face == 3u) {
            let ring = 0.5 + 0.5 * sin(length(fract(uv) - vec2<f32>(0.5)) * 42.0 + noise * 3.0);
            return mix(vec3<f32>(0.28, 0.13, 0.045), vec3<f32>(0.58, 0.32, 0.10), ring);
        }
        let bark = 0.5 + 0.5 * sin(fract(uv.x) * 36.0 + noise * 2.0);
        return mix(vec3<f32>(0.24, 0.11, 0.035), vec3<f32>(0.47, 0.25, 0.075), bark);
    }
    if (material == 6u) {
        let leaf = step(0.19, noise);
        return mix(vec3<f32>(0.08, 0.21, 0.045), mix(vec3<f32>(0.11, 0.36, 0.07), vec3<f32>(0.24, 0.55, 0.12), noise), leaf);
    }
    if (material == 7u) {
        let wave = 0.5 + 0.5 * sin((worldPosition.x + worldPosition.z) * 2.8 + uniforms.cameraAndTime.w * 1.4);
        return mix(vec3<f32>(0.035, 0.24, 0.42), vec3<f32>(0.08, 0.48, 0.69), wave * 0.45 + noise * 0.15);
    }
    return vec3<f32>(1.0, 0.0, 1.0);
}

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    let face = (input.material >> 8u) & 7u;
    let material = input.material & 255u;
    let normal = face_normal(face);
    let materialInput = ProGpuVoxelMaterialInput(
        input.position,
        normal,
        input.textureCoordinate,
        material,
        face,
        uniforms.cameraAndTime.w);
    let worldPosition = progpu_voxel_deform(materialInput);
    var output: VertexOutput;
    output.position = uniforms.projection * uniforms.view * vec4<f32>(worldPosition, 1.0);
    output.worldPosition = worldPosition;
    output.textureCoordinate = input.textureCoordinate;
    output.normal = normal;
    output.faceLight = f32((input.material >> 16u) & 255u) / 255.0;
    output.material = material;
    output.face = face;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    var color = material_color(input.material, input.face, input.textureCoordinate, input.worldPosition);
    let materialInput = ProGpuVoxelMaterialInput(
        input.worldPosition,
        input.normal,
        input.textureCoordinate,
        input.material,
        input.face,
        uniforms.cameraAndTime.w);
    color = progpu_voxel_shade(materialInput, color);
    let sun = normalize(uniforms.sunDirectionAndIntensity.xyz);
    let direct = max(dot(input.normal, sun), 0.0) * uniforms.sunDirectionAndIntensity.w;
    let skyAmbient = mix(uniforms.fogEndAndAmbient.z, uniforms.fogEndAndAmbient.y, input.normal.y * 0.5 + 0.5);
    color *= (skyAmbient + direct) * mix(0.72, 1.0, input.faceLight);

    if (uniforms.selectedBlock.w > 0.5) {
        let ownerBlock = floor(input.worldPosition - input.normal * 0.001);
        if (all(ownerBlock == uniforms.selectedBlock.xyz)) {
            let localUv = fract(input.textureCoordinate);
            let edgeDistance = min(min(localUv.x, 1.0 - localUv.x), min(localUv.y, 1.0 - localUv.y));
            let outline = 1.0 - smoothstep(0.025, 0.065, edgeDistance);
            color = mix(color, vec3<f32>(1.0, 0.92, 0.28), 0.72 * outline + 0.12);
        }
    }

    let distanceToCamera = distance(input.worldPosition, uniforms.cameraAndTime.xyz);
    let fog = smoothstep(uniforms.skyColorAndFogStart.w, uniforms.fogEndAndAmbient.x, distanceToCamera);
    color = mix(color, uniforms.skyColorAndFogStart.rgb, fog);
    return vec4<f32>(color, 1.0);
}
