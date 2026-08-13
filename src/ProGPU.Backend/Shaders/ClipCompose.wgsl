// Algorithm: Sample one retained path-atlas quad into an R8 node mask, then combine that mask with prior ordered clip coverage using intersection or difference.
// Time complexity: O(P + W*H) per changed clip node for P covered quad fragments and a W by H target-wide composition; stable retained replay performs no work in this module.
// Space complexity: O(1) shader-private storage and two R8 texture reads plus one R8 attachment write per composed pixel; the native owner retains one node and two ping-pong target-sized R8 textures.
struct ClipVertexInput {
    @location(0) position: vec2<f32>,
    @location(1) atlasUv: vec2<f32>,
};

struct ClipVertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) atlasUv: vec2<f32>,
};

struct ClipComposeUniforms {
    operation: u32,
    first: u32,
    width: u32,
    height: u32,
};

@group(0) @binding(0) var clipSampler: sampler;
@group(0) @binding(1) var nodeOrAtlasTexture: texture_2d<f32>;
@group(0) @binding(2) var previousTexture: texture_2d<f32>;
@group(0) @binding(3) var<uniform> compose: ClipComposeUniforms;

@vertex
fn vs_path(input: ClipVertexInput) -> ClipVertexOutput {
    var output: ClipVertexOutput;
    output.position = vec4<f32>(input.position, 0.0, 1.0);
    output.atlasUv = input.atlasUv;
    return output;
}

@fragment
fn fs_path(input: ClipVertexOutput) -> @location(0) vec4<f32> {
    let coverage = textureSample(
        nodeOrAtlasTexture,
        clipSampler,
        input.atlasUv).r;
    return vec4<f32>(coverage, 0.0, 0.0, 1.0);
}

@vertex
fn vs_compose(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    // One oversized triangle covers the attachment without a vertex buffer.
    let x = select(-1.0, 3.0, index == 1u);
    let y = select(-1.0, 3.0, index == 2u);
    return vec4<f32>(x, y, 0.0, 1.0);
}

@fragment
fn fs_compose(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let maximum = vec2<i32>(i32(compose.width) - 1, i32(compose.height) - 1);
    let coordinate = clamp(vec2<i32>(position.xy), vec2<i32>(0), maximum);
    let node = textureLoad(nodeOrAtlasTexture, coordinate, 0).r;
    let previous = select(
        textureLoad(previousTexture, coordinate, 0).r,
        1.0,
        compose.first != 0u);
    let coverage = select(
        previous * node,
        previous * (1.0 - node),
        compose.operation != 0u);
    return vec4<f32>(coverage, 0.0, 0.0, 1.0);
}
