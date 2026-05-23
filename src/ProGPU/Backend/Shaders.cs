namespace ProGPU.Backend;

public static class Shaders
{
    public const string VectorShader = @"
struct Brush {
    brushType: u32,
    opacity: f32,
    gradientStart: vec2<f32>,
    gradientEnd: vec2<f32>,
    gradientCenter: vec2<f32>,
    gradientRadius: f32,
    stopCount: u32,
    _pad: u32,
    stopColors0: vec4<f32>,
    stopColors1: vec4<f32>,
    stopColors2: vec4<f32>,
    stopColors3: vec4<f32>,
    stopOffsets: vec4<f32>,
};

struct Uniforms {
    projection: mat4x4<f32>,
    brushes: array<Brush, 16>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) texCoord: vec2<f32>,
    @location(3) brushIndex: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) brushIndex: f32,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    output.brushIndex = input.brushIndex;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let bIdx = u32(round(input.brushIndex));
    let brush = uniforms.brushes[bIdx];

    if (brush.brushType == 0u) {
        return vec4<f32>(input.color.rgb, input.color.a * brush.opacity);
    }

    var t: f32 = 0.0;
    if (brush.brushType == 1u) {
        // Linear Gradient
        let gradVec = brush.gradientEnd - brush.gradientStart;
        let lenSq = dot(gradVec, gradVec);
        if (lenSq > 0.0001) {
            t = dot(input.texCoord - brush.gradientStart, gradVec) / lenSq;
        }
    } else if (brush.brushType == 2u) {
        // Radial Gradient
        let dist = distance(input.texCoord, brush.gradientCenter);
        if (brush.gradientRadius > 0.0001) {
            t = dist / brush.gradientRadius;
        }
    }
    t = clamp(t, 0.0, 1.0);

    // Interpolate colors based on stops
    var finalColor = brush.stopColors0;
    if (brush.stopCount > 1u) {
        if (t <= brush.stopOffsets.y) {
            let factor = (t - brush.stopOffsets.x) / max(brush.stopOffsets.y - brush.stopOffsets.x, 0.0001);
            finalColor = mix(brush.stopColors0, brush.stopColors1, clamp(factor, 0.0, 1.0));
        } else if (t <= brush.stopOffsets.z) {
            let factor = (t - brush.stopOffsets.y) / max(brush.stopOffsets.z - brush.stopOffsets.y, 0.0001);
            finalColor = mix(brush.stopColors1, brush.stopColors2, clamp(factor, 0.0, 1.0));
        } else {
            let factor = (t - brush.stopOffsets.z) / max(brush.stopOffsets.w - brush.stopOffsets.z, 0.0001);
            finalColor = mix(brush.stopColors2, brush.stopColors3, clamp(factor, 0.0, 1.0));
        }
    }

    return vec4<f32>(finalColor.rgb, finalColor.a * brush.opacity);
}
";

    public const string TextShader = @"
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

struct Uniforms {
    projection: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let dist = textureSample(atlasTexture, atlasSampler, input.texCoord).r;
    let sig_dist = (dist - 0.5) * 16.0; // 2 * spread (spread is 8.0)
    let dims = vec2<f32>(textureDimensions(atlasTexture));
    let uv_dx = dpdx(input.texCoord) * dims;
    let uv_dy = dpdy(input.texCoord) * dims;
    let screen_width = length(vec2<f32>(length(uv_dx), length(uv_dy)));
    let dist_in_screen = sig_dist / max(screen_width, 0.0001);
    let alpha = smoothstep(-0.5, 0.5, dist_in_screen);
    return vec4<f32>(input.color.rgb, input.color.a * alpha);
}
";

    public const string TextureShader = @"
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

struct Uniforms {
    projection: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(input.position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

@group(1) @binding(0) var texSampler: sampler;
@group(1) @binding(1) var texTexture: texture_2d<f32>;

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let texColor = textureSample(texTexture, texSampler, input.texCoord);
    return texColor * input.color;
}
";
}
