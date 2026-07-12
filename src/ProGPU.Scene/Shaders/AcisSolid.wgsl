// Algorithm: Transform ACIS mesh vertices, derive lit surface color, and shade selected/edge-aware solid geometry.
// Time complexity: O(1) per vertex and fragment.
// Space complexity: O(1) local storage with fixed uniform and vertex reads.
struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

struct GpuAcisEdge {
    p0: vec4<f32>,
    p1: vec4<f32>,
};

struct GpuAcisRecord {
    transform: mat4x4<f32>,
    color: vec4<f32>,
    startEdge: u32,
    edgeCount: u32,
    penThickness: f32,
    opacity: f32,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> acisRecords: array<GpuAcisRecord>;
@group(0) @binding(2) var<storage, read> acisEdges: array<GpuAcisEdge>;

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

    let edgeIdx = u32(round(input.position.x));
    let vertexIdx = u32(round(input.position.y));
    let recordIdx = u32(round(input.shapeSize.x));

    let record = acisRecords[recordIdx];
    let edge = acisEdges[edgeIdx];

    let screenP0 = (record.transform * vec4<f32>(edge.p0.xyz, 1.0)).xyz;
    let screenP1 = (record.transform * vec4<f32>(edge.p1.xyz, 1.0)).xyz;

    let p0 = screenP0.xy;
    let p1 = screenP1.xy;
    let tangent = p1 - p0;
    let len = length(tangent);
    var normal = vec2<f32>(0.0, 0.0);
    if (len > 0.0001) {
        normal = vec2<f32>(-tangent.y, tangent.x) / len;
    }
    let halfThickness = record.penThickness * 0.5;
    let expandedDistance = halfThickness + 1.5;
    let signVal = select(-1.0, 1.0, (vertexIdx % 2u) == 0u);
    let pos = select(p1, p0, vertexIdx < 2u);
    let offset = normal * expandedDistance * signVal;

    let z = select(screenP1.z, screenP0.z, vertexIdx < 2u);

    let worldPos10 = pos + offset;
    let texCoord10 = pos;
    let gridIndex10 = signVal * expandedDistance;

    if (useGpuTransforms) {
        output.position = uniforms.projection * uniforms.view * vec4<f32>(worldPos10, z, 1.0);
    } else if (isStatic) {
        output.position = uniforms.projection * uniforms.mvp * vec4<f32>(worldPos10, z, 1.0);
    } else {
        output.position = uniforms.mvp * vec4<f32>(worldPos10, z, 1.0);
    }
    output.color = vec4<f32>(record.color.rgb, record.color.a * record.opacity);
    output.texCoord = texCoord10;
    output.brushIndex = input.brushIndex;
    output.shapeSize = vec2<f32>(record.penThickness, 0.0);
    output.cornerRadius = 0.0;
    output.strokeThickness = record.penThickness;
    output.shapeType = f32(sType);
    output.gridIndex = gridIndex10;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let d_pixels = abs(input.gridIndex);
    let d_shape = d_pixels - input.strokeThickness * 0.5;
    let shapeAlpha = 1.0 - smoothstep(-0.5, 0.5, d_shape);

    if (shapeAlpha <= 0.0) {
        discard;
    }

    return vec4<f32>(input.color.rgb, input.color.a * shapeAlpha);
}
