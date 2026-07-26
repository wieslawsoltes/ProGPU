// Algorithm: Intersect one primary ray with the voxel-volume AABB, traverse only the in-volume interval with bounded Amanatides-Woo DDA, then shade the first occupied cell.
// Time complexity: O(1) slab intersection plus O(min(D, 512)) per fragment for D in-volume crossed cells; the runtime max-step uniform is clamped to 512.
// Space complexity: O(1) private storage per fragment and O(W*H*D) read-only storage for the dense block volume; no texture samples.
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
    cameraForwardAndTanHalfFov: vec4<f32>,
    cameraRightAndAspect: vec4<f32>,
    cameraUpAndMaxSteps: vec4<f32>,
    volumeOrigin: vec4<f32>,
    volumeSize: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VoxelUniforms;
@group(0) @binding(1) var<storage, read> blocks: array<u32>;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

struct TraceResult {
    material: u32,
    cell: vec3<i32>,
    normal: vec3<f32>,
    distance: f32,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    let x = f32((vertexIndex << 1u) & 2u);
    let y = f32(vertexIndex & 2u);
    var output: VertexOutput;
    output.position = vec4<f32>(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0);
    output.uv = vec2<f32>(x, y);
    return output;
}

fn inside_volume(cell: vec3<i32>, size: vec3<i32>) -> bool {
    return all(cell >= vec3<i32>(0)) && all(cell < size);
}

fn block_at_inside_volume(cell: vec3<i32>, size: vec3<i32>) -> u32 {
    let index = cell.x + size.x * (cell.z + size.z * cell.y);
    return blocks[u32(index)];
}

fn trace_voxels(originWorld: vec3<f32>, direction: vec3<f32>) -> TraceResult {
    let volumeOrigin = uniforms.volumeOrigin.xyz;
    let size = vec3<i32>(uniforms.volumeSize.xyz);
    let origin = originWorld - volumeOrigin;
    let safeDirection = select(
        vec3<f32>(0.000001),
        direction,
        abs(direction) > vec3<f32>(0.000001));
    let signedInverseDirection = 1.0 / safeDirection;
    let bounds0 = (vec3<f32>(0.0) - origin) * signedInverseDirection;
    let bounds1 = (vec3<f32>(size) - origin) * signedInverseDirection;
    let entryByAxis = min(bounds0, bounds1);
    let exitByAxis = max(bounds0, bounds1);
    let entryDistance = max(max(entryByAxis.x, entryByAxis.y), entryByAxis.z);
    let exitDistance = min(min(exitByAxis.x, exitByAxis.y), exitByAxis.z);
    let startDistance = max(entryDistance, 0.0);
    if (exitDistance < startDistance || exitDistance < 0.0) {
        return TraceResult(0u, vec3<i32>(0), vec3<f32>(0.0), 0.0);
    }

    // Move a small distance inside the box so a ray entering exactly on a maximum
    // boundary selects the last valid cell instead of the first out-of-range cell.
    let start = origin + direction * (startDistance + 0.0001);
    var cell = vec3<i32>(floor(start));
    let stepDirection = vec3<i32>(select(
        vec3<f32>(-1.0),
        vec3<f32>(1.0),
        safeDirection >= vec3<f32>(0.0)));
    let inverseDirection = 1.0 / abs(safeDirection);
    let nextBoundary = vec3<f32>(cell) + select(
        vec3<f32>(0.0),
        vec3<f32>(1.0),
        stepDirection > vec3<i32>(0));
    var sideDistance = vec3<f32>(startDistance) + abs((nextBoundary - start) / safeDirection);
    var normal = vec3<f32>(0.0);
    var traveled = startDistance;
    let maxSteps = u32(uniforms.cameraUpAndMaxSteps.w);
    let maximumDistance = min(exitDistance, uniforms.fogEndAndAmbient.x);

    for (var stepIndex = 0u; stepIndex < 512u; stepIndex++) {
        if (stepIndex >= maxSteps || traveled > maximumDistance || !inside_volume(cell, size)) {
            break;
        }
        let material = block_at_inside_volume(cell, size);
        if (material != 0u) {
            return TraceResult(material, cell, normal, traveled);
        }

        if (sideDistance.x <= sideDistance.y && sideDistance.x <= sideDistance.z) {
            cell.x += stepDirection.x;
            traveled = sideDistance.x;
            sideDistance.x += inverseDirection.x;
            normal = vec3<f32>(-f32(stepDirection.x), 0.0, 0.0);
        } else if (sideDistance.y <= sideDistance.z) {
            cell.y += stepDirection.y;
            traveled = sideDistance.y;
            sideDistance.y += inverseDirection.y;
            normal = vec3<f32>(0.0, -f32(stepDirection.y), 0.0);
        } else {
            cell.z += stepDirection.z;
            traveled = sideDistance.z;
            sideDistance.z += inverseDirection.z;
            normal = vec3<f32>(0.0, 0.0, -f32(stepDirection.z));
        }
    }
    return TraceResult(0u, cell, normal, traveled);
}

fn hash31(value: vec3<f32>) -> f32 {
    let p = fract(value * vec3<f32>(0.1031, 0.1030, 0.0973));
    let q = p + dot(p, p.yxz + 33.33);
    return fract((q.x + q.y) * q.z);
}

fn ray_material_color(material: u32, cell: vec3<i32>, normal: vec3<f32>) -> vec3<f32> {
    let noise = hash31(vec3<f32>(cell));
    if (material == 1u) {
        return select(
            mix(vec3<f32>(0.32, 0.19, 0.08), vec3<f32>(0.47, 0.29, 0.12), noise),
            mix(vec3<f32>(0.15, 0.43, 0.08), vec3<f32>(0.29, 0.63, 0.14), noise),
            normal.y > 0.5);
    }
    if (material == 2u) { return mix(vec3<f32>(0.31, 0.18, 0.08), vec3<f32>(0.50, 0.31, 0.14), noise); }
    if (material == 3u) { return mix(vec3<f32>(0.34), vec3<f32>(0.58), noise); }
    if (material == 4u) { return mix(vec3<f32>(0.72, 0.61, 0.34), vec3<f32>(0.91, 0.82, 0.52), noise); }
    if (material == 5u) { return mix(vec3<f32>(0.25, 0.11, 0.035), vec3<f32>(0.51, 0.28, 0.08), noise); }
    if (material == 6u) { return mix(vec3<f32>(0.08, 0.26, 0.05), vec3<f32>(0.22, 0.53, 0.11), noise); }
    if (material == 7u) { return mix(vec3<f32>(0.03, 0.23, 0.42), vec3<f32>(0.08, 0.48, 0.69), noise); }
    return vec3<f32>(1.0, 0.0, 1.0);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let ndc = input.uv * 2.0 - vec2<f32>(1.0);
    let forward = normalize(uniforms.cameraForwardAndTanHalfFov.xyz);
    let right = normalize(uniforms.cameraRightAndAspect.xyz);
    let up = normalize(uniforms.cameraUpAndMaxSteps.xyz);
    let ray = normalize(
        forward +
        right * ndc.x * uniforms.cameraRightAndAspect.w * uniforms.cameraForwardAndTanHalfFov.w -
        up * ndc.y * uniforms.cameraForwardAndTanHalfFov.w);
    let hit = trace_voxels(uniforms.cameraAndTime.xyz, ray);
    if (hit.material == 0u) {
        let horizon = clamp(ray.y * 0.5 + 0.5, 0.0, 1.0);
        return vec4<f32>(mix(uniforms.skyColorAndFogStart.rgb * 0.55, uniforms.skyColorAndFogStart.rgb, horizon), 1.0);
    }

    let worldCell = hit.cell + vec3<i32>(uniforms.volumeOrigin.xyz);
    var color = ray_material_color(hit.material, worldCell, hit.normal);
    let sun = normalize(uniforms.sunDirectionAndIntensity.xyz);
    let direct = max(dot(hit.normal, sun), 0.0) * uniforms.sunDirectionAndIntensity.w;
    let ambient = mix(uniforms.fogEndAndAmbient.z, uniforms.fogEndAndAmbient.y, hit.normal.y * 0.5 + 0.5);
    color *= ambient + direct;
    let wetness = uniforms.weatherAndTimeOfDay.x * uniforms.weatherAndTimeOfDay.y;
    color *= 1.0 - clamp(wetness, 0.0, 1.0) * 0.16;

    if (uniforms.selectedBlock.w > 0.5 &&
        all(vec3<f32>(worldCell) == uniforms.selectedBlock.xyz)) {
        color = mix(color, vec3<f32>(1.0, 0.92, 0.28), 0.45);
    }
    let fog = smoothstep(uniforms.skyColorAndFogStart.w, uniforms.fogEndAndAmbient.x, hit.distance);
    return vec4<f32>(mix(color, uniforms.skyColorAndFogStart.rgb, fog), 1.0);
}
