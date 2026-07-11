namespace ProGPU.Backend;

public static class Shaders
{
    public const string SharedWgpuMathCode = @"
fn solve_quadratic(a: f32, b: f32, c: f32, roots: ptr<function, array<f32, 2>>, root_count: ptr<function, u32>) {
    if (abs(a) < 0.00001) {
        if (abs(b) > 0.00001) {
            (*roots)[0] = -c / b;
            *root_count = 1u;
        } else {
            *root_count = 0u;
        }
    } else {
        let d = b * b - 4.0 * a * c;
        if (d < 0.0) {
            *root_count = 0u;
        } else if (d == 0.0) {
            (*roots)[0] = -b / (2.0 * a);
            *root_count = 1u;
        } else {
            let sqrt_d = sqrt(d);
            (*roots)[0] = (-b - sqrt_d) / (2.0 * a);
            (*roots)[1] = (-b + sqrt_d) / (2.0 * a);
            *root_count = 2u;
        }
    }
}

fn cbrt(x: f32) -> f32 {
    if (x < 0.0) {
        return -pow(-x, 1.0 / 3.0);
    }
    return pow(x, 1.0 / 3.0);
}

fn solve_cubic(a_in: f32, b_in: f32, c_in: f32, d_in: f32, roots: ptr<function, array<f32, 3>>, root_count: ptr<function, u32>) {
    if (abs(a_in) < 0.00001) {
        var quad_roots = array<f32, 2>(0.0, 0.0);
        var quad_count = 0u;
        solve_quadratic(b_in, c_in, d_in, &quad_roots, &quad_count);
        *root_count = quad_count;
        for (var i = 0u; i < quad_count; i = i + 1u) {
            (*roots)[i] = quad_roots[i];
        }
        return;
    }

    let a = b_in / a_in;
    let b = c_in / a_in;
    let c = d_in / a_in;

    let p = b - a * a / 3.0;
    let q = c - a * b / 3.0 + 2.0 * a * a * a / 27.0;

    let D = q * q / 4.0 + p * p * p / 27.0;

    if (D > 0.0) {
        let sqrt_D = sqrt(D);
        let u = cbrt(-q / 2.0 + sqrt_D);
        let v = cbrt(-q / 2.0 - sqrt_D);
        (*roots)[0] = u + v - a / 3.0;
        *root_count = 1u;
    } else {
        if (p < 0.0) {
            let r = 2.0 * sqrt(-p / 3.0);
            let val = clamp(-q / (2.0 * sqrt(-p * p * p / 27.0)), -1.0, 1.0);
            let theta = acos(val);
            let pi = 3.14159265359;
            (*roots)[0] = r * cos(theta / 3.0) - a / 3.0;
            (*roots)[1] = r * cos((theta + 2.0 * pi) / 3.0) - a / 3.0;
            (*roots)[2] = r * cos((theta + 4.0 * pi) / 3.0) - a / 3.0;
            *root_count = 3u;
        } else {
            (*roots)[0] = -a / 3.0;
            *root_count = 1u;
        }
    }
}
";

    public const string VectorShader = @"
struct Brush {
    brushType: u32,
    opacity: f32,
    gradientStart: vec2<f32>,
    gradientEnd: vec2<f32>,
    gradientCenter: vec2<f32>,
    gradientRadius: f32,
    stopCount: u32,
    gradientRadiusY: f32,
    spreadMethod: u32,
    colorInterpolationMode: u32,
    stopOffset: u32,
    stopColors0: vec4<f32>,
    stopColors1: vec4<f32>,
    stopColors2: vec4<f32>,
    stopColors3: vec4<f32>,
    stopColors4: vec4<f32>,
    stopColors5: vec4<f32>,
    stopColors6: vec4<f32>,
    stopColors7: vec4<f32>,
    stopOffsets0: vec4<f32>,
    stopOffsets1: vec4<f32>,
    coordinateTransform0: vec4<f32>,
    coordinateTransform1: vec4<f32>,
};

struct GradientStop {
    color: vec4<f32>,
    offset: f32,
};

struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
};


@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read> brushes: array<Brush>;
@group(0) @binding(2) var<storage, read> gradientStops: array<GradientStop>;
@group(1) @binding(0) var pathAtlasSampler: sampler;
@group(1) @binding(1) var pathAtlasTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

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

fn apply_gradient_spread(t: f32, spreadMethod: u32) -> f32 {
    if (spreadMethod == 1u) {
        let period = fract(t * 0.5) * 2.0;
        return select(period, 2.0 - period, period > 1.0);
    }

    if (spreadMethod == 2u) {
        return fract(t);
    }

    return clamp(t, 0.0, 1.0);
}

fn get_gradient_stop_color(brush: Brush, index: u32) -> vec4<f32> {
    return gradientStops[brush.stopOffset + index].color;
}

fn get_gradient_stop_offset(brush: Brush, index: u32) -> f32 {
    return gradientStops[brush.stopOffset + index].offset;
}

fn srgb_to_linear_component(value: f32) -> f32 {
    if (value <= 0.04045) {
        return value / 12.92;
    }

    return pow((value + 0.055) / 1.055, 2.4);
}

fn linear_to_srgb_component(value: f32) -> f32 {
    let clamped = max(value, 0.0);
    if (clamped <= 0.0031308) {
        return clamped * 12.92;
    }

    return (1.055 * pow(clamped, 1.0 / 2.4)) - 0.055;
}

fn srgb_to_linear_color(color: vec4<f32>) -> vec3<f32> {
    return vec3<f32>(
        srgb_to_linear_component(color.r),
        srgb_to_linear_component(color.g),
        srgb_to_linear_component(color.b));
}

fn linear_to_srgb_color(color: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        linear_to_srgb_component(color.r),
        linear_to_srgb_component(color.g),
        linear_to_srgb_component(color.b));
}

fn interpolate_gradient_color(brush: Brush, startColor: vec4<f32>, endColor: vec4<f32>, factor: f32) -> vec4<f32> {
    if (brush.colorInterpolationMode == 1u) {
        let linearColor = mix(srgb_to_linear_color(startColor), srgb_to_linear_color(endColor), factor);
        return vec4<f32>(linear_to_srgb_color(linearColor), mix(startColor.a, endColor.a, factor));
    }

    return mix(startColor, endColor, factor);
}

fn sample_gradient_color(brush: Brush, t: f32) -> vec4<f32> {
    let stopCount = brush.stopCount;
    if (stopCount == 0u) {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    var previousColor = get_gradient_stop_color(brush, 0u);
    var previousOffset = get_gradient_stop_offset(brush, 0u);
    var i = 1u;
    loop {
        if (i >= stopCount) {
            break;
        }

        let currentColor = get_gradient_stop_color(brush, i);
        let currentOffset = get_gradient_stop_offset(brush, i);
        if (t <= currentOffset) {
            let factor = (t - previousOffset) / max(currentOffset - previousOffset, 0.0001);
            return interpolate_gradient_color(brush, previousColor, currentColor, clamp(factor, 0.0, 1.0));
        }

        previousColor = currentColor;
        previousOffset = currentOffset;
        i = i + 1u;
    }

    return previousColor;
}

fn transform_brush_coordinate(brush: Brush, coord: vec2<f32>) -> vec2<f32> {
    let p = vec3<f32>(coord, 1.0);
    return vec2<f32>(
        dot(p, brush.coordinateTransform0.xyz),
        dot(p, brush.coordinateTransform1.xyz));
}

fn perlin_fade(value: vec2<f32>) -> vec2<f32> {
    return value * value * (vec2<f32>(3.0) - 2.0 * value);
}

fn wrap_perlin_cell(cell: vec2<f32>, period: vec2<f32>) -> vec2<f32> {
    var wrapped = cell;
    if (period.x > 0.5) {
        wrapped.x = wrapped.x - floor(wrapped.x / period.x) * period.x;
    }
    if (period.y > 0.5) {
        wrapped.y = wrapped.y - floor(wrapped.y / period.y) * period.y;
    }
    return wrapped;
}

fn fallback_perlin_gradient(cell: vec2<f32>, seed: f32, channel: f32) -> vec2<f32> {
    let value = fract(sin(dot(cell, vec2<f32>(127.1, 311.7)) + seed * 74.7 + channel * 19.19) * 43758.5453);
    let angle = value * 6.283185307179586;
    return vec2<f32>(cos(angle), sin(angle));
}

fn fallback_perlin_noise(
    point: vec2<f32>,
    seed: f32,
    channel: f32,
    period: vec2<f32>) -> f32 {
    let baseCell = floor(point);
    let local = fract(point);
    let fade = perlin_fade(local);
    let cell00 = wrap_perlin_cell(baseCell, period);
    let cell10 = wrap_perlin_cell(baseCell + vec2<f32>(1.0, 0.0), period);
    let cell01 = wrap_perlin_cell(baseCell + vec2<f32>(0.0, 1.0), period);
    let cell11 = wrap_perlin_cell(baseCell + vec2<f32>(1.0, 1.0), period);
    let value00 = dot(fallback_perlin_gradient(cell00, seed, channel), local);
    let value10 = dot(fallback_perlin_gradient(cell10, seed, channel), local - vec2<f32>(1.0, 0.0));
    let value01 = dot(fallback_perlin_gradient(cell01, seed, channel), local - vec2<f32>(0.0, 1.0));
    let value11 = dot(fallback_perlin_gradient(cell11, seed, channel), local - vec2<f32>(1.0, 1.0));
    return mix(mix(value00, value10, fade.x), mix(value01, value11, fade.x), fade.y);
}

fn fallback_perlin_channel(brush: Brush, coordinate: vec2<f32>, channel: f32) -> f32 {
    let octaveCount = min(brush.stopCount, 255u);
    if (octaveCount == 0u) {
        return select(0.5, 0.0, brush.spreadMethod != 0u);
    }

    var frequency = max(abs(brush.gradientStart), vec2<f32>(0.000001));
    var amplitude = 1.0;
    var sum = 0.0;
    var amplitudeSum = 0.0;
    var octave = 0u;
    loop {
        if (octave >= octaveCount) {
            break;
        }
        let period = select(
            vec2<f32>(0.0),
            max(round(abs(brush.gradientCenter) * frequency), vec2<f32>(1.0)),
            all(abs(brush.gradientCenter) > vec2<f32>(0.0)));
        let sampleValue = fallback_perlin_noise(
            coordinate * frequency,
            brush.gradientRadius,
            channel,
            period);
        sum = sum + select(sampleValue, abs(sampleValue), brush.spreadMethod != 0u) * amplitude;
        amplitudeSum = amplitudeSum + amplitude;
        frequency = frequency * 2.0;
        amplitude = amplitude * 0.5;
        octave = octave + 1u;
    }

    let normalized = sum / max(amplitudeSum, 0.000001);
    return clamp(select(normalized * 0.5 + 0.5, normalized, brush.spreadMethod != 0u), 0.0, 1.0);
}

fn perlin_table_selector(brush: Brush, index: i32) -> i32 {
    let wrapped = u32(index & 255);
    return i32(round(gradientStops[brush.stopOffset + wrapped * 2u].offset));
}

fn perlin_table_gradient(brush: Brush, channel: u32, index: i32) -> vec2<f32> {
    let wrapped = u32(index & 255);
    let first = gradientStops[brush.stopOffset + wrapped * 2u].color;
    if (channel == 0u) {
        return first.xy;
    }
    if (channel == 1u) {
        return first.zw;
    }

    let second = gradientStops[brush.stopOffset + wrapped * 2u + 1u].color;
    return select(second.zw, second.xy, channel == 2u);
}

fn perlin_table_noise_channel(
    brush: Brush,
    channel: u32,
    index00: i32,
    index10: i32,
    index01: i32,
    index11: i32,
    fraction: vec2<f32>,
    smoothValue: vec2<f32>) -> f32 {
    let value00 = dot(perlin_table_gradient(brush, channel, index00), fraction);
    let value10 = dot(
        perlin_table_gradient(brush, channel, index10),
        fraction - vec2<f32>(1.0, 0.0));
    let value01 = dot(
        perlin_table_gradient(brush, channel, index01),
        fraction - vec2<f32>(0.0, 1.0));
    let value11 = dot(
        perlin_table_gradient(brush, channel, index11),
        fraction - vec2<f32>(1.0, 1.0));
    return mix(
        mix(value00, value10, smoothValue.x),
        mix(value01, value11, smoothValue.x),
        smoothValue.y);
}

fn perlin_table_noise(
    brush: Brush,
    noiseVector: vec2<f32>,
    stitchData: vec2<f32>) -> vec4<f32> {
    var floorValue = floor(noiseVector);
    var ceilValue = floorValue + vec2<f32>(1.0);
    let fraction = noiseVector - floorValue;
    if (stitchData.x > 0.0) {
        if (floorValue.x >= stitchData.x) { floorValue.x = floorValue.x - stitchData.x; }
        if (ceilValue.x >= stitchData.x) { ceilValue.x = ceilValue.x - stitchData.x; }
    }
    if (stitchData.y > 0.0) {
        if (floorValue.y >= stitchData.y) { floorValue.y = floorValue.y - stitchData.y; }
        if (ceilValue.y >= stitchData.y) { ceilValue.y = ceilValue.y - stitchData.y; }
    }

    let latticeX0 = perlin_table_selector(brush, i32(round(floorValue.x)));
    let latticeX1 = perlin_table_selector(brush, i32(round(ceilValue.x)));
    let index00 = latticeX0 + i32(round(floorValue.y));
    let index10 = latticeX1 + i32(round(floorValue.y));
    let index01 = latticeX0 + i32(round(ceilValue.y));
    let index11 = latticeX1 + i32(round(ceilValue.y));
    let smoothValue = perlin_fade(fraction);
    return vec4<f32>(
        perlin_table_noise_channel(
            brush, 0u, index00, index10, index01, index11, fraction, smoothValue),
        perlin_table_noise_channel(
            brush, 1u, index00, index10, index01, index11, fraction, smoothValue),
        perlin_table_noise_channel(
            brush, 2u, index00, index10, index01, index11, fraction, smoothValue),
        perlin_table_noise_channel(
            brush, 3u, index00, index10, index01, index11, fraction, smoothValue));
}

fn exact_perlin_noise(brush: Brush, coordinate: vec2<f32>) -> vec4<f32> {
    let octaveCount = min(brush.stopCount, 255u);
    if (octaveCount == 0u) {
        return select(vec4<f32>(0.5), vec4<f32>(0.0), brush.spreadMethod != 0u);
    }

    var noiseVector = (coordinate + vec2<f32>(0.5)) * brush.gradientStart;
    var stitchData = brush.gradientEnd;
    var ratio = 1.0;
    var result = vec4<f32>(0.0);
    for (var octave = 0u; octave < octaveCount; octave = octave + 1u) {
        var sampleValue = perlin_table_noise(brush, noiseVector, stitchData);
        if (brush.spreadMethod != 0u) {
            sampleValue = abs(sampleValue);
        }
        result = result + sampleValue * ratio;
        noiseVector = noiseVector * 2.0;
        stitchData = stitchData * 2.0;
        ratio = ratio * 0.5;
    }

    if (brush.spreadMethod == 0u) {
        result = result * 0.5 + vec4<f32>(0.5);
    }
    return clamp(result, vec4<f32>(0.0), vec4<f32>(1.0));
}

fn sample_perlin_noise(brush: Brush, coordinate: vec2<f32>) -> vec4<f32> {
    if (brush.colorInterpolationMode != 0u) {
        return exact_perlin_noise(brush, coordinate);
    }
    return vec4<f32>(
        fallback_perlin_channel(brush, coordinate, 0.0),
        fallback_perlin_channel(brush, coordinate, 1.0),
        fallback_perlin_channel(brush, coordinate, 2.0),
        fallback_perlin_channel(brush, coordinate, 3.0));
}

fn solve_two_point_conical_gradient(brush: Brush, coord: vec2<f32>) -> vec2<f32> {
    let centerDelta = brush.gradientCenter - brush.gradientStart;
    let radiusDelta = brush.gradientRadiusY - brush.gradientRadius;
    let point = coord - brush.gradientStart;
    let a = dot(centerDelta, centerDelta) - radiusDelta * radiusDelta;
    let b = -2.0 * (dot(point, centerDelta) + brush.gradientRadius * radiusDelta);
    let c = dot(point, point) - brush.gradientRadius * brush.gradientRadius;

    if (abs(a) < 0.00001) {
        if (abs(b) > 0.00001) {
            let root = -c / b;
            let radius = brush.gradientRadius + root * radiusDelta;
            if (radius >= -0.00001) {
                return vec2<f32>(root, 1.0);
            }
        }

        return vec2<f32>(0.0, 0.0);
    }

    let discriminant = (b * b) - (4.0 * a * c);
    if (discriminant < 0.0) {
        return vec2<f32>(0.0, 0.0);
    }

    let sqrtDiscriminant = sqrt(discriminant);
    let denominator = 2.0 * a;
    let root0 = (-b - sqrtDiscriminant) / denominator;
    let root1 = (-b + sqrtDiscriminant) / denominator;
    let root0Radius = brush.gradientRadius + root0 * radiusDelta;
    let root1Radius = brush.gradientRadius + root1 * radiusDelta;
    let root0Valid = root0Radius >= -0.00001;
    let root1Valid = root1Radius >= -0.00001;

    if (root0Valid && root1Valid) {
        return vec2<f32>(max(root0, root1), 1.0);
    }

    if (root0Valid) {
        return vec2<f32>(root0, 1.0);
    }

    if (root1Valid) {
        return vec2<f32>(root1, 1.0);
    }

    return vec2<f32>(0.0, 0.0);
}

const PROGPU_TWO_PI: f32 = 6.28318530718;

fn normalize_positive_radians(angle: f32) -> f32 {
    return angle - floor(angle / PROGPU_TWO_PI) * PROGPU_TWO_PI;
}

fn arc_eval(center: vec2<f32>, axisX: vec2<f32>, axisY: vec2<f32>, theta: f32) -> vec2<f32> {
    return center + axisX * cos(theta) + axisY * sin(theta);
}

fn arc_derivative(axisX: vec2<f32>, axisY: vec2<f32>, theta: f32, deltaTheta: f32) -> vec2<f32> {
    let sweepSign = select(-1.0, 1.0, deltaTheta >= 0.0);
    return (-axisX * sin(theta) + axisY * cos(theta)) * sweepSign;
}

fn safe_normalize(value: vec2<f32>) -> vec2<f32> {
    let len = length(value);
    if (len > 0.0001) {
        return value / len;
    }

    return vec2<f32>(0.0, 0.0);
}

fn nearest_ellipse_theta(point: vec2<f32>, center: vec2<f32>, axisX: vec2<f32>, axisY: vec2<f32>) -> f32 {
    let p = point - center;
    let det = axisX.x * axisY.y - axisX.y * axisY.x;
    var theta = 0.0;
    if (abs(det) > 0.00001) {
        let local = vec2<f32>(
            (p.x * axisY.y - p.y * axisY.x) / det,
            (axisX.x * p.y - axisX.y * p.x) / det);
        theta = atan2(local.y, local.x);
    }

    for (var i = 0u; i < 5u; i = i + 1u) {
        let c = cos(theta);
        let s = sin(theta);
        let q = center + axisX * c + axisY * s;
        let dq = -axisX * s + axisY * c;
        let ddq = -axisX * c - axisY * s;
        let r = q - point;
        let numerator = dot(r, dq);
        let denominator = dot(dq, dq) + dot(r, ddq);
        if (abs(denominator) > 0.00001) {
            theta = theta - numerator / denominator;
        }
    }

    return theta;
}

fn is_angle_inside_arc(theta: f32, theta1: f32, deltaTheta: f32) -> bool {
    let span = abs(deltaTheta);
    if (span >= PROGPU_TWO_PI - 0.001) {
        return true;
    }

    let sweepSign = select(-1.0, 1.0, deltaTheta >= 0.0);
    let along = normalize_positive_radians((theta - theta1) * sweepSign);
    return along <= span + 0.001;
}

@vertex
fn vs_main(input: VertexInput, @builtin(vertex_index) vertexIndex: u32) -> VertexOutput {
    var output: VertexOutput;
    
    var encodedShapeType = input.shapeType;
    let aliasedEdge = encodedShapeType >= 1000.0;
    if (aliasedEdge) {
        encodedShapeType = encodedShapeType - 1000.0;
    }

    var sType = u32(round(encodedShapeType));
    var isStatic = false;
    var useGpuTransforms = false;
    if (encodedShapeType >= 195.0) {
        isStatic = true;
        sType = u32(round(encodedShapeType - 200.0));
    } else if (encodedShapeType >= 95.0) {
        useGpuTransforms = true;
        sType = u32(round(encodedShapeType - 100.0));
    }
    


    var inPos = input.position;
    var inTexCoord = input.texCoord;
    var inShapeSize = input.shapeSize;
    var inColor = input.color;

    if ((isStatic || useGpuTransforms) && sType != 8u) {
        if (useGpuTransforms) {
            inPos = (uniforms.view * vec4<f32>(input.position, 0.0, 1.0)).xy;
            if (sType == 12u) {
                inTexCoord = (uniforms.view * vec4<f32>(input.texCoord, 0.0, 0.0)).xy;
                inShapeSize = (uniforms.view * vec4<f32>(input.shapeSize, 0.0, 0.0)).xy;
                inColor = vec4<f32>((uniforms.view * vec4<f32>(input.color.xy, 0.0, 1.0)).xy, input.color.z, input.color.w);
            } else if (sType == 3u || sType == 5u || sType == 6u) {
                inTexCoord = (uniforms.view * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
                inShapeSize = (uniforms.view * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
                if (sType == 6u) {
                    inColor = vec4<f32>((uniforms.view * vec4<f32>(input.color.rg, 0.0, 1.0)).xy, input.color.b, input.color.a);
                }
            } else if (sType < 3u) {
                let bIdx = u32(round(input.brushIndex));
                let brush = brushes[bIdx];
                if (brush.brushType > 0u) {
                    inColor = vec4<f32>((uniforms.view * vec4<f32>(input.color.xy, 0.0, 1.0)).xy, input.color.z, input.color.w);
                }
            }
        } else {
            inPos = (uniforms.mvp * vec4<f32>(input.position, 0.0, 1.0)).xy;
            if (sType == 12u) {
                inTexCoord = (uniforms.mvp * vec4<f32>(input.texCoord, 0.0, 0.0)).xy;
                inShapeSize = (uniforms.mvp * vec4<f32>(input.shapeSize, 0.0, 0.0)).xy;
                inColor = vec4<f32>((uniforms.mvp * vec4<f32>(input.color.xy, 0.0, 1.0)).xy, input.color.z, input.color.w);
            } else if (sType == 3u || sType == 5u || sType == 6u) {
                inTexCoord = (uniforms.mvp * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
                inShapeSize = (uniforms.mvp * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
                if (sType == 6u) {
                    inColor = vec4<f32>((uniforms.mvp * vec4<f32>(input.color.rg, 0.0, 1.0)).xy, input.color.b, input.color.a);
                }
            } else if (sType < 3u) {
                let bIdx = u32(round(input.brushIndex));
                let brush = brushes[bIdx];
                if (brush.brushType > 0u) {
                    inColor = vec4<f32>((uniforms.mvp * vec4<f32>(input.color.xy, 0.0, 1.0)).xy, input.color.z, input.color.w);
                }
            }
        }
    }

    var worldPos = inPos;
    var texCoord = inTexCoord;
    var gridIndex = 0.0;
    var outputCornerRadius = input.cornerRadius;

    if (sType == 3u) {
        // GPU Stroke Expansion
        var miterN = vec2<f32>(0.0, 0.0);
        var miterScale: f32 = 1.0;
        
        let p0 = inTexCoord;
        let p1 = inShapeSize;
        
        let isStart = abs(input.cornerRadius) < 1.5;
        worldPos = inPos;

        let len1 = length(worldPos - p0);
        let len2 = length(p1 - worldPos);

        if (len1 < 0.001) {
            if (len2 > 0.001) {
                let dir = normalize(p1 - p0);
                miterN = vec2<f32>(-dir.y, dir.x);
            }
        } else if (len2 < 0.001) {
            let dir = normalize(worldPos - p0);
            miterN = vec2<f32>(-dir.y, dir.x);
        } else {
            let dir1 = normalize(worldPos - p0);
            let dir2 = normalize(p1 - worldPos);
            let n1 = vec2<f32>(-dir1.y, dir1.x);
            let n2 = vec2<f32>(-dir2.y, dir2.x);
            miterN = normalize(n1 + n2);
            miterScale = clamp(1.0 / max(dot(miterN, n1), 0.0001), 0.5, 4.0);
        }
        let halfThickness = input.strokeThickness * 0.5;
        let expandedDistance = halfThickness * miterScale + 1.5;
        let signVal = select(-1.0, 1.0, input.cornerRadius > 0.0);
        let offset = miterN * expandedDistance * signVal;
        worldPos = worldPos + offset;
        texCoord = worldPos;
        gridIndex = signVal * expandedDistance;
    } else if (sType == 5u) {
        // GPU Quadratic Bezier Curve Evaluation
        let p0 = inPos;
        let p1 = inTexCoord;
        let p2 = inShapeSize;
        
        let idxStart = u32(round(input.cornerRadius));
        let localIndex = vertexIndex - idxStart;
        let N = 24u;
        let t = f32(localIndex / 2u) / f32(N);
        let signVal = select(-1.0, 1.0, (localIndex % 2u) == 0u);
        
        let oneMinusT = 1.0 - t;
        let pos = oneMinusT * oneMinusT * p0 + 2.0 * oneMinusT * t * p1 + t * t * p2;
        var tangent = 2.0 * oneMinusT * (p1 - p0) + 2.0 * t * (p2 - p1);
        if (length(tangent) <= 0.0001) {
            tangent = p2 - p0;
        }
        let len = length(tangent);
        var normal = vec2<f32>(0.0, 0.0);
        if (len > 0.0001) {
            normal = vec2<f32>(-tangent.y, tangent.x) / len;
        }
        let halfThickness = input.strokeThickness * 0.5;
        let expandedDistance = halfThickness + 1.5;
        let offset = normal * expandedDistance * signVal;
        worldPos = pos + offset;
        texCoord = pos;
        gridIndex = signVal * expandedDistance;
    } else if (sType == 6u) {
        // GPU Cubic Bezier Curve Evaluation
        let p0 = inPos;
        let p1 = inTexCoord;
        let p2 = inShapeSize;
        let p3 = inColor.rg;
        
        let idxStart = u32(round(input.cornerRadius));
        let localIndex = vertexIndex - idxStart;
        let N = 24u;
        let t = f32(localIndex / 2u) / f32(N);
        let signVal = select(-1.0, 1.0, (localIndex % 2u) == 0u);
        
        let oneMinusT = 1.0 - t;
        
        let pos = oneMinusT * oneMinusT * oneMinusT * p0 
                + 3.0 * oneMinusT * oneMinusT * t * p1 
                + 3.0 * oneMinusT * t * t * p2 
                + t * t * t * p3;
                
        var tangent = 3.0 * oneMinusT * oneMinusT * (p1 - p0)
                    + 6.0 * oneMinusT * t * (p2 - p1) 
                    + 3.0 * t * t * (p3 - p2);
        if (length(tangent) <= 0.0001) {
            tangent = select(p3 - p1, p2 - p0, t <= 0.5);
            if (length(tangent) <= 0.0001) {
                tangent = p3 - p0;
            }
        }
                    
        let len = length(tangent);
        var normal = vec2<f32>(0.0, 0.0);
        if (len > 0.0001) {
            normal = vec2<f32>(-tangent.y, tangent.x) / len;
        }
        let halfThickness = input.strokeThickness * 0.5;
        let expandedDistance = halfThickness + 1.5;
        let offset = normal * expandedDistance * signVal;
        worldPos = pos + offset;
        texCoord = pos;
        gridIndex = signVal * expandedDistance;
    } else if (sType == 12u) {
        // Fixed-quad native arc stroke. The fragment shader evaluates the
        // transformed ellipse and WPF/SVG sweep directly, so the CPU does not
        // tessellate valid arcs into a line strip.
        worldPos = inPos;
        texCoord = worldPos;
        outputCornerRadius = inTexCoord.x;
        gridIndex = inTexCoord.y;
    }

    if (sType == 8u) {
        let local3D = vec3<f32>(inPos, inTexCoord.x);
        var pos3D = local3D;
        if (useGpuTransforms) {
            pos3D = (uniforms.view * vec4<f32>(local3D, 1.0)).xyz;
        } else if (isStatic) {
            pos3D = (uniforms.mvp * vec4<f32>(local3D, 1.0)).xyz;
        }
        output.position = uniforms.projection * vec4<f32>(pos3D, 1.0);
    } else {
        var pos = worldPos;
        if (useGpuTransforms) {
            // Since we pre-transformed inPos/inTexCoord/inShapeSize by uniforms.view,
            // worldPos is already in screen-space. Do not transform it again!
            pos = worldPos;
        }
        output.position = uniforms.projection * vec4<f32>(pos, 0.0, 1.0);
    }
    output.color = inColor;
    output.texCoord = texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = inShapeSize;
    output.cornerRadius = outputCornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = select(f32(sType), f32(sType) + 1000.0, aliasedEdge);
    output.gridIndex = gridIndex;
    return output;
}



fn vector_fs_main(input: VertexOutput) -> vec4<f32> {
    var encodedShapeType = input.shapeType;
    let aliasedEdge = encodedShapeType >= 1000.0;
    if (aliasedEdge) {
        encodedShapeType = encodedShapeType - 1000.0;
    }

    let sType = u32(round(encodedShapeType));
    var d: f32 = -1.0;

    var evalCoord = input.texCoord;
    if (sType < 3u) {
        evalCoord = input.color.xy + input.texCoord;
    } else if (sType == 4u) {
        evalCoord = input.shapeSize;
    }

    if (sType == 0u) {
        // Rectangle SDF
        let d_vec = abs(input.texCoord) - input.shapeSize * 0.5;
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0);
    } else if (sType == 1u) {
        // Ellipse SDF
        let rx = input.shapeSize.x * 0.5;
        let ry = input.shapeSize.y * 0.5;
        if (rx > 0.0001 && ry > 0.0001) {
            if (abs(rx - ry) <= 0.0001) {
                d = length(input.texCoord) - rx;
            } else {
                let v = (input.texCoord.x * input.texCoord.x) / (rx * rx) +
                    (input.texCoord.y * input.texCoord.y) / (ry * ry);
                let grad = vec2<f32>(
                    (2.0 * input.texCoord.x) / (rx * rx),
                    (2.0 * input.texCoord.y) / (ry * ry));
                d = (v - 1.0) / max(length(grad), 0.0001);
            }
        }
    } else if (sType == 2u) {
        // Rounded Rectangle SDF
        let r = input.cornerRadius;
        let d_vec = abs(input.texCoord) - (input.shapeSize * 0.5 - vec2<f32>(r));
        d = length(max(d_vec, vec2<f32>(0.0))) + min(max(d_vec.x, d_vec.y), 0.0) - r;
    }

    var shapeAlpha: f32 = 1.0;
    if (sType == 0u && input.strokeThickness <= 0.0) {
        let edgeDistance = abs(input.texCoord) - input.shapeSize * 0.5;
        let edgeWidth = max(fwidth(edgeDistance), vec2<f32>(0.0001));
        let antialiasedCoverage = vec2<f32>(1.0) - smoothstep(
            -0.5 * edgeWidth,
            0.5 * edgeWidth,
            edgeDistance);
        let aliasedCoverage = select(
            vec2<f32>(0.0),
            vec2<f32>(1.0),
            edgeDistance <= vec2<f32>(0.0));
        let coverage = select(antialiasedCoverage, aliasedCoverage, aliasedEdge);
        shapeAlpha = coverage.x * coverage.y;
    } else if (sType < 3u) {
        var d_shape: f32 = 0.0;
        if (input.strokeThickness > 0.0) {
            var strokeDistance = d;
            if (aliasedEdge && sType == 1u) {
                strokeDistance = strokeDistance + 0.08;
            }
            let thinStrokeInset = select(
                0.0,
                0.0625,
                !aliasedEdge && input.strokeThickness <= 1.0001);
            d_shape = abs(strokeDistance) -
                max(input.strokeThickness * 0.5 - thinStrokeInset, 0.0);
        } else {
            d_shape = d;
        }
        let fw = max(fwidth(d_shape), 0.0001);
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 12u) {
        // Fixed-quad elliptical arc stroke SDF. color.xy carries the center,
        // color.zw carries theta1/deltaTheta, cornerRadius/gridIndex carries
        // axisX, shapeSize carries axisY, and texCoord is the pixel position.
        let center = input.color.xy;
        let theta1 = input.color.z;
        let deltaTheta = input.color.w;
        let axisX = vec2<f32>(input.cornerRadius, input.gridIndex);
        let axisY = input.shapeSize;
        let point = input.texCoord;
        let theta = nearest_ellipse_theta(point, center, axisX, axisY);
        let nearest = arc_eval(center, axisX, axisY, theta);
        let thinStrokeInset = select(
            0.0,
            0.0625,
            !aliasedEdge && input.strokeThickness <= 1.0001);
        let effectiveHalfWidth = max(
            input.strokeThickness * 0.5 - thinStrokeInset,
            0.0);
        var d_shape = length(point - nearest) - effectiveHalfWidth;
        let radiusX = length(axisX);
        let radiusY = length(axisY);
        if (abs(radiusX - radiusY) <= 0.0001) {
            let signedDistance = length(point - center) - radiusX;
            if (aliasedEdge) {
                d_shape = abs(signedDistance + 0.08) -
                    input.strokeThickness * 0.5;
            } else {
                d_shape = abs(signedDistance) - effectiveHalfWidth;
            }
        }

        if (abs(deltaTheta) < PROGPU_TWO_PI - 0.001) {
            let startPoint = arc_eval(center, axisX, axisY, theta1);
            let endPoint = arc_eval(center, axisX, axisY, theta1 + deltaTheta);
            let startTangent = safe_normalize(arc_derivative(axisX, axisY, theta1, deltaTheta));
            let endTangent = safe_normalize(arc_derivative(axisX, axisY, theta1 + deltaTheta, deltaTheta));
            let startCap = -dot(point - startPoint, startTangent);
            let endCap = dot(point - endPoint, endTangent);
            let capDistance = max(startCap, endCap);
            let insideSweep = is_angle_inside_arc(theta, theta1, deltaTheta);
            d_shape = select(max(d_shape, capDistance), d_shape, insideSweep);
        }

        let fw = max(fwidth(d_shape), 0.0001);
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 13u) {
        // Antialiased stroke join/cap triangle. color.xy/color.zw/shapeSize
        // carry its three screen-space points and texCoord carries the pixel.
        let p0 = input.color.xy;
        let p1 = input.color.zw;
        let p2 = input.shapeSize;
        let point = input.texCoord;
        let edge0 = p1 - p0;
        let edge1 = p2 - p1;
        let edge2 = p0 - p2;
        let area = edge0.x * (p2.y - p0.y) - edge0.y * (p2.x - p0.x);
        let orientation = select(-1.0, 1.0, area >= 0.0);
        let distance0 = -orientation *
            (edge0.x * (point.y - p0.y) - edge0.y * (point.x - p0.x)) /
            max(length(edge0), 0.0001);
        let distance1 = -orientation *
            (edge1.x * (point.y - p1.y) - edge1.y * (point.x - p1.x)) /
            max(length(edge1), 0.0001);
        let distance2 = -orientation *
            (edge2.x * (point.y - p2.y) - edge2.y * (point.x - p2.x)) /
            max(length(edge2), 0.0001);
        let d_shape = max(distance0, max(distance1, distance2));
        let fw = max(fwidth(d_shape), 0.0001);
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 14u) {
        // Antialiased affine stroke segment. color.xy/color.zw/shapeSize and
        // cornerRadius/strokeThickness carry its four screen-space corners.
        let p0 = input.color.xy;
        let p1 = input.color.zw;
        let p2 = input.shapeSize;
        let p3 = vec2<f32>(input.cornerRadius, input.strokeThickness);
        let point = input.texCoord;
        let edge0 = p1 - p0;
        let edge1 = p2 - p1;
        let edge2 = p3 - p2;
        let edge3 = p0 - p3;
        let area = edge0.x * (p2.y - p0.y) - edge0.y * (p2.x - p0.x);
        let orientation = select(-1.0, 1.0, area >= 0.0);
        let distance0 = -orientation *
            (edge0.x * (point.y - p0.y) - edge0.y * (point.x - p0.x)) /
            max(length(edge0), 0.0001);
        let distance1 = -orientation *
            (edge1.x * (point.y - p1.y) - edge1.y * (point.x - p1.x)) /
            max(length(edge1), 0.0001);
        let distance2 = -orientation *
            (edge2.x * (point.y - p2.y) - edge2.y * (point.x - p2.x)) /
            max(length(edge2), 0.0001);
        let distance3 = -orientation *
            (edge3.x * (point.y - p3.y) - edge3.y * (point.x - p3.x)) /
            max(length(edge3), 0.0001);
        let d_shape = max(max(distance0, distance1), max(distance2, distance3));
        let fw = max(fwidth(d_shape), 0.0001);
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 3u || sType == 5u || sType == 6u) {
        // Line, Quadratic, and Cubic Bezier stroke anti-aliasing via signed pixel distance
        let d_pixels = abs(input.gridIndex);
        let thinStrokeInset = select(
            0.0,
            0.0625,
            !aliasedEdge && input.strokeThickness <= 1.0001);
        let d_shape = d_pixels -
            max(input.strokeThickness * 0.5 - thinStrokeInset, 0.0);
        let antialiasHalfWidth = 0.5;
        let antialiasedAlpha = 1.0 - smoothstep(
            -antialiasHalfWidth,
            antialiasHalfWidth,
            d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 4u) {
        // Path rendering: sample coverage directly from PathAtlas
        let coverage = textureSample(pathAtlasTexture, pathAtlasSampler, input.texCoord).r;
        shapeAlpha = select(coverage, select(0.0, 1.0, coverage >= 0.5), aliasedEdge);
    } else if (sType == 7u) {
        // Direct solid fill
        shapeAlpha = 1.0;
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    // Process Color Brush
    let bIdx = u32(round(input.brushIndex));
    let brush = brushes[bIdx];

    var finalColor = input.color;
    if (brush.brushType == 0u) {
        if (sType == 5u || sType == 6u || sType == 12u || sType == 13u || sType == 14u) {
            finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
        } else {
            finalColor = vec4<f32>(input.color.rgb, input.color.a * brush.opacity);
        }

    } else {
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        var t: f32 = 0.0;
        var gradientCoverage: f32 = 1.0;
        if (brush.brushType == 1u) {
            // Linear Gradient
            let gradVec = brush.gradientEnd - brush.gradientStart;
            let lenSq = dot(gradVec, gradVec);
            if (lenSq > 0.0001) {
                t = dot(brushCoord - brush.gradientStart, gradVec) / lenSq;
            }
        } else if (brush.brushType == 2u) {
            // Radial Gradient
            let rx = brush.gradientRadius;
            let ry = brush.gradientRadiusY;
            if (rx > 0.0001 || ry > 0.0001) {
                let radii = vec2<f32>(max(rx, 0.0001), max(ry, 0.0001));
                let point = (brushCoord - brush.gradientCenter) / radii;
                let origin = (brush.gradientStart - brush.gradientCenter) / radii;
                let direction = point - origin;
                let a = dot(direction, direction);
                if (a > 0.0001) {
                    let b = 2.0 * dot(origin, direction);
                    let c = dot(origin, origin) - 1.0;
                    let discriminant = max((b * b) - (4.0 * a * c), 0.0);
                    let boundary = (-b + sqrt(discriminant)) / (2.0 * a);
                    if (boundary > 0.0001) {
                        t = 1.0 / boundary;
                    }
                }
            }
        } else if (brush.brushType == 5u) {
            // Two-point conical gradient: interpolate between two moving circle boundaries.
            let solution = solve_two_point_conical_gradient(brush, brushCoord);
            t = solution.x;
            gradientCoverage = solution.y;
        } else if (brush.brushType == 6u) {
            // Sweep gradient, starting at the positive X axis and increasing clockwise.
            let direction = brushCoord - brush.gradientCenter;
            t = atan2(direction.y, direction.x) / (2.0 * 3.141592653589793);
            if (t < 0.0) {
                t = t + 1.0;
            }
        } else if (brush.brushType == 7u) {
            let noiseColor = sample_perlin_noise(brush, brushCoord);
            finalColor = vec4<f32>(noiseColor.rgb, noiseColor.a * brush.opacity);
        }
        if (brush.brushType == 7u) {
            // Procedural noise was evaluated directly above.
        } else if (gradientCoverage <= 0.0) {
            if ((brush.spreadMethod & 0x80000000u) != 0u) {
                finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
            } else {
                finalColor = vec4<f32>(0.0);
        }
        } else {
            t = apply_gradient_spread(t, brush.spreadMethod & 0x7fffffffu);
        let gradColor = sample_gradient_color(brush, t);
        finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
    }
    }

    let screen_uv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
    if (maskAlpha <= 0.0) {
        discard;
    }
    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha * maskAlpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return vector_fs_main(input);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = vector_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = vector_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
";

    public const string TextShader = @"
struct VertexInput {
    @builtin(vertex_index) vertexIndex: u32,
    @location(0) snappedLogicalPos: vec2<f32>,
    @location(1) basisX: vec2<f32>,
    @location(2) basisY: vec2<f32>,
    @location(3) bearSize: vec4<f32>,
    @location(4) texCoords: vec4<f32>,
    @location(5) color: vec4<f32>,
    @location(6) scaleBoldItalicUseMvp: vec4<f32>,
    @location(7) brushIndex: f32,
};

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec4<f32>,
    @location(1) texCoord: vec2<f32>,
    @location(2) cornerRadius: f32,
    @location(3) strokeThickness: f32,
    @location(4) textMode: f32,
};

struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    pad0: f32,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;

    var local_uv = vec2<f32>(0.0, 0.0);
    var corner = 0u;
    if (input.vertexIndex == 1u) {
        local_uv = vec2<f32>(1.0, 0.0);
        corner = 1u;
    } else if (input.vertexIndex == 2u) {
        local_uv = vec2<f32>(1.0, 1.0);
        corner = 2u;
    } else if (input.vertexIndex == 4u) {
        local_uv = vec2<f32>(1.0, 1.0);
        corner = 2u;
    } else if (input.vertexIndex == 5u) {
        local_uv = vec2<f32>(0.0, 1.0);
        corner = 3u;
    }

    let bear = input.bearSize.xy / uniforms.dpiScale;
    let size = input.bearSize.zw / uniforms.dpiScale;
    let texCoordMin = input.texCoords.xy;
    let texCoordMax = input.texCoords.zw;

    let scaleRatio = input.scaleBoldItalicUseMvp.x;
    let boldOffset = input.scaleBoldItalicUseMvp.y;
    let italicSkew = input.scaleBoldItalicUseMvp.z;
    let encodedTextFlags = input.scaleBoldItalicUseMvp.w;
    let colorGlyph = encodedTextFlags > 5.5;
    let textFlags = select(encodedTextFlags, encodedTextFlags - 8.0, colorGlyph);
    let aliasedText = textFlags < -0.5;
    let clearTypeText = textFlags > 1.5;
    let useMvp = select(
        select(textFlags, textFlags - 2.0, clearTypeText),
        -textFlags - 1.0,
        aliasedText);

    let lx0 = bear.x * scaleRatio + boldOffset;
    let ly0 = bear.y * scaleRatio;
    let lx1 = lx0 + size.x * scaleRatio;
    let ly1 = ly0 + size.y * scaleRatio;

    let lsx0 = lx0 - ly0 * italicSkew;
    let lsx1 = lx1 - ly0 * italicSkew;
    let lsx2 = lx1 - ly1 * italicSkew;
    let lsx3 = lx0 - ly1 * italicSkew;

    var localOffset = vec2<f32>(0.0, 0.0);
    if (corner == 0u) {
        localOffset = vec2<f32>(lsx0, ly0);
    } else if (corner == 1u) {
        localOffset = vec2<f32>(lsx1, ly0);
    } else if (corner == 2u) {
        localOffset = vec2<f32>(lsx2, ly1);
    } else {
        localOffset = vec2<f32>(lsx3, ly1);
    }

    let physicalOffset = localOffset.x * input.basisX + localOffset.y * input.basisY;
    var finalPosLogical = input.snappedLogicalPos + physicalOffset;

    if (useMvp > 0.5) {
        finalPosLogical = (uniforms.mvp * vec4<f32>(finalPosLogical, 0.0, 1.0)).xy;
    }

    output.position = uniforms.projection * vec4<f32>(finalPosLogical, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = mix(texCoordMin, texCoordMax, local_uv);
    output.cornerRadius = select(1.43, -1.43, aliasedText); // DefaultTextGamma, sign encodes aliased text
    output.strokeThickness = 1.15; // DefaultTextContrast
    output.textMode = select(
        select(select(0.0, 2.0, clearTypeText), 1.0, aliasedText),
        3.0,
        colorGlyph);
    return output;
}

@group(1) @binding(0) var atlasSampler: sampler;
@group(1) @binding(1) var atlasTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

fn text_coverage_to_alpha(alpha: f32, contrast: f32, gamma: f32, aliasedText: bool) -> f32 {
    let dilated = clamp(alpha * contrast, 0.0, 1.0);
    return select(pow(dilated, gamma), select(0.0, 1.0, alpha >= 0.5), aliasedText);
}

fn text_fs_main(input: VertexOutput) -> vec4<f32> {
    let atlasColor = textureSample(atlasTexture, atlasSampler, input.texCoord);
    let alpha = atlasColor.r;
    let aliasedText = input.cornerRadius < 0.0;
    let screen_uv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
    if (maskAlpha <= 0.0) {
        discard;
    }
    if (input.textMode > 2.5) {
        return vec4<f32>(atlasColor.rgb, atlasColor.a * input.color.a * maskAlpha);
    }
    let gamma = abs(input.cornerRadius);
    let grayscaleAlpha = text_coverage_to_alpha(alpha, input.strokeThickness, gamma, aliasedText);

    if (input.textMode > 1.5) {
        let atlasDims = textureDimensions(atlasTexture);
        let atlasSize = vec2<f32>(f32(atlasDims.x), f32(atlasDims.y));
        let subpixelOffset = vec2<f32>(1.0 / max(atlasSize.x * 3.0, 1.0), 0.0);
        let redCoverage = textureSample(atlasTexture, atlasSampler, input.texCoord - subpixelOffset).r;
        let greenCoverage = alpha;
        let blueCoverage = textureSample(atlasTexture, atlasSampler, input.texCoord + subpixelOffset).r;
        let rgbCoverage = vec3<f32>(
            text_coverage_to_alpha(redCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(greenCoverage, input.strokeThickness, gamma, false),
            text_coverage_to_alpha(blueCoverage, input.strokeThickness, gamma, false)) * input.color.a * maskAlpha;
        let finalAlpha = max(max(rgbCoverage.r, rgbCoverage.g), rgbCoverage.b);
        if (finalAlpha <= 0.0001) {
            return vec4<f32>(0.0);
        }

        return vec4<f32>(input.color.rgb * (rgbCoverage / finalAlpha), finalAlpha);
    }

    return vec4<f32>(input.color.rgb, input.color.a * grayscaleAlpha * maskAlpha);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return text_fs_main(input);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = text_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
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
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    var pos = input.position;
    output.position = uniforms.projection * vec4<f32>(pos, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    return output;
}

    @group(1) @binding(0) var texSampler: sampler;
@group(1) @binding(1) var texTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

fn cubic_weight(x: f32) -> f32 {
    let a = -0.5;
    let ax = abs(x);
    let ax2 = ax * ax;
    let ax3 = ax2 * ax;

    if (ax <= 1.0) {
        return ((a + 2.0) * ax3) - ((a + 3.0) * ax2) + 1.0;
    }

    if (ax < 2.0) {
        return (a * ax3) - (5.0 * a * ax2) + (8.0 * a * ax) - (4.0 * a);
    }

    return 0.0;
}

fn sample_bicubic(uv: vec2<f32>) -> vec4<f32> {
    let size = textureDimensions(texTexture);
    let sizef = vec2<f32>(f32(size.x), f32(size.y));
    let texel = uv * sizef - vec2<f32>(0.5, 0.5);
    let base = floor(texel);
    let f = texel - base;
    let maxCoord = vec2<i32>(i32(size.x) - 1, i32(size.y) - 1);
    var color = vec4<f32>(0.0);
    var total = 0.0;

    for (var y: i32 = -1; y <= 2; y = y + 1) {
        let wy = cubic_weight(f.y - f32(y));
        for (var x: i32 = -1; x <= 2; x = x + 1) {
            let wx = cubic_weight(f.x - f32(x));
            let weight = wx * wy;
            let coord = clamp(
                vec2<i32>(i32(base.x) + x, i32(base.y) + y),
                vec2<i32>(0, 0),
                maxCoord);
            color = color + textureLoad(texTexture, coord, 0) * weight;
            total = total + weight;
        }
    }

    return color / max(total, 0.0001);
}

fn texture_fs_main(input: VertexOutput) -> vec4<f32> {
    var texColor = textureSample(texTexture, texSampler, input.texCoord);
    if (input.color.a < 0.0) {
        texColor = sample_bicubic(input.texCoord);
    }
    let opacity = abs(input.color.a);
    let sourceIsPremultiplied = input.color.g > 0.5;
    let rgbScale = input.color.r;
    let screen_uv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, screen_uv).r;
    if (maskAlpha <= 0.0) {
        discard;
    }
    let coverage = opacity * maskAlpha;
    if (sourceIsPremultiplied) {
        return vec4<f32>(texColor.rgb * rgbScale * maskAlpha, texColor.a * coverage);
    }

    return vec4<f32>(texColor.rgb * rgbScale, texColor.a * coverage);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return texture_fs_main(input);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = texture_fs_main(input);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
";

    public const string GlyphRasterizerShader = @"
struct GlyphUniforms {
    xStart: f32,
    yStart: f32,
    scale: f32,
    glyphIndex: u32,
    atlasX: u32,
    atlasY: u32,
    width: u32,
    height: u32,
    subpixelX: f32,
    _pad0: f32,
    _pad1: f32,
    _pad2: f32,
};

struct GlyphRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    _pad0: u32,
    _pad1: u32,
};

struct Segment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

@group(0) @binding(0) var<uniform> uniforms: GlyphUniforms;
@group(0) @binding(1) var<storage, read> glyphRecords: array<GlyphRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var atlasTexture: texture_storage_2d<rgba8unorm, write>;

" + SharedWgpuMathCode + @"

fn is_point_inside(p: vec2<f32>, record: GlyphRecord) -> bool {
    var winding: i32 = 0;
    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = segments[i];
        if (seg.segmentType == 0u) {
            // Line Segment from A to B
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) { // Upward crossing
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) { // Downward crossing
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
            // Quadratic Bezier from A to C with control point B
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            
            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - p.y;
            
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(a, b, c, &roots, &root_count);
            
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);
                    
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= C.y);
                        }
                    } else {
                        is_valid = true;
                    }
                    
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;

            let a = -A.y + 3.0 * B.y - 3.0 * C.y + D_pt.y;
            let b = 3.0 * A.y - 6.0 * B.y + 3.0 * C.y;
            let c = -3.0 * A.y + 3.0 * B.y;
            let d = A.y - p.y;

            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(a, b, c, d, &roots, &root_count);

            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;

                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < D_pt.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= D_pt.y);
                        }
                    } else {
                        is_valid = true;
                    }

                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * omt * A.x
                                + 3.0 * omt * omt * tc * B.x
                                + 3.0 * omt * tc * tc * C.x
                                + tc * tc * tc * D_pt.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        }
    }
    return winding != 0;
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let x = global_id.x;
    let y = global_id.y;
    
    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }
    
    let glyphIndex = uniforms.glyphIndex;
    let record = glyphRecords[glyphIndex];
    
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);
    
    var coverage: f32 = 0.0;
    
    for (var dy: f32 = 0.0625; dy < 1.0; dy = dy + 0.125) {
        for (var dx: f32 = 0.0625; dx < 1.0; dx = dx + 0.125) {
            let sp = vec2<f32>(px + dx - uniforms.subpixelX, py + dy);
            let fp = vec2<f32>(sp.x / uniforms.scale, -sp.y / uniforms.scale);
            if (is_point_inside(fp, record)) {
                coverage = coverage + 0.015625;
            }
        }
    }
    
    let writeCoord = vec2<u32>(uniforms.atlasX + x, uniforms.atlasY + y);
    textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));
}
";

    public const string PathRasterizerShader = @"
struct PathUniforms {
    xStart: f32,
    yStart: f32,
    scaleX: f32,
    scaleY: f32,
    pathIndex: u32,
    atlasX: u32,
    atlasY: u32,
    width: u32,
    height: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

struct PathRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    fillRule: u32,
    _pad1: u32,
};

struct Segment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

@group(0) @binding(0) var<uniform> uniforms: PathUniforms;
@group(0) @binding(1) var<storage, read> pathRecords: array<PathRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var atlasTexture: texture_storage_2d<rgba8unorm, write>;

" + SharedWgpuMathCode + @"

fn is_point_inside(p: vec2<f32>, record: PathRecord) -> bool {
    var winding: i32 = 0;
    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = segments[i];
        if (seg.segmentType == 0u) {
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            
            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - p.y;
            
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(a, b, c, &roots, &root_count);
            
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);
                    
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= C.y);
                        }
                    } else {
                        is_valid = true;
                    }
                    
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;
            
            let a = -A.y + 3.0 * B.y - 3.0 * C.y + D_pt.y;
            let b = 3.0 * A.y - 6.0 * B.y + 3.0 * C.y;
            let c = -3.0 * A.y + 3.0 * B.y;
            let d = A.y - p.y;
            
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(a, b, c, d, &roots, &root_count);
            
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;
                    
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (p.y < D_pt.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (p.y >= D_pt.y);
                        }
                    } else {
                        is_valid = true;
                    }
                    
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * omt * A.x
                                + 3.0 * omt * omt * tc * B.x
                                + 3.0 * omt * tc * tc * C.x
                                + tc * tc * tc * D_pt.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 3u) {
            let p0 = seg.p0;
            let p1 = seg.p1;
            let center = seg.p2;
            let r = seg.p3;
            let rx = r.x;
            let ry = r.y;
            
            let theta1 = bitcast<f32>(seg._pad0);
            let delta_theta = bitcast<f32>(seg._pad1);
            let phi = bitcast<f32>(seg._pad2);
            
            let cos_phi = cos(phi);
            let sin_phi = sin(phi);
            
            let dy = p.y - center.y;
            
            let rx2 = rx * rx;
            let ry2 = ry * ry;
            
            let A_val = (cos_phi * cos_phi) / rx2 + (sin_phi * sin_phi) / ry2;
            let B_val = 2.0 * dy * cos_phi * sin_phi * (1.0 / rx2 - 1.0 / ry2);
            let C_val = dy * dy * ((sin_phi * sin_phi) / rx2 + (cos_phi * cos_phi) / ry2) - 1.0;
            
            let discriminant = B_val * B_val - 4.0 * A_val * C_val;
            if (discriminant >= 0.0) {
                let sqrt_d = sqrt(discriminant);
                let dx1 = (-B_val - sqrt_d) / (2.0 * A_val);
                let dx2 = (-B_val + sqrt_d) / (2.0 * A_val);
                
                var roots = array<f32, 2>(dx1, dx2);
                for (var r_idx: u32 = 0u; r_idx < 2u; r_idx = r_idx + 1u) {
                    let dx = roots[r_idx];
                    let intersectX = center.x + dx;
                    if (p.x < intersectX) {
                        let localX = dx * cos_phi + dy * sin_phi;
                        let localY = -dx * sin_phi + dy * cos_phi;
                        let theta = atan2(localY / ry, localX / rx);
                        
                        var t: f32 = 0.0;
                        let pi2 = 6.283185307179586;
                        if (delta_theta > 0.0) {
                            let diff = (theta - theta1) - pi2 * floor((theta - theta1) / pi2);
                            t = diff / delta_theta;
                        } else {
                            let diff = (theta1 - theta) - pi2 * floor((theta1 - theta) / pi2);
                            t = diff / (-delta_theta);
                        }
                        
                        let deriv_y = delta_theta * (-rx * sin(theta) * sin_phi + ry * cos(theta) * cos_phi);
                        
                        var is_valid = false;
                        if (deriv_y > 0.0) {
                            is_valid = (t >= 0.0 && t < 1.0);
                        } else if (deriv_y < 0.0) {
                            is_valid = (t > 0.0 && t <= 1.0);
                        }
                        
                        if (is_valid) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        }
    }
    if (record.fillRule == 0u) {
        return abs(winding) % 2 == 1;
    }
    return winding != 0;
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let x = global_id.x;
    let y = global_id.y;
    
    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }
    
    let pathIndex = uniforms.pathIndex;
    let record = pathRecords[pathIndex];
    
    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);
    
    var coverage: f32 = 0.0;
    let sampleGrid = 4u;
    let sampleWeight = 1.0 / 16.0;
    for (var sampleY = 0u; sampleY < sampleGrid; sampleY = sampleY + 1u) {
        for (var sampleX = 0u; sampleX < sampleGrid; sampleX = sampleX + 1u) {
            let offset = (vec2<f32>(f32(sampleX), f32(sampleY)) + 0.5) /
                f32(sampleGrid);
            let samplePosition = vec2<f32>(px, py) + offset;
            let fillPoint = samplePosition / vec2<f32>(uniforms.scaleX, uniforms.scaleY);
            if (is_point_inside(fillPoint, record)) {
                coverage = coverage + sampleWeight;
    }
    }
    }
    
    let writeCoord = vec2<u32>(uniforms.atlasX + x, uniforms.atlasY + y);
    textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));
}
";

    public const string ChartLineShader = @"
const AA_PADDING: f32 = 1.5;

struct VSUniforms {
  transform       : mat4x4<f32>,
  canvasSize      : vec2<f32>,
  devicePixelRatio: f32,
  lineWidthCssPx  : f32,
  scale           : vec2<f32>,
  translate       : vec2<f32>,
};

@group(0) @binding(0) var<uniform> vsUniforms : VSUniforms;

struct FSUniforms {
  color : vec4<f32>,
};

@group(0) @binding(1) var<uniform> fsUniforms : FSUniforms;

@group(0) @binding(2) var<storage, read> points : array<vec2<f32>>;

struct VSOut {
  @builtin(position) clipPosition : vec4<f32>,
  @location(0) acrossDevice       : f32,
  @location(1) @interpolate(flat) widthDevice : f32,
};

fn quadUv(vid : u32) -> vec2<f32> {
  switch (vid) {
    case 0u: { return vec2<f32>(0.0, 0.0); }
    case 1u: { return vec2<f32>(1.0, 0.0); }
    case 2u: { return vec2<f32>(0.0, 1.0); }
    case 3u: { return vec2<f32>(0.0, 1.0); }
    case 4u: { return vec2<f32>(1.0, 0.0); }
    default: { return vec2<f32>(1.0, 1.0); }
  }
}

@vertex
fn vs_main(
  @builtin(vertex_index) vid : u32,
  @builtin(instance_index) iid : u32,
) -> VSOut {
  let uv = quadUv(vid);
  let pA_data = points[iid];
  let pB_data = points[iid + 1u];

  if (pA_data.x != pA_data.x || pA_data.y != pA_data.y ||
      pB_data.x != pB_data.x || pB_data.y != pB_data.y) {
    var out: VSOut;
    out.clipPosition = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    out.acrossDevice = 0.0;
    out.widthDevice = 0.0;
    return out;
  }

  let pA_scaled = pA_data * vsUniforms.scale + vsUniforms.translate;
  let pB_scaled = pB_data * vsUniforms.scale + vsUniforms.translate;

  let clipA = vsUniforms.transform * vec4<f32>(pA_scaled, 0.0, 1.0);
  let clipB = vsUniforms.transform * vec4<f32>(pB_scaled, 0.0, 1.0);

  let ndcA = clipA.xy / clipA.w;
  let ndcB = clipB.xy / clipB.w;
  let screenA = vec2<f32>(
    (ndcA.x * 0.5 + 0.5) * vsUniforms.canvasSize.x,
    (1.0 - (ndcA.y * 0.5 + 0.5)) * vsUniforms.canvasSize.y,
  );
  let screenB = vec2<f32>(
    (ndcB.x * 0.5 + 0.5) * vsUniforms.canvasSize.x,
    (1.0 - (ndcB.y * 0.5 + 0.5)) * vsUniforms.canvasSize.y,
  );

  let delta = screenB - screenA;
  let segLen = length(delta);

  if (segLen < 1e-6) {
    var out : VSOut;
    out.clipPosition = clipA;
    out.acrossDevice = 0.0;
    out.widthDevice = 0.0;
    return out;
  }

  let dir = delta / segLen;
  let perp = vec2<f32>(dir.y, -dir.x);

  let widthHalfCss = vsUniforms.lineWidthCssPx * 0.5;
  let widthHalfDevice = widthHalfCss * vsUniforms.devicePixelRatio;
  let totalHalfDevice = widthHalfDevice + AA_PADDING;

  let offsetDevice = perp * totalHalfDevice * (uv.y * 2.0 - 1.0);
  let lengthOffsetDevice = dir * totalHalfDevice * (uv.x * (segLen + totalHalfDevice * 2.0) - totalHalfDevice);

  let pDevice = mix(screenA, screenB, uv.x) + offsetDevice;

  var out : VSOut;
  let pNdc = vec2<f32>(
    (pDevice.x / vsUniforms.canvasSize.x - 0.5) * 2.0,
    (1.0 - pDevice.y / vsUniforms.canvasSize.y - 0.5) * 2.0,
  );
  out.clipPosition = vec4<f32>(pNdc, 0.0, 1.0);
  out.acrossDevice = (uv.y * 2.0 - 1.0) * totalHalfDevice;
  out.widthDevice = widthHalfDevice;
  return out;
}

@fragment
fn fs_main(in : VSOut) -> @location(0) vec4<f32> {
  let dist = abs(in.acrossDevice) - in.widthDevice;
  let alpha = 1.0 - smoothstep(0.0, AA_PADDING, dist);
  if (alpha <= 0.0) {
    discard;
  }
  return vec4<f32>(fsUniforms.color.rgb, fsUniforms.color.a * alpha);
}
";

    public const string ChartScatterShader = @"
struct VSUniforms {
  transform: mat4x4<f32>,
  viewportPx: vec2<f32>,
  _pad0: vec2<f32>,
  scale: vec2<f32>,
  translate: vec2<f32>,
};

@group(0) @binding(0) var<uniform> vsUniforms: VSUniforms;

struct FSUniforms {
  color: vec4<f32>,
};

@group(0) @binding(1) var<uniform> fsUniforms: FSUniforms;

struct VSIn {
  @location(0) center: vec2<f32>,
  @location(1) radiusPx: f32,
};

struct VSOut {
  @builtin(position) clipPosition: vec4<f32>,
  @location(0) localPx: vec2<f32>,
  @location(1) radiusPx: f32,
};

fn quadCorner(vid : u32) -> vec2<f32> {
  switch (vid) {
    case 0u: { return vec2<f32>(-1.0, -1.0); }
    case 1u: { return vec2<f32>( 1.0, -1.0); }
    case 2u: { return vec2<f32>(-1.0,  1.0); }
    case 3u: { return vec2<f32>(-1.0,  1.0); }
    case 4u: { return vec2<f32>( 1.0, -1.0); }
    default: { return vec2<f32>( 1.0,  1.0); }
  }
}

@vertex
fn vs_main(in: VSIn, @builtin(vertex_index) vertexIndex: u32) -> VSOut {
  let corner = quadCorner(vertexIndex);
  let localPx = corner * in.radiusPx;

  let localClip = localPx * (2.0 / vsUniforms.viewportPx);
  let centerScaled = in.center * vsUniforms.scale + vsUniforms.translate;
  let centerClip = (vsUniforms.transform * vec4<f32>(centerScaled, 0.0, 1.0)).xy;

  var out: VSOut;
  out.clipPosition = vec4<f32>(centerClip + localClip, 0.0, 1.0);
  out.localPx = localPx;
  out.radiusPx = in.radiusPx;
  return out;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4<f32> {
  let dist = length(in.localPx) - in.radiusPx;
  let w = fwidth(dist);
  let a = 1.0 - smoothstep(0.0, w, dist);

  if (a <= 0.0) {
    discard;
  }

  return vec4<f32>(fsUniforms.color.rgb, fsUniforms.color.a * a);
}
";

    public const string PathOpGeometryShader = @"
struct PathOpUniforms {
    op: u32,
    maxDestSegments: u32,
    _pad1: u32,
    _pad2: u32,
};

struct PathRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    fillRule: u32,
    _pad1: u32,
};

struct Segment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

struct OutputSegments {
    count: atomic<u32>,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
    segments: array<Segment>,
};

@group(0) @binding(0) var<uniform> uniforms: PathOpUniforms;
@group(0) @binding(1) var<storage, read> recordA: PathRecord;
@group(0) @binding(2) var<storage, read> segmentsA: array<Segment>;
@group(0) @binding(3) var<storage, read> recordB: PathRecord;
@group(0) @binding(4) var<storage, read> segmentsB: array<Segment>;
@group(0) @binding(5) var<storage, read_write> destRecord: PathRecord;
@group(0) @binding(6) var<storage, read_write> destSegments: OutputSegments;

" + SharedWgpuMathCode + @"

fn is_point_inside_A(p: vec2<f32>) -> bool {
    var winding: i32 = 0;
    let endIdx = recordA.startSegment + recordA.segmentCount;
    for (var i: u32 = recordA.startSegment; i < endIdx; i = i + 1u) {
        let seg = segmentsA[i];
        if (seg.segmentType == 0u) {
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - p.y;
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(a, b, c, &roots, &root_count);
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) { is_valid = (p.y >= A.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y < A.y); }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) { is_valid = (p.y < C.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y >= C.y); }
                    } else {
                        is_valid = true;
                    }
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;
            let a = -A.y + 3.0 * B.y - 3.0 * C.y + D_pt.y;
            let b = 3.0 * A.y - 6.0 * B.y + 3.0 * C.y;
            let c = -3.0 * A.y + 3.0 * B.y;
            let d = A.y - p.y;
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(a, b, c, d, &roots, &root_count);
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) { is_valid = (p.y >= A.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y < A.y); }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) { is_valid = (p.y < D_pt.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y >= D_pt.y); }
                    } else {
                        is_valid = true;
                    }
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * omt * A.x + 3.0 * omt * omt * tc * B.x + 3.0 * omt * tc * tc * C.x + tc * tc * tc * D_pt.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 3u) {
            let p0 = seg.p0; let p1 = seg.p1; let center = seg.p2; let r = seg.p3;
            let rx = r.x; let ry = r.y;
            let theta1 = bitcast<f32>(seg._pad0);
            let delta_theta = bitcast<f32>(seg._pad1);
            let phi = bitcast<f32>(seg._pad2);
            let cos_phi = cos(phi); let sin_phi = sin(phi);
            let dy = p.y - center.y;
            let rx2 = rx * rx; let ry2 = ry * ry;
            let A_val = (cos_phi * cos_phi) / rx2 + (sin_phi * sin_phi) / ry2;
            let B_val = 2.0 * dy * cos_phi * sin_phi * (1.0 / rx2 - 1.0 / ry2);
            let C_val = dy * dy * ((sin_phi * sin_phi) / rx2 + (cos_phi * cos_phi) / ry2) - 1.0;
            let discriminant = B_val * B_val - 4.0 * A_val * C_val;
            if (discriminant >= 0.0) {
                let sqrt_d = sqrt(discriminant);
                let dx1 = (-B_val - sqrt_d) / (2.0 * A_val);
                let dx2 = (-B_val + sqrt_d) / (2.0 * A_val);
                var roots = array<f32, 2>(dx1, dx2);
                for (var r_idx: u32 = 0u; r_idx < 2u; r_idx = r_idx + 1u) {
                    let dx = roots[r_idx];
                    let intersectX = center.x + dx;
                    if (p.x < intersectX) {
                        let localX = dx * cos_phi + dy * sin_phi;
                        let localY = -dx * sin_phi + dy * cos_phi;
                        let theta = atan2(localY / ry, localX / rx);
                        var t: f32 = 0.0;
                        let pi2 = 6.283185307179586;
                        if (delta_theta > 0.0) {
                            let diff = (theta - theta1) - pi2 * floor((theta - theta1) / pi2);
                            t = diff / delta_theta;
                        } else {
                            let diff = (theta1 - theta) - pi2 * floor((theta1 - theta) / pi2);
                            t = diff / (-delta_theta);
                        }
                        let deriv_y = delta_theta * (-rx * sin(theta) * sin_phi + ry * cos(theta) * cos_phi);
                        var is_valid = false;
                        if (deriv_y > 0.0) { is_valid = (t >= 0.0 && t < 1.0); }
                        else if (deriv_y < 0.0) { is_valid = (t > 0.0 && t <= 1.0); }
                        if (is_valid) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        }
    }
    if (recordA.fillRule == 0u) {
        return abs(winding) % 2 == 1;
    }
    return winding != 0;
}

fn is_point_inside_B(p: vec2<f32>) -> bool {
    var winding: i32 = 0;
    let endIdx = recordB.startSegment + recordB.segmentCount;
    for (var i: u32 = recordB.startSegment; i < endIdx; i = i + 1u) {
        let seg = segmentsB[i];
        if (seg.segmentType == 0u) {
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= p.y) {
                if (B.y > p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding + 1;
                    }
                }
            } else {
                if (B.y <= p.y) {
                    let t = (p.y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    if (p.x < intersectX) {
                        winding = winding - 1;
                    }
                }
            }
        } else if (seg.segmentType == 1u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - p.y;
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(a, b, c, &roots, &root_count);
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) { is_valid = (p.y >= A.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y < A.y); }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) { is_valid = (p.y < C.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y >= C.y); }
                    } else {
                        is_valid = true;
                    }
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;
            let a = -A.y + 3.0 * B.y - 3.0 * C.y + D_pt.y;
            let b = 3.0 * A.y - 6.0 * B.y + 3.0 * C.y;
            let c = -3.0 * A.y + 3.0 * B.y;
            let d = A.y - p.y;
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(a, b, c, d, &roots, &root_count);
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) { is_valid = (p.y >= A.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y < A.y); }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) { is_valid = (p.y < D_pt.y); }
                        else if (deriv_y < 0.0) { is_valid = (p.y >= D_pt.y); }
                    } else {
                        is_valid = true;
                    }
                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * omt * A.x + 3.0 * omt * omt * tc * B.x + 3.0 * omt * tc * tc * C.x + tc * tc * tc * D_pt.x;
                        if (p.x < x_t) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 3u) {
            let p0 = seg.p0; let p1 = seg.p1; let center = seg.p2; let r = seg.p3;
            let rx = r.x; let ry = r.y;
            let theta1 = bitcast<f32>(seg._pad0);
            let delta_theta = bitcast<f32>(seg._pad1);
            let phi = bitcast<f32>(seg._pad2);
            let cos_phi = cos(phi); let sin_phi = sin(phi);
            let dy = p.y - center.y;
            let rx2 = rx * rx; let ry2 = ry * ry;
            let A_val = (cos_phi * cos_phi) / rx2 + (sin_phi * sin_phi) / ry2;
            let B_val = 2.0 * dy * cos_phi * sin_phi * (1.0 / rx2 - 1.0 / ry2);
            let C_val = dy * dy * ((sin_phi * sin_phi) / rx2 + (cos_phi * cos_phi) / ry2) - 1.0;
            let discriminant = B_val * B_val - 4.0 * A_val * C_val;
            if (discriminant >= 0.0) {
                let sqrt_d = sqrt(discriminant);
                let dx1 = (-B_val - sqrt_d) / (2.0 * A_val);
                let dx2 = (-B_val + sqrt_d) / (2.0 * A_val);
                var roots = array<f32, 2>(dx1, dx2);
                for (var r_idx: u32 = 0u; r_idx < 2u; r_idx = r_idx + 1u) {
                    let dx = roots[r_idx];
                    let intersectX = center.x + dx;
                    if (p.x < intersectX) {
                        let localX = dx * cos_phi + dy * sin_phi;
                        let localY = -dx * sin_phi + dy * cos_phi;
                        let theta = atan2(localY / ry, localX / rx);
                        var t: f32 = 0.0;
                        let pi2 = 6.283185307179586;
                        if (delta_theta > 0.0) {
                            let diff = (theta - theta1) - pi2 * floor((theta - theta1) / pi2);
                            t = diff / delta_theta;
                        } else {
                            let diff = (theta1 - theta) - pi2 * floor((theta1 - theta) / pi2);
                            t = diff / (-delta_theta);
                        }
                        let deriv_y = delta_theta * (-rx * sin(theta) * sin_phi + ry * cos(theta) * cos_phi);
                        var is_valid = false;
                        if (deriv_y > 0.0) { is_valid = (t >= 0.0 && t < 1.0); }
                        else if (deriv_y < 0.0) { is_valid = (t > 0.0 && t <= 1.0); }
                        if (is_valid) {
                            if (deriv_y > 0.0) { winding = winding + 1; }
                            else if (deriv_y < 0.0) { winding = winding - 1; }
                        }
                    }
                }
            }
        }
    }
    if (recordB.fillRule == 0u) {
        return abs(winding) % 2 == 1;
    }
    return winding != 0;
}

fn intersect_lines(p0: vec2<f32>, p1: vec2<f32>, q0: vec2<f32>, q1: vec2<f32>, t: ptr<function, f32>, u: ptr<function, f32>) -> bool {
    let r = p1 - p0;
    let s = q1 - q0;
    let denom = r.x * s.y - r.y * s.x;
    if (abs(denom) < 0.00001) {
        return false;
    }
    let t_val = ((q0.x - p0.x) * s.y - (q0.y - p0.y) * s.x) / denom;
    let u_val = ((q0.x - p0.x) * r.y - (q0.y - p0.y) * r.x) / denom;
    if (t_val >= -0.001 && t_val <= 1.001 && u_val >= -0.001 && u_val <= 1.001) {
        *t = clamp(t_val, 0.0, 1.0);
        *u = clamp(u_val, 0.0, 1.0);
        return true;
    }
    return false;
}

fn evaluate_segment(seg: Segment, t: f32) -> vec2<f32> {
    if (seg.segmentType == 0u) {
        return mix(seg.p0, seg.p1, t);
    } else if (seg.segmentType == 1u) {
        let p01 = mix(seg.p0, seg.p1, t);
        let p12 = mix(seg.p1, seg.p2, t);
        return mix(p01, p12, t);
    } else if (seg.segmentType == 2u) {
        let p01 = mix(seg.p0, seg.p1, t);
        let p12 = mix(seg.p1, seg.p2, t);
        let p23 = mix(seg.p2, seg.p3, t);
        let p012 = mix(p01, p12, t);
        let p123 = mix(p12, p23, t);
        return mix(p012, p123, t);
    } else { // Arc
        let theta1 = bitcast<f32>(seg._pad0);
        let delta_theta = bitcast<f32>(seg._pad1);
        let phi = bitcast<f32>(seg._pad2);
        let theta = theta1 + t * delta_theta;
        let center = seg.p2;
        let r = seg.p3;
        let rx = r.x;
        let ry = r.y;
        let cos_phi = cos(phi);
        let sin_phi = sin(phi);
        let cosT = cos(theta);
        let sinT = sin(theta);
        return vec2<f32>(
            rx * cosT * cos_phi - ry * sinT * sin_phi + center.x,
            rx * cosT * sin_phi + ry * sinT * cos_phi + center.y
        );
    }
}

fn split_segment(seg: Segment, t: f32, left: ptr<function, Segment>, right: ptr<function, Segment>) {
    (*left).segmentType = seg.segmentType;
    (*right).segmentType = seg.segmentType;
    (*left)._pad0 = seg._pad0; (*left)._pad1 = seg._pad1; (*left)._pad2 = seg._pad2;
    (*right)._pad0 = seg._pad0; (*right)._pad1 = seg._pad1; (*right)._pad2 = seg._pad2;

    if (seg.segmentType == 0u) {
        let p_t = mix(seg.p0, seg.p1, t);
        (*left).p0 = seg.p0; (*left).p1 = p_t;
        (*right).p0 = p_t; (*right).p1 = seg.p1;
    } else if (seg.segmentType == 1u) {
        let p01 = mix(seg.p0, seg.p1, t);
        let p12 = mix(seg.p1, seg.p2, t);
        let p_t = mix(p01, p12, t);
        (*left).p0 = seg.p0; (*left).p1 = p01; (*left).p2 = p_t;
        (*right).p0 = p_t; (*right).p1 = p12; (*right).p2 = seg.p2;
    } else if (seg.segmentType == 2u) {
        let p01 = mix(seg.p0, seg.p1, t);
        let p12 = mix(seg.p1, seg.p2, t);
        let p23 = mix(seg.p2, seg.p3, t);
        let p012 = mix(p01, p12, t);
        let p123 = mix(p12, p23, t);
        let p_t = mix(p012, p123, t);
        (*left).p0 = seg.p0; (*left).p1 = p01; (*left).p2 = p012; (*left).p3 = p_t;
        (*right).p0 = p_t; (*right).p1 = p123; (*right).p2 = p23; (*right).p3 = seg.p3;
    } else if (seg.segmentType == 3u) {
        let theta1 = bitcast<f32>(seg._pad0);
        let delta_theta = bitcast<f32>(seg._pad1);
        let phi = bitcast<f32>(seg._pad2);
        let split_theta = theta1 + t * delta_theta;
        (*left).p0 = seg.p0;
        let center = seg.p2; let r = seg.p3; let rx = r.x; let ry = r.y;
        let cos_phi = cos(phi); let sin_phi = sin(phi);
        let cosT = cos(split_theta); let sinT = sin(split_theta);
        let p_t = vec2<f32>(
            rx * cosT * cos_phi - ry * sinT * sin_phi + center.x,
            rx * cosT * sin_phi + ry * sinT * cos_phi + center.y
        );
        (*left).p1 = p_t; (*left).p2 = center; (*left).p3 = r;
        (*left)._pad0 = seg._pad0; (*left)._pad1 = bitcast<u32>(t * delta_theta);
        
        (*right).p0 = p_t; (*right).p1 = seg.p1; (*right).p2 = center; (*right).p3 = r;
        (*right)._pad0 = bitcast<u32>(split_theta); (*right)._pad1 = bitcast<u32>((1.0 - t) * delta_theta);
    }
}

fn get_sub_segment(seg: Segment, t0: f32, t1: f32) -> Segment {
    if (t0 <= 0.0001 && t1 >= 0.9999) {
        return seg;
    }
    var left = seg;
    var right = seg;
    split_segment(seg, t1, &left, &right);
    var sub_left = left;
    var sub_right = left;
    let u = clamp(t0 / max(t1, 0.0001), 0.0, 1.0);
    split_segment(left, u, &sub_left, &sub_right);
    return sub_right;
}

fn reverse_segment(seg: Segment) -> Segment {
    var out = seg;
    if (seg.segmentType == 0u) {
        out.p0 = seg.p1;
        out.p1 = seg.p0;
    } else if (seg.segmentType == 1u) {
        out.p0 = seg.p2;
        out.p2 = seg.p0;
    } else if (seg.segmentType == 2u) {
        out.p0 = seg.p3;
        out.p3 = seg.p0;
        out.p1 = seg.p2;
        out.p2 = seg.p1;
    } else if (seg.segmentType == 3u) {
        out.p0 = seg.p1;
        out.p1 = seg.p0;
        let delta_theta = bitcast<f32>(seg._pad1);
        out._pad1 = bitcast<u32>(-delta_theta);
        let theta1 = bitcast<f32>(seg._pad0);
        out._pad0 = bitcast<u32>(theta1 + delta_theta);
    }
    return out;
}

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x == 999999u) {
        destRecord.startSegment = 0u;
    }
    let segmentCountA = recordA.segmentCount;
    let segmentCountB = recordB.segmentCount;
    
    if (global_id.x >= segmentCountA + segmentCountB) {
        return;
    }
    
    let isA = global_id.x < segmentCountA;
    var seg: Segment;
    if (isA) {
        seg = segmentsA[recordA.startSegment + global_id.x];
    } else {
        seg = segmentsB[recordB.startSegment + (global_id.x - segmentCountA)];
    }
    
    var t_values = array<f32, 16>();
    t_values[0] = 0.0;
    t_values[1] = 1.0;
    var count = 2u;
    
    if (isA) {
        for (var j: u32 = 0u; j < segmentCountB; j = j + 1u) {
            let otherSeg = segmentsB[recordB.startSegment + j];
            let stepsA = select(16u, 1u, seg.segmentType == 0u);
            let stepsB = select(16u, 1u, otherSeg.segmentType == 0u);
            
            for (var stepA: u32 = 0u; stepA < stepsA; stepA = stepA + 1u) {
                let t0 = f32(stepA) / f32(stepsA); let t1 = f32(stepA + 1u) / f32(stepsA);
                let cp0 = evaluate_segment(seg, t0); let cp1 = evaluate_segment(seg, t1);
                
                for (var stepB: u32 = 0u; stepB < stepsB; stepB = stepB + 1u) {
                    let u0 = f32(stepB) / f32(stepsB); let u1 = f32(stepB + 1u) / f32(stepsB);
                    let cq0 = evaluate_segment(otherSeg, u0); let cq1 = evaluate_segment(otherSeg, u1);
                    
                    var t_sub: f32 = 0.0; var u_sub: f32 = 0.0;
                    if (intersect_lines(cp0, cp1, cq0, cq1, &t_sub, &u_sub)) {
                        let t_intersect = clamp(t0 + t_sub * (t1 - t0), 0.0, 1.0);
                        var duplicate = false;
                        for (var d: u32 = 0u; d < count; d = d + 1u) {
                            if (abs(t_values[d] - t_intersect) < 0.001) { duplicate = true; break; }
                        }
                        if (!duplicate && count < 16u) {
                            t_values[count] = t_intersect;
                            count = count + 1u;
                        }
                    }
                }
            }
        }
    } else {
        for (var i: u32 = 0u; i < segmentCountA; i = i + 1u) {
            let otherSeg = segmentsA[recordA.startSegment + i];
            let stepsA = select(16u, 1u, seg.segmentType == 0u);
            let stepsB = select(16u, 1u, otherSeg.segmentType == 0u);
            
            for (var stepA: u32 = 0u; stepA < stepsA; stepA = stepA + 1u) {
                let t0 = f32(stepA) / f32(stepsA); let t1 = f32(stepA + 1u) / f32(stepsA);
                let cp0 = evaluate_segment(seg, t0); let cp1 = evaluate_segment(seg, t1);
                
                for (var stepB: u32 = 0u; stepB < stepsB; stepB = stepB + 1u) {
                    let u0 = f32(stepB) / f32(stepsB); let u1 = f32(stepB + 1u) / f32(stepsB);
                    let cq0 = evaluate_segment(otherSeg, u0); let cq1 = evaluate_segment(otherSeg, u1);
                    
                    var t_sub: f32 = 0.0; var u_sub: f32 = 0.0;
                    if (intersect_lines(cp0, cp1, cq0, cq1, &t_sub, &u_sub)) {
                        let t_intersect = clamp(t0 + t_sub * (t1 - t0), 0.0, 1.0);
                        var duplicate = false;
                        for (var d: u32 = 0u; d < count; d = d + 1u) {
                            if (abs(t_values[d] - t_intersect) < 0.001) { duplicate = true; break; }
                        }
                        if (!duplicate && count < 16u) {
                            t_values[count] = t_intersect;
                            count = count + 1u;
                        }
                    }
                }
            }
        }
    }
    
    // Sort t_values using simple bubble sort
    if (count > 2u) {
        for (var step: u32 = 0u; step < count - 1u; step = step + 1u) {
            for (var idx: u32 = 0u; idx < count - step - 1u; idx = idx + 1u) {
                if (t_values[idx] > t_values[idx + 1u]) {
                    let temp = t_values[idx];
                    t_values[idx] = t_values[idx + 1u];
                    t_values[idx + 1u] = temp;
                }
            }
        }
    }
    
    // Classify and output sub-segments
    for (var k: u32 = 0u; k < count - 1u; k = k + 1u) {
        let t0 = t_values[k];
        let t1 = t_values[k + 1u];
        let sub = get_sub_segment(seg, t0, t1);
        let mid = evaluate_segment(sub, 0.5);
        let mid_perturbed = mid + vec2<f32>(1e-4, 1.5e-4);
        
        var inside = false;
        if (isA) {
            inside = is_point_inside_B(mid_perturbed);
        } else {
            inside = is_point_inside_A(mid_perturbed);
        }
        
        var keep = false;
        var rev = false;
        let op = uniforms.op;
        
        if (op == 0u) { // Difference (A - B)
            if (isA) { keep = !inside; }
            else { keep = inside; rev = true; }
        } else if (op == 1u) { // Intersect
            keep = inside;
        } else if (op == 2u) { // Union
            keep = !inside;
        } else if (op == 3u) { // XOR
            keep = true;
            if (inside) { rev = true; }
        } else if (op == 4u) { // Reverse Difference (B - A)
            if (isA) { keep = inside; rev = true; }
            else { keep = !inside; }
        }
        
        if (keep) {
            var finalSeg = sub;
            if (rev) {
                finalSeg = reverse_segment(sub);
            }
            let destIdx = atomicAdd(&destSegments.count, 1u);
            if (destIdx < uniforms.maxDestSegments) {
                destSegments.segments[destIdx] = finalSeg;
            }
        }
    }
}
";

    public const string PathOpRecordFinalizerShader = @"
struct PathOpUniforms {
    op: u32,
    maxDestSegments: u32,
    _pad1: u32,
    _pad2: u32,
};

struct PathRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    fillRule: u32,
    _pad1: u32,
};

struct OutputSegments {
    count: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

@group(0) @binding(0) var<uniform> uniforms: PathOpUniforms;
@group(0) @binding(1) var<storage, read> recordA: PathRecord;
@group(0) @binding(2) var<storage, read> recordB: PathRecord;
@group(0) @binding(3) var<storage, read_write> destRecord: PathRecord;
@group(0) @binding(4) var<storage, read> destSegments: OutputSegments;

@compute @workgroup_size(1)
fn cs_main() {
    destRecord.startSegment = 0u;
    destRecord.segmentCount = min(destSegments.count, uniforms.maxDestSegments);
    destRecord.fillRule = 1u;
    destRecord._pad1 = 0u;
    
    let op = uniforms.op;
    if (op == 0u) { // Difference (A - B)
        destRecord.minX = recordA.minX;
        destRecord.minY = recordA.minY;
        destRecord.maxX = recordA.maxX;
        destRecord.maxY = recordA.maxY;
    } else if (op == 4u) { // Reverse Difference (B - A)
        destRecord.minX = recordB.minX;
        destRecord.minY = recordB.minY;
        destRecord.maxX = recordB.maxX;
        destRecord.maxY = recordB.maxY;
    } else if (op == 1u) { // Intersect
        destRecord.minX = max(recordA.minX, recordB.minX);
        destRecord.minY = max(recordA.minY, recordB.minY);
        destRecord.maxX = min(recordA.maxX, recordB.maxX);
        destRecord.maxY = min(recordA.maxY, recordB.maxY);
    } else { // Union / XOR
        destRecord.minX = min(recordA.minX, recordB.minX);
        destRecord.minY = min(recordA.minY, recordB.minY);
        destRecord.maxX = max(recordA.maxX, recordB.maxX);
        destRecord.maxY = max(recordA.maxY, recordB.maxY);
    }
}
";

    public const string AdvancedBlendShader = """
@group(0) @binding(0) var destinationTexture: texture_2d<f32>;
@group(0) @binding(1) var sourceTexture: texture_2d<f32>;

const blendMode = __BLEND_MODE__u;

@vertex
fn vs_main(@builtin(vertex_index) vertexIndex: u32) -> @builtin(position) vec4<f32> {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0));
    return vec4<f32>(positions[vertexIndex], 0.0, 1.0);
}

fn screen(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return backdrop + source - backdrop * source;
}

fn hard_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop * (2.0 * source);
    }
    return backdrop + (2.0 * source - 1.0) -
        backdrop * (2.0 * source - 1.0);
}

fn hard_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        hard_light_component(backdrop.r, source.r),
        hard_light_component(backdrop.g, source.g),
        hard_light_component(backdrop.b, source.b));
}

fn color_dodge_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) {
        return 0.0;
    }
    if (source >= 1.0) {
        return 1.0;
    }
    return min(1.0, backdrop / (1.0 - source));
}

fn color_dodge(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        color_dodge_component(backdrop.r, source.r),
        color_dodge_component(backdrop.g, source.g),
        color_dodge_component(backdrop.b, source.b));
}

fn color_burn_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) {
        return 1.0;
    }
    if (source <= 0.0) {
        return 0.0;
    }
    return 1.0 - min(1.0, (1.0 - backdrop) / source);
}

fn color_burn(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        color_burn_component(backdrop.r, source.r),
        color_burn_component(backdrop.g, source.g),
        color_burn_component(backdrop.b, source.b));
}

fn soft_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop -
            (1.0 - 2.0 * source) * backdrop * (1.0 - backdrop);
    }

    var curve = sqrt(backdrop);
    if (backdrop <= 0.25) {
        curve = ((16.0 * backdrop - 12.0) * backdrop + 4.0) * backdrop;
    }
    return backdrop + (2.0 * source - 1.0) * (curve - backdrop);
}

fn soft_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        soft_light_component(backdrop.r, source.r),
        soft_light_component(backdrop.g, source.g),
        soft_light_component(backdrop.b, source.b));
}

fn luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn saturation(color: vec3<f32>) -> f32 {
    return max(max(color.r, color.g), color.b) -
        min(min(color.r, color.g), color.b);
}

fn clip_color(input: vec3<f32>) -> vec3<f32> {
    var color = input;
    let lightness = luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness / (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) /
                (maximum - lightness);
    }
    return color;
}

fn set_luminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return clip_color(color + vec3<f32>(lightness - luminosity(color)));
}

fn set_saturation(color: vec3<f32>, targetSaturation: f32) -> vec3<f32> {
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (maximum <= minimum) {
        return vec3<f32>(0.0);
    }
    return (color - vec3<f32>(minimum)) * targetSaturation / (maximum - minimum);
}

fn blend(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    switch blendMode {
        case 11u: {
            return backdrop * source;
        }
        case 12u: {
            return screen(backdrop, source);
        }
        case 13u: {
            return min(backdrop, source);
        }
        case 14u: {
            return max(backdrop, source);
        }
        case 15u: {
            return backdrop + source - 2.0 * backdrop * source;
        }
        case 18u: {
            return hard_light(source, backdrop);
        }
        case 19u: {
            return color_dodge(backdrop, source);
        }
        case 20u: {
            return color_burn(backdrop, source);
        }
        case 21u: {
            return hard_light(backdrop, source);
        }
        case 22u: {
            return soft_light(backdrop, source);
        }
        case 23u: {
            return abs(backdrop - source);
        }
        case 24u: {
            return set_luminosity(
                set_saturation(source, saturation(backdrop)),
                luminosity(backdrop));
        }
        case 25u: {
            return set_luminosity(
                set_saturation(backdrop, saturation(source)),
                luminosity(backdrop));
        }
        case 26u: {
            return set_luminosity(source, luminosity(backdrop));
        }
        case 27u: {
            return set_luminosity(backdrop, luminosity(source));
        }
        default: {
            return source;
        }
    }
}

fn unpremultiply(color: vec3<f32>, alpha: f32) -> vec3<f32> {
    if (alpha <= 0.0) {
        return vec3<f32>(0.0);
    }

    return color / alpha;
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let pixel = vec2<i32>(position.xy);
    let destination = clamp(
        textureLoad(destinationTexture, pixel, 0),
        vec4<f32>(0.0),
        vec4<f32>(1.0));
    let source = clamp(
        textureLoad(sourceTexture, pixel, 0),
        vec4<f32>(0.0),
        vec4<f32>(1.0));

    if (blendMode == 1u) {
        return source;
    }

    let sourceAlpha = source.a;
    let destinationAlpha = destination.a;
    let straightSource = unpremultiply(source.rgb, sourceAlpha);
    let straightDestination = unpremultiply(destination.rgb, destinationAlpha);
    let mixed = clamp(
        blend(straightDestination, straightSource),
        vec3<f32>(0.0),
        vec3<f32>(1.0));
    let result = vec4<f32>(
        source.rgb * (1.0 - destinationAlpha) +
            destination.rgb * (1.0 - sourceAlpha) +
            mixed * sourceAlpha * destinationAlpha,
        sourceAlpha + destinationAlpha - sourceAlpha * destinationAlpha);
    return clamp(result, vec4<f32>(0.0), vec4<f32>(1.0));
}
""";

}
