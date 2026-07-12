// Algorithm: Transform the ShaderToy quad and expose time, resolution, frame, and mouse inputs to user shader code.
// Time complexity: O(1) per vertex; total fragment cost is defined by the appended user shader.
// Space complexity: O(1) header-local storage.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct ShaderToyUniforms {
    iResolution: vec3<f32>,
    iTime: f32,
    iTimeDelta: f32,
    iFrame: i32,
    iFrameRate: f32,
    _pad0: f32,
    iMouse: vec4<f32>,
    iDate: vec4<f32>,
};

@group(1) @binding(0) var<uniform> inputs: ShaderToyUniforms;

@group(2) @binding(0) var activeMaskSampler: sampler;
@group(2) @binding(1) var activeMaskTexture: texture_2d<f32>;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}
