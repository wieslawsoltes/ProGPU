// Algorithm: Expand retained unique CAD mesh edges in physical screen space, classify boundary/crease/silhouette policy from fixed adjacency, and render visible or depth-occluded coverage.
// Time complexity: O(E) vertex and fragment work for E retained unique edges; every edge performs two normal transforms and bounded classification, and every covered fragment performs O(1) coverage plus an optional dash test.
// Space complexity: O(E) read-only edge records plus O(M) mesh records for M retained meshes; O(1) private storage and no auxiliary output storage per invocation.
// Each edge is one six-vertex instanced quad. Manifold classification stores
// exactly two adjacent face normals; non-manifold edges are conservatively
// classified as both creases and silhouettes. Width and dash lengths are in
// physical framebuffer pixels. The depth attachment provides occlusion.
// Exact ProGPU-owned source provenance: GpuMesh3DRecord and transform
// contracts from Mesh3DSolid.wgsl plus physical line expansion from the
// existing ProGPU Line3D.wgsl. The adjacency classification is original to
// this retained CAD edge stream.

struct VSUniforms {
    projection: mat4x4<f32>,
    view: mat4x4<f32>,
    cameraPosition: vec3<f32>,
    _pad: f32,
    visibleEdgeColor: vec4<f32>,
    occludedEdgeColor: vec4<f32>,
    edgeOptions0: vec4<f32>,
    edgeOptions1: vec4<f32>,
};

struct GpuMesh3DRecord {
    modelTransform: mat4x4<f32>,
    normalTransform: mat4x4<f32>,
    color: vec4<f32>,
    lightDirection: vec4<f32>,
    ambientColor: vec4<f32>,
    specularColor: vec4<f32>,
    materialAmbient: vec4<f32>,
    opacity: f32,
    renderMode: f32,
    shadingMode: f32,
    textureSamplingMode: f32,
    textureEffects0: vec4<f32>,
    textureEffects1: vec4<f32>,
    textureInfo: vec4<f32>,
    colorMatrixRed: vec4<f32>,
    colorMatrixGreen: vec4<f32>,
    colorMatrixBlue: vec4<f32>,
    colorMatrixAlpha: vec4<f32>,
    colorMatrixOffset: vec4<f32>,
    textureFlags: vec4<f32>,
    yuvRange: vec4<f32>,
    yuvRed: vec4<f32>,
    yuvGreen: vec4<f32>,
    yuvBlue: vec4<f32>,
    textureSourceRect: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> meshRecords: array<GpuMesh3DRecord>;

struct EdgeInput {
    @location(0) start: vec4<f32>,
    @location(1) end: vec4<f32>,
    @location(2) firstNormal: vec4<f32>,
    @location(3) secondNormal: vec4<f32>,
    @location(4) recordIndex: u32,
    @location(5) topology: u32,
};

struct EdgeOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) edgeCoordinate: f32,
    @location(1) alongPixels: f32,
    @location(2) @interpolate(flat) enabled: u32,
};

fn safeNormalize(value: vec3<f32>) -> vec3<f32> {
    return value / max(length(value), 0.000001);
}

fn edgeCorner(vertexIndex: u32) -> vec2<f32> {
    switch vertexIndex {
        case 0u: { return vec2<f32>(0.0, -0.5); }
        case 1u: { return vec2<f32>(0.0, 0.5); }
        case 2u: { return vec2<f32>(1.0, -0.5); }
        case 3u: { return vec2<f32>(1.0, -0.5); }
        case 4u: { return vec2<f32>(0.0, 0.5); }
        default: { return vec2<f32>(1.0, 0.5); }
    }
}

@vertex
fn vs_main(
    input: EdgeInput,
    @builtin(vertex_index) vertexIndex: u32) -> EdgeOutput {
    let corner = edgeCorner(vertexIndex);
    let record = meshRecords[input.recordIndex];
    let startWorld = record.modelTransform *
        vec4<f32>(input.start.xyz, 1.0);
    let endWorld = record.modelTransform *
        vec4<f32>(input.end.xyz, 1.0);
    let firstNormal = safeNormalize((record.normalTransform *
        vec4<f32>(input.firstNormal.xyz, 0.0)).xyz);
    let secondNormal = safeNormalize((record.normalTransform *
        vec4<f32>(input.secondNormal.xyz, 0.0)).xyz);
    let midpoint = (startWorld.xyz + endWorld.xyz) * 0.5;
    let viewVector = uniforms.cameraPosition - midpoint;
    let viewLength = max(length(viewVector), 0.000001);
    let viewDirection = viewVector / viewLength;
    let firstFacing = dot(firstNormal, viewDirection);
    let secondFacing = dot(secondNormal, viewDirection);
    let boundary = input.topology == 1u;
    let nonManifold = input.topology == 2u;
    let crease = nonManifold ||
        (!boundary && dot(firstNormal, secondNormal) <= uniforms.edgeOptions0.y);
    let silhouette = nonManifold ||
        (boundary && firstFacing >= 0.0) ||
        (!boundary && firstFacing * secondFacing <= 0.0);
    let display = u32(uniforms.edgeOptions1.x + 0.5);
    let enabled =
        (boundary && (display & 1u) != 0u) ||
        (crease && (display & 2u) != 0u) ||
        (silhouette && (display & 4u) != 0u);

    let startClip = uniforms.projection * uniforms.view * startWorld;
    let endClip = uniforms.projection * uniforms.view * endWorld;
    let safeStartW = select(startClip.w, 0.000001, abs(startClip.w) < 0.000001);
    let safeEndW = select(endClip.w, 0.000001, abs(endClip.w) < 0.000001);
    let viewport = max(uniforms.edgeOptions1.yz, vec2<f32>(1.0));
    let startScreen = (startClip.xy / safeStartW) * viewport * 0.5;
    let endScreen = (endClip.xy / safeEndW) * viewport * 0.5;
    let delta = endScreen - startScreen;
    let edgeLength = max(length(delta), 0.000001);
    let expansionNormal = vec2<f32>(-delta.y, delta.x) / edgeLength;
    var clip = select(startClip, endClip, corner.x > 0.5);
    clip = vec4<f32>(
        clip.xy + expansionNormal * corner.y *
            uniforms.edgeOptions0.x * 2.0 * clip.w / viewport,
        clip.zw);
    if (!enabled) {
        clip = vec4<f32>(2.0, 2.0, 2.0, 1.0);
    }

    var output: EdgeOutput;
    output.position = clip;
    output.edgeCoordinate = corner.y * 2.0;
    output.alongPixels = corner.x * edgeLength;
    output.enabled = select(0u, 1u, enabled);
    return output;
}

fn edgeCoverage(input: EdgeOutput) -> f32 {
    return 1.0 - smoothstep(0.80, 1.0, abs(input.edgeCoordinate));
}

@fragment
fn fs_visible(input: EdgeOutput) -> @location(0) vec4<f32> {
    if (input.enabled == 0u) {
        discard;
    }
    let coverage = edgeCoverage(input);
    return vec4<f32>(
        uniforms.visibleEdgeColor.rgb,
        uniforms.visibleEdgeColor.a * coverage);
}

@fragment
fn fs_occluded(input: EdgeOutput) -> @location(0) vec4<f32> {
    if (input.enabled == 0u) {
        discard;
    }
    let dash = uniforms.edgeOptions0.z;
    let gap = uniforms.edgeOptions0.w;
    let period = dash + gap;
    let phase = input.alongPixels - floor(input.alongPixels / period) * period;
    if (gap > 0.0 && phase >= dash) {
        discard;
    }
    let coverage = edgeCoverage(input);
    return vec4<f32>(
        uniforms.occludedEdgeColor.rgb,
        uniforms.occludedEdgeColor.a * coverage);
}
