// Algorithm: Transform 3D line vertices through model-view-projection matrices and interpolate vertex color.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
    @location(3) brushIndex: f32,
    @location(4) shapeSize: vec2<f32>,
    @location(5) cornerRadius: f32,
    @location(6) strokeThickness: f32,
    @location(7) shapeType: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) brushIndex: f32,
    @location(3) shapeSize: vec2<f32>,
    @location(4) cornerRadius: f32,
    @location(5) strokeThickness: f32,
    @location(6) shapeType: f32,
    @location(7) gridIndex: f32,
};

@vertex
fn vs_main(input: VertexInput, @builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var output: VertexOutput;

    var sType = u32(round(input.shapeType));
    var isStatic = false;
    var useGpuTransforms = false;
    if (input.shapeType >= 195.0) {
        isStatic = true;
        sType = u32(round(input.shapeType - 200.0));
    } else if (input.shapeType >= 95.0) {
        useGpuTransforms = true;
        sType = u32(round(input.shapeType - 100.0));
    }

    let local3D = vec3<f32>(input.position, input.texCoord.x);
    var pos3D = local3D;
    if (useGpuTransforms) {
        pos3D = (uniforms.view * vec4<f32>(local3D, 1.0)).xyz;
    } else if (isStatic) {
        pos3D = (uniforms.mvp * vec4<f32>(local3D, 1.0)).xyz;
    }
    output.position = uniforms.projection * vec4<f32>(pos3D, 1.0);

    output.color = input.color;
    output.texCoord = input.texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = input.shapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = f32(sType);
    output.gridIndex = 0.0;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return input.color;
}
