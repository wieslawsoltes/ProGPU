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
    brushes: array<Brush, 64>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

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
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    let sType = u32(round(input.shapeType));
    var worldPos = input.position;

    if (sType == 3u) {
        // GPU Stroke Expansion
        var miterN = vec2<f32>(0.0, 0.0);
        var miterScale: f32 = 1.0;
        let len1 = length(input.position - input.texCoord);
        let len2 = length(input.shapeSize - input.position);

        if (len1 < 0.001) {
            if (len2 > 0.001) {
                let dir = normalize(input.shapeSize - input.position);
                miterN = vec2<f32>(-dir.y, dir.x);
            }
        } else if (len2 < 0.001) {
            let dir = normalize(input.position - input.texCoord);
            miterN = vec2<f32>(-dir.y, dir.x);
        } else {
            let dir1 = normalize(input.position - input.texCoord);
            let dir2 = normalize(input.shapeSize - input.position);
            let n1 = vec2<f32>(-dir1.y, dir1.x);
            let n2 = vec2<f32>(-dir2.y, dir2.x);
            miterN = normalize(n1 + n2);
            miterScale = clamp(1.0 / max(dot(miterN, n1), 0.0001), 0.5, 4.0);
        }
        let offset = miterN * (input.strokeThickness * 0.5) * miterScale * input.cornerRadius;
        worldPos = input.position + offset;
    } else if (sType == 5u) {
        // GPU Bezier Curve Evaluation
        let t = input.brushIndex;
        let oneMinusT = 1.0 - t;
        let pos = oneMinusT * oneMinusT * input.position + 2.0 * oneMinusT * t * input.texCoord + t * t * input.shapeSize;
        let tangent = 2.0 * oneMinusT * (input.texCoord - input.position) + 2.0 * t * (input.shapeSize - input.texCoord);
        let len = length(tangent);
        var normal = vec2<f32>(0.0, 0.0);
        if (len > 0.0001) {
            normal = vec2<f32>(-tangent.y, tangent.x) / len;
        }
        let offset = normal * (input.strokeThickness * 0.5) * input.cornerRadius;
        worldPos = pos + offset;
    }

    output.position = uniforms.projection * vec4<f32>(worldPos, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = input.shapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = input.shapeType;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let sType = u32(round(input.shapeType));
    var d: f32 = -1.0;

    if (sType == 0u) {
        // Rectangle SDF
        let d_vec = abs(input.texCoord) - input.shapeSize * 0.5;
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0);
    } else if (sType == 1u) {
        // Ellipse SDF
        let rx = input.shapeSize.x * 0.5;
        let ry = input.shapeSize.y * 0.5;
        if (rx > 0.0001 && ry > 0.0001) {
            let v = (input.texCoord.x * input.texCoord.x) / (rx * rx) + (input.texCoord.y * input.texCoord.y) / (ry * ry);
            let grad = vec2<f32>((2.0 * input.texCoord.x) / (rx * rx), (2.0 * input.texCoord.y) / (ry * ry));
            d = (v - 1.0) / max(length(grad), 0.0001);
        }
    } else if (sType == 2u) {
        // Rounded Rectangle SDF
        let r = input.cornerRadius;
        let d_vec = abs(input.texCoord) - (input.shapeSize * 0.5 - vec2<f32>(r));
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0) - r;
    }

    var shapeAlpha: f32 = 1.0;
    if (sType < 3u) {
        var d_shape: f32 = 0.0;
        if (input.strokeThickness > 0.0) {
            d_shape = abs(d) - input.strokeThickness * 0.5;
        } else {
            d_shape = d;
        }
        let fw = max(fwidth(d_shape), 0.0001);
        shapeAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    // Process Color Brush
    let bIdx = u32(round(input.brushIndex));
    let brush = uniforms.brushes[bIdx];

    var finalColor = input.color;
    if (brush.brushType == 0u) {
        finalColor = vec4<f32>(input.color.rgb, input.color.a * brush.opacity);
    } else {
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
        var gradColor = brush.stopColors0;
        if (brush.stopCount > 1u) {
            if (t <= brush.stopOffsets.y) {
                let factor = (t - brush.stopOffsets.x) / max(brush.stopOffsets.y - brush.stopOffsets.x, 0.0001);
                gradColor = mix(brush.stopColors0, brush.stopColors1, clamp(factor, 0.0, 1.0));
            } else if (t <= brush.stopOffsets.z) {
                let factor = (t - brush.stopOffsets.y) / max(brush.stopOffsets.z - brush.stopOffsets.y, 0.0001);
                gradColor = mix(brush.stopColors1, brush.stopColors2, clamp(factor, 0.0, 1.0));
            } else {
                let factor = (t - brush.stopOffsets.z) / max(brush.stopOffsets.w - brush.stopOffsets.z, 0.0001);
                gradColor = mix(brush.stopColors2, brush.stopColors3, clamp(factor, 0.0, 1.0));
            }
        }
        finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
    }

    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha);
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
