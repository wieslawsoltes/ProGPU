// Algorithm: Transform effect quads and provide uniform/helper declarations before dynamically generated sampler bindings.
// Time complexity: O(1) per vertex; module assembly is O(S) for S active sampler registers.
// Space complexity: O(1) shader-local storage and O(S) generated declaration text.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct WpfEffectUniforms {
    constants: array<vec4<f32>, 32>,
    bounds: vec4<f32>,
    textureSize: vec4<f32>,
    metadata: vec4<f32>,
};

@group(1) @binding(0) var<uniform> effect: WpfEffectUniforms;

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

fn wpf_constant(index: u32) -> vec4<f32> {
    return effect.constants[index];
}
