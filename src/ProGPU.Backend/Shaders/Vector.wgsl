// Algorithm: Expand and transform batched vector primitives and meshes; direct 2D strokes use a scalar screen-space fast path for conformal transforms and an exact transformed local-outline path with derivative anti-aliasing for anisotropic or sheared transforms; reserved negative width encodings select either the Skia one-framebuffer-pixel hairline or an arbitrary positive fixed-device width, both expanded after the late transform, while one fixed quad evaluates each device or affine round cap and device join analytically with hard-owned body seams; evaluate analytic curves, arcs, and quarter-pixel-snapped periodic dot grids; use exact single-evaluation box/rounded-box distance gradients; then shade fills, strokes, gradients, vertex-color blends, and edges. Dedicated solid-rectangle and adaptively selected circular-rounded-rectangle entry points avoid the general material/path program for dense UI chrome.
// Time complexity: O(1) per vertex or fragment under the shader's fixed primitive and gradient limits; static draws reuse CPU-cached maximum/minimum singular values, dynamic GPU-transformed direct strokes and fixed-device bounds add fixed 2x2 matrix arithmetic and two square roots per vertex, non-conformal arc quads test four analytic extrema per vertex, fixed-device caps/joins use one fixed quad with bounded line-intersection and at most four signed-edge evaluations, and non-conformal or analytic fixed-device stroke fragments add fixed derivative/gradient arithmetic.
// Space complexity: O(1) local storage and bounded uniform/storage reads; texture masks add one sample per fragment while analytic rounded and uniform-opacity masks add fixed derivative arithmetic and no texture bandwidth.
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
    dpiScale: f32,
    pad0: f32,
    renderOrigin: vec2<f32>,
    pad1: vec2<f32>,
};


@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read> brushes: array<Brush>;
@group(0) @binding(2) var<storage, read> gradientStops: array<GradientStop>;
@group(1) @binding(0) var pathAtlasSampler: sampler;
@group(1) @binding(1) var pathAtlasTexture: texture_2d<f32>;
@group(2) @binding(0) var maskSampler: sampler;
@group(2) @binding(1) var maskTexture: texture_2d<f32>;

struct MaskSamplingUniforms {
    coordinate0: vec4<f32>,
    coordinate1: vec4<f32>,
    bounds: vec4<f32>,
    cornerRadiiX: vec4<f32>,
    cornerRadiiY: vec4<f32>,
    options: vec4<f32>,
};

@group(2) @binding(2) var<uniform> maskSampling: MaskSamplingUniforms;

fn analytic_rounded_mask_alpha(position: vec2<f32>) -> f32 {
    let local = vec2<f32>(
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate0.xyz),
        dot(vec3<f32>(position, 1.0), maskSampling.coordinate1.xyz));
    let bounds = maskSampling.bounds;
    let edge = max(
        max(bounds.x - local.x, local.x - bounds.z),
        max(bounds.y - local.y, local.y - bounds.w));

    var center = vec2<f32>(0.0);
    var radius = vec2<f32>(0.0);
    var usesCorner = false;
    if (local.x < bounds.x + maskSampling.cornerRadiiX.x &&
        local.y < bounds.y + maskSampling.cornerRadiiY.x) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.x, maskSampling.cornerRadiiY.x);
        center = vec2<f32>(bounds.x + radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - maskSampling.cornerRadiiX.y &&
               local.y < bounds.y + maskSampling.cornerRadiiY.y) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.y, maskSampling.cornerRadiiY.y);
        center = vec2<f32>(bounds.z - radius.x, bounds.y + radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x > bounds.z - maskSampling.cornerRadiiX.z &&
               local.y > bounds.w - maskSampling.cornerRadiiY.z) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.z, maskSampling.cornerRadiiY.z);
        center = vec2<f32>(bounds.z - radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    } else if (local.x < bounds.x + maskSampling.cornerRadiiX.w &&
               local.y > bounds.w - maskSampling.cornerRadiiY.w) {
        radius = vec2<f32>(maskSampling.cornerRadiiX.w, maskSampling.cornerRadiiY.w);
        center = vec2<f32>(bounds.x + radius.x, bounds.w - radius.y);
        usesCorner = all(radius > vec2<f32>(0.0));
    }

    let safeRadius = max(radius, vec2<f32>(0.000001));
    let ellipsePoint = (local - center) / safeRadius;
    let ellipse = dot(ellipsePoint, ellipsePoint) - 1.0;
    let implicit = select(edge, ellipse, usesCorner);
    let antialiasWidth = max(fwidth(implicit), 0.0001);
    return clamp(0.5 - implicit / antialiasWidth, 0.0, 1.0);
}

fn sample_mask_alpha(position: vec2<f32>) -> f32 {
    if (maskSampling.options.x < 0.5) {
        return 1.0;
    }

    let targetPosition = position + uniforms.renderOrigin;
    if (maskSampling.options.x > 1.5) {
        return analytic_rounded_mask_alpha(targetPosition) *
            maskSampling.options.y;
    }

    let uv = (targetPosition - maskSampling.coordinate0.xy) * maskSampling.coordinate1.xy;
    let sampled = textureSample(maskTexture, maskSampler, clamp(uv, vec2<f32>(0.0), vec2<f32>(1.0))).r;
    let inside = all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0));
    return select(0.0, sampled, inside);
}

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
    @location(8) @interpolate(flat) localStrokeMode: f32,
    @location(9) brushCoord: vec2<f32>,
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

fn cross_2d(left: vec2<f32>, right: vec2<f32>) -> f32 {
    return left.x * right.y - left.y * right.x;
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

fn arc_bounds_center(
    center: vec2<f32>,
    axisX: vec2<f32>,
    axisY: vec2<f32>,
    theta1: f32,
    deltaTheta: f32) -> vec2<f32> {
    let start = arc_eval(center, axisX, axisY, theta1);
    let end = arc_eval(center, axisX, axisY, theta1 + deltaTheta);
    var boundsMin = min(start, end);
    var boundsMax = max(start, end);

    let xMinimumTheta = atan2(axisY.x, axisX.x);
    if (is_angle_inside_arc(xMinimumTheta, theta1, deltaTheta)) {
        let point = arc_eval(center, axisX, axisY, xMinimumTheta);
        boundsMin = min(boundsMin, point);
        boundsMax = max(boundsMax, point);
    }
    let xMaximumTheta = xMinimumTheta + PROGPU_TWO_PI * 0.5;
    if (is_angle_inside_arc(xMaximumTheta, theta1, deltaTheta)) {
        let point = arc_eval(center, axisX, axisY, xMaximumTheta);
        boundsMin = min(boundsMin, point);
        boundsMax = max(boundsMax, point);
    }

    let yMinimumTheta = atan2(axisY.y, axisX.y);
    if (is_angle_inside_arc(yMinimumTheta, theta1, deltaTheta)) {
        let point = arc_eval(center, axisX, axisY, yMinimumTheta);
        boundsMin = min(boundsMin, point);
        boundsMax = max(boundsMax, point);
    }
    let yMaximumTheta = yMinimumTheta + PROGPU_TWO_PI * 0.5;
    if (is_angle_inside_arc(yMaximumTheta, theta1, deltaTheta)) {
        let point = arc_eval(center, axisX, axisY, yMaximumTheta);
        boundsMin = min(boundsMin, point);
        boundsMax = max(boundsMax, point);
    }

    return (boundsMin + boundsMax) * 0.5;
}

fn affine_stroke_scales(transform: mat4x4<f32>) -> vec2<f32> {
    // Match TransformMetrics.TryGetStrokeScale for sigma_max and derive
    // sigma_min from |determinant| / sigma_max. Matrix indexing returns columns
    // in WGSL, matching the uploaded System.Numerics basis vectors.
    let axisX = transform[0].xy;
    let axisY = transform[1].xy;
    let sum = dot(axisX, axisX) + dot(axisY, axisY);
    let determinant = axisX.x * axisY.y - axisX.y * axisY.x;
    let discriminant = max(
        0.0,
        sum * sum - 4.0 * determinant * determinant);
    let maximumScale = sqrt(max(0.0, (sum + sqrt(discriminant)) * 0.5));
    // NaN fails both comparisons. Infinity fails the upper bound. Invalid
    // transforms fail closed through the zero pair.
    if (!(maximumScale > 0.0 && maximumScale <= 3.402823e38)) {
        return vec2<f32>(0.0);
    }

    let minimumScale = abs(determinant) / maximumScale;
    if (!(minimumScale >= 0.0 && minimumScale <= 3.402823e38)) {
        return vec2<f32>(maximumScale);
    }
    return vec2<f32>(maximumScale, minimumScale);
}

fn direct_2d_stroke_scales(isStatic: bool, useGpuTransforms: bool) -> vec2<f32> {
    if (isStatic) {
        // pad1.xy is precomputed by DxfStaticBuffer. Fall back to the matrix so
        // buffers produced by older/custom writers preserve the same result.
        let cachedScales = uniforms.pad1;
        if (cachedScales.x > 0.0 && cachedScales.x <= 3.402823e38 &&
            cachedScales.y > 0.0 && cachedScales.y <= cachedScales.x) {
            return cachedScales;
        }
        return affine_stroke_scales(uniforms.mvp);
    }
    if (useGpuTransforms) {
        return affine_stroke_scales(uniforms.view);
    }
    return vec2<f32>(1.0);
}

fn is_conformal_stroke_transform(scales: vec2<f32>) -> bool {
    let tolerance = max(1.0, scales.x) * 0.0001;
    return abs(scales.x - scales.y) <= tolerance;
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

    // Rectangles/ellipses keep their stroke width in local SDF coordinates and
    // therefore scale through interpolation. Hairlines intentionally stay one
    // device pixel. Direct strokes retain the screen-space scalar fast path for
    // conformal transforms and transform their expanded local outline exactly
    // for anisotropic or sheared transforms.
    let isAnalyticSdf = sType < 3u;
    let isHairlineAdornment = sType == 22u || sType == 23u;
    let isDirect2DStroke =
        sType == 3u || sType == 5u || sType == 6u || sType == 12u ||
        isHairlineAdornment;
    let isLocalPolygonSdf = sType >= 13u && sType <= 17u;
    // strokeThickness is overloaded as quadrilateral p3.y for types 14-17;
    // only analytic/direct primitives own it as an actual stroke width.
    let isHairlineStroke =
        (isAnalyticSdf || isDirect2DStroke) &&
        input.strokeThickness == -1.0;
    let isFixedDeviceStroke =
        (isAnalyticSdf || isDirect2DStroke) &&
        input.strokeThickness < -1.0;
    let fixedDeviceStrokeThickness = max(
        -input.strokeThickness - 1.0,
        0.0);
    let hasLateAffineTransform = isStatic || useGpuTransforms;
    var directStrokeScales = vec2<f32>(1.0);
    if ((isAnalyticSdf || isDirect2DStroke || isLocalPolygonSdf) && hasLateAffineTransform) {
        directStrokeScales = direct_2d_stroke_scales(isStatic, useGpuTransforms);
    }
    let hasValidLateAffineTransform =
        directStrokeScales.x > 0.000001 &&
        directStrokeScales.y > 0.000001;
    let discardLateAffineShape =
        (isAnalyticSdf || isDirect2DStroke || isLocalPolygonSdf) &&
        hasLateAffineTransform &&
        !hasValidLateAffineTransform;
    let useLocalStrokeOutline =
        isDirect2DStroke &&
        !isHairlineStroke &&
        !isFixedDeviceStroke &&
        hasLateAffineTransform &&
        hasValidLateAffineTransform &&
        !is_conformal_stroke_transform(directStrokeScales);
    var outputStrokeThickness = select(
        input.strokeThickness,
        1.0,
        isHairlineStroke);
    outputStrokeThickness = select(
        outputStrokeThickness,
        fixedDeviceStrokeThickness,
        isFixedDeviceStroke);
    if (isDirect2DStroke &&
        !isHairlineStroke &&
        !isFixedDeviceStroke &&
        hasLateAffineTransform &&
        hasValidLateAffineTransform &&
        !useLocalStrokeOutline) {
        outputStrokeThickness = outputStrokeThickness * directStrokeScales.x;
    }
    let strokeExpansionPadding = select(
        1.5,
        1.5 / max(directStrokeScales.y, 0.000001),
        useLocalStrokeOutline);

    if (isAnalyticSdf &&
        (isHairlineStroke || isFixedDeviceStroke) &&
        hasLateAffineTransform &&
        hasValidLateAffineTransform) {
        // CPU recording reserves a two-local-unit halo for a device hairline
        // (half a pixel of stroke plus the 1.5-pixel AA fringe). A late
        // downscale needs a larger local quad so that the same two framebuffer
        // pixels remain covered after interpolation. The SDF itself derives
        // its exact one-pixel width from fragment derivatives below.
        let retainedPadding = select(
            2.0,
            fixedDeviceStrokeThickness * 0.5 + 1.5,
            isFixedDeviceStroke);
        let extraPadding = max(
            0.0,
            retainedPadding / directStrokeScales.y - retainedPadding);
        let quadrant = select(
            vec2<f32>(-1.0),
            vec2<f32>(1.0),
            input.texCoord >= vec2<f32>(0.0));
        inPos = inPos + quadrant * extraPadding;
        inTexCoord = inTexCoord + quadrant * extraPadding;
    }

    if (sType == 12u &&
        (isHairlineStroke || isFixedDeviceStroke) &&
        hasLateAffineTransform &&
        hasValidLateAffineTransform) {
        // Arc stroke quads are recorded with a two-local-unit halo. Enlarge
        // that halo before applying a late affine transform so its support in
        // every device-space direction remains at least two pixels:
        // (0.5 hairline width + 1.5 antialias fringe). Since every vector is
        // scaled by at least sigma_min, 2 / sigma_min is conservative under
        // anisotropic scale and shear.
        let retainedPadding = select(
            2.0,
            fixedDeviceStrokeThickness * 0.5 + 1.5,
            isFixedDeviceStroke);
        let extraPadding = max(
            0.0,
            retainedPadding / directStrokeScales.y - retainedPadding);
        let boundsCenter = arc_bounds_center(
            inColor.xy,
            inTexCoord,
            inShapeSize,
            inColor.z,
            inColor.w);
        let quadrant = select(
            vec2<f32>(-1.0),
            vec2<f32>(1.0),
            inPos >= boundsCenter);
        inPos = inPos + quadrant * extraPadding;
    }

    if (isLocalPolygonSdf && hasLateAffineTransform && hasValidLateAffineTransform) {
        // Polygon SDF payload stays in its pre-late-transform coordinate space;
        // interpolation then provides the inverse affine map in the fragment.
        // When Position and texCoord share that space (all quadrilateral stroke
        // bodies and stroke triangles), conservatively enlarge their retained
        // 1.5-unit raster pad for downscaled late transforms. Direct-fill
        // triangles with a previously baked command transform intentionally do
        // not satisfy this equality and retain their existing CPU-sized quad.
        let sharedPositionCoordinates =
            distance(input.position, input.texCoord) <= 0.0001;
        if (sharedPositionCoordinates) {
            let extraPadding = max(
                0.0,
                1.5 / directStrokeScales.y - 1.5);
            var payloadMin = min(input.color.xy, input.color.zw);
            var payloadMax = max(input.color.xy, input.color.zw);
            payloadMin = min(payloadMin, input.shapeSize);
            payloadMax = max(payloadMax, input.shapeSize);
            if (sType >= 14u) {
                let fourthPoint = vec2<f32>(
                    input.cornerRadius,
                    input.strokeThickness);
                payloadMin = min(payloadMin, fourthPoint);
                payloadMax = max(payloadMax, fourthPoint);
            }
            let payloadCenter = (payloadMin + payloadMax) * 0.5;
            let quadrant = select(
                vec2<f32>(-1.0),
                vec2<f32>(1.0),
                input.texCoord >= payloadCenter);
            inPos = inPos + quadrant * extraPadding;
            inTexCoord = inTexCoord + quadrant * extraPadding;
        }
    }

    if ((isStatic || useGpuTransforms) && sType != 8u) {
        if (useGpuTransforms) {
            if (!(useLocalStrokeOutline && isDirect2DStroke) &&
                !isHairlineAdornment) {
                inPos = (uniforms.view * vec4<f32>(inPos, 0.0, 1.0)).xy;
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
                }
            }
            if (sType < 3u || sType == 19u || sType == 20u) {
                let bIdx = u32(round(input.brushIndex));
                let brush = brushes[bIdx];
                if (brush.brushType > 0u) {
                    inColor = vec4<f32>((uniforms.view * vec4<f32>(input.color.xy, 0.0, 1.0)).xy, input.color.z, input.color.w);
                }
            }
        } else {
            if (!(useLocalStrokeOutline && isDirect2DStroke) &&
                !isHairlineAdornment) {
                inPos = (uniforms.mvp * vec4<f32>(inPos, 0.0, 1.0)).xy;
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
                }
            }
            if (sType < 3u || sType == 19u || sType == 20u) {
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
    var outputShapeType = sType;
    var discardGeneratedHairlineAdornment = false;

    if (sType == 22u) {
        // A cap descriptor stores a centerline endpoint and direction. Apply
        // any retained late transform to those values first, normalize in
        // framebuffer space, then expand one fixed quad around the exact
        // one-pixel cap. color.x is PenLineCap, color.y isStart, color.z is a
        // full-cap flag for degenerate paths, and shapeSize.x is vertex start.
        var center = inPos;
        var direction = inTexCoord;
        if (useGpuTransforms) {
            center = (uniforms.view * vec4<f32>(center, 0.0, 1.0)).xy;
            direction = (uniforms.view * vec4<f32>(direction, 0.0, 0.0)).xy;
        } else if (isStatic) {
            center = (uniforms.mvp * vec4<f32>(center, 0.0, 1.0)).xy;
            direction = (uniforms.mvp * vec4<f32>(direction, 0.0, 0.0)).xy;
        }
        direction = safe_normalize(direction);
        if (length(direction) <= 0.0001) {
            discardGeneratedHairlineAdornment = true;
        }

        let isStart = input.color.y > 0.5;
        let outward = select(direction, -direction, isStart);
        let normal = vec2<f32>(-direction.y, direction.x);
        let deviceStrokeThickness = select(
            1.0,
            fixedDeviceStrokeThickness,
            isFixedDeviceStroke);
        let capExtent = deviceStrokeThickness * 0.5 + 1.5;
        let localIndex = vertexIndex - u32(round(input.shapeSize.x));
        var capCoordinate = vec2<f32>(-capExtent, -capExtent);
        if (localIndex == 1u) {
            capCoordinate = vec2<f32>(capExtent, -capExtent);
        } else if (localIndex == 2u) {
            capCoordinate = vec2<f32>(capExtent, capExtent);
        } else if (localIndex == 3u) {
            capCoordinate = vec2<f32>(-capExtent, capExtent);
        }
        worldPos = center + outward * capCoordinate.x +
            normal * capCoordinate.y;
        texCoord = capCoordinate;
        inShapeSize = vec2<f32>(deviceStrokeThickness, 0.0);
        outputCornerRadius = input.color.x;
        outputStrokeThickness = input.color.z;
        outputShapeType = 22u;
        gridIndex = 0.0;
    } else if (sType == 23u) {
        // A join descriptor stores the center and its two centerline
        // directions. Resolve the outer side, transformed angle, and miter
        // limit in framebuffer space. One AABB quad then evaluates the bevel,
        // miter, or round exterior analytically in the fragment shader.
        var center = inPos;
        var incoming = inTexCoord;
        var outgoing = inShapeSize;
        if (useGpuTransforms) {
            center = (uniforms.view * vec4<f32>(center, 0.0, 1.0)).xy;
            incoming = (uniforms.view * vec4<f32>(incoming, 0.0, 0.0)).xy;
            outgoing = (uniforms.view * vec4<f32>(outgoing, 0.0, 0.0)).xy;
        } else if (isStatic) {
            center = (uniforms.mvp * vec4<f32>(center, 0.0, 1.0)).xy;
            incoming = (uniforms.mvp * vec4<f32>(incoming, 0.0, 0.0)).xy;
            outgoing = (uniforms.mvp * vec4<f32>(outgoing, 0.0, 0.0)).xy;
        }
        incoming = safe_normalize(incoming);
        outgoing = safe_normalize(outgoing);
        let turn = cross_2d(incoming, outgoing);
        if (length(incoming) <= 0.0001 ||
            length(outgoing) <= 0.0001 ||
            abs(turn) <= 0.0001) {
            discardGeneratedHairlineAdornment = true;
        }

        let outerSign = select(1.0, -1.0, turn > 0.0);
        let deviceStrokeThickness = select(
            1.0,
            fixedDeviceStrokeThickness,
            isFixedDeviceStroke);
        let halfStrokeThickness = deviceStrokeThickness * 0.5;
        let previousOuter =
            vec2<f32>(-incoming.y, incoming.x) * outerSign * halfStrokeThickness;
        let nextOuter =
            vec2<f32>(-outgoing.y, outgoing.x) * outerSign * halfStrokeThickness;
        let denominator = cross_2d(incoming, outgoing);
        var miterPoint = vec2<f32>(0.0);
        var hasMiter = false;
        if (abs(denominator) > 0.0001) {
            let intersectionDistance =
                cross_2d(nextOuter - previousOuter, outgoing) /
                denominator;
            let candidate = previousOuter + incoming * intersectionDistance;
            let miterLimit = max(input.color.y, 1.0);
            if (length(candidate) <= halfStrokeThickness * miterLimit + 0.0001) {
                miterPoint = candidate;
                hasMiter = true;
            }
        }

        let joinKind = u32(round(input.color.x));
        var boundsMin = min(vec2<f32>(0.0), min(previousOuter, nextOuter));
        var boundsMax = max(vec2<f32>(0.0), max(previousOuter, nextOuter));
        if (joinKind == 2u) {
            boundsMin = vec2<f32>(-halfStrokeThickness);
            boundsMax = vec2<f32>(halfStrokeThickness);
        } else if (joinKind == 0u && hasMiter) {
            boundsMin = min(boundsMin, miterPoint);
            boundsMax = max(boundsMax, miterPoint);
        }
        boundsMin = boundsMin - vec2<f32>(1.5);
        boundsMax = boundsMax + vec2<f32>(1.5);

        let localIndex = vertexIndex - u32(round(input.color.z));
        var joinCoordinate = boundsMin;
        if (localIndex == 1u) {
            joinCoordinate = vec2<f32>(boundsMax.x, boundsMin.y);
        } else if (localIndex == 2u) {
            joinCoordinate = boundsMax;
        } else if (localIndex == 3u) {
            joinCoordinate = vec2<f32>(boundsMin.x, boundsMax.y);
        }
        worldPos = center + joinCoordinate;
        texCoord = joinCoordinate;
        inColor = vec4<f32>(previousOuter, nextOuter);
        inShapeSize = miterPoint;
        outputCornerRadius = input.color.x;
        outputStrokeThickness = select(0.0, 1.0, hasMiter);
        outputShapeType = 23u;
        gridIndex = select(-1.0, 1.0, turn > 0.0);
    } else if (sType == 19u || sType == 20u) {
        // Algorithm: expand a retained point center by the encoded corner offset
        // after static/GPU transforms so a zero-width Skia hairline stays one
        // device pixel. Time and local-space complexity are O(1) per vertex.
        let hairlineCenter = select(
            inPos,
            floor(inPos) + vec2<f32>(0.5),
            aliasedEdge);
        worldPos = hairlineCenter + inTexCoord;
        texCoord = inTexCoord;
        outputShapeType = select(0u, 1u, sType == 20u);
    } else if (sType == 3u) {
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
        let halfThickness = outputStrokeThickness * 0.5;
        let expandedDistance = halfThickness * miterScale + strokeExpansionPadding;
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
        let halfThickness = outputStrokeThickness * 0.5;
        let expandedDistance = halfThickness + strokeExpansionPadding;
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
        let halfThickness = outputStrokeThickness * 0.5;
        let expandedDistance = halfThickness + strokeExpansionPadding;
        let offset = normal * expandedDistance * signVal;
        worldPos = pos + offset;
        texCoord = pos;
        gridIndex = signVal * expandedDistance;
    } else if (sType == 12u) {
        // Fixed-quad native arc stroke. The fragment shader evaluates the
        // transformed ellipse for conformal transforms and the original local
        // ellipse for non-conformal transforms, so the CPU does not tessellate
        // valid arcs into a line strip.
        worldPos = inPos;
        if (useLocalStrokeOutline) {
            // The retained quad includes two local units of padding. Derive the
            // tight partial-arc bounds from four fixed analytic extrema before
            // extending its corners, rather than comparing against the ellipse
            // center (a partial arc can lie wholly within one quadrant).
            let extraPadding = max(0.0, strokeExpansionPadding - 2.0);
            let boundsCenter = arc_bounds_center(
                inColor.xy,
                inTexCoord,
                inShapeSize,
                inColor.z,
                inColor.w);
            let quadrant = select(
                vec2<f32>(-1.0),
                vec2<f32>(1.0),
                worldPos >= boundsCenter);
            worldPos = worldPos + quadrant * extraPadding;
        }
        texCoord = worldPos;
        outputCornerRadius = inTexCoord.x;
        gridIndex = inTexCoord.y;
    }

    var brushCoord = texCoord;
    if (isHairlineAdornment || sType == 24u) {
        brushCoord = worldPos;
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
        if (useLocalStrokeOutline) {
            if (useGpuTransforms) {
                pos = (uniforms.view * vec4<f32>(worldPos, 0.0, 1.0)).xy;
                brushCoord = (uniforms.view * vec4<f32>(texCoord, 0.0, 1.0)).xy;
            } else {
                pos = (uniforms.mvp * vec4<f32>(worldPos, 0.0, 1.0)).xy;
                brushCoord = (uniforms.mvp * vec4<f32>(texCoord, 0.0, 1.0)).xy;
            }
        }
        output.position = uniforms.projection * vec4<f32>(pos, 0.0, 1.0);
    }
    if (discardLateAffineShape || discardGeneratedHairlineAdornment) {
        // Collapse invalid/singular late-transformed primitives outside clip
        // space. The fragment sentinel below is a second fail-closed guard.
        output.position = vec4<f32>(2.0, 2.0, 0.0, 1.0);
    }
    output.color = inColor;
    output.texCoord = texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = inShapeSize;
    output.cornerRadius = outputCornerRadius;
    output.strokeThickness = outputStrokeThickness;
    output.shapeType = select(
        f32(outputShapeType),
        f32(outputShapeType) + 1000.0,
        aliasedEdge);
    output.gridIndex = gridIndex;
    output.localStrokeMode = select(
        select(
            select(
                select(0.0, 1.0, useLocalStrokeOutline),
                3.0,
                isFixedDeviceStroke),
            2.0,
            isHairlineStroke),
        -1.0,
        discardLateAffineShape || discardGeneratedHairlineAdornment);
    output.brushCoord = brushCoord;
    return output;
}

// Solid UI rectangles use the same retained VectorVertex ABI and transform flags as
// the general vector path. The specialized entry point intentionally does not read
// brush storage or evaluate unrelated primitive branches. The compositor bakes the
// solid brush and effective opacity into input.color before selecting this pipeline.
@vertex
fn vs_solid_rect(input: VertexInput) -> VertexOutput {
    var encodedShapeType = input.shapeType;
    let aliasedEdge = encodedShapeType >= 1000.0;
    if (aliasedEdge) {
        encodedShapeType = encodedShapeType - 1000.0;
    }

    var position = input.position;
    if (encodedShapeType >= 195.0) {
        position = (uniforms.mvp * vec4<f32>(position, 0.0, 1.0)).xy;
    } else if (encodedShapeType >= 95.0) {
        position = (uniforms.view * vec4<f32>(position, 0.0, 1.0)).xy;
    }

    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = input.shapeSize;
    output.cornerRadius = 0.0;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = select(0.0, 1000.0, aliasedEdge);
    output.gridIndex = 0.0;
    output.localStrokeMode = 0.0;
    output.brushCoord = input.texCoord;
    return output;
}

fn solid_rect_distance(coordinate: vec2<f32>, shapeSize: vec2<f32>) -> f32 {
    let distanceVector = abs(coordinate) - shapeSize * 0.5;
    return length(max(distanceVector, vec2<f32>(0.0))) +
        min(max(distanceVector.x, distanceVector.y), 0.0);
}

fn solid_rect_fs_main(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let aliasedEdge = input.shapeType >= 1000.0;
    // Fragment derivatives must execute in uniform control flow. Compute both
    // fill and stroke widths before selecting the interpolated primitive mode;
    // the branch below then only combines already evaluated scalar coverage.
    let edgeDistance = abs(input.texCoord) - input.shapeSize * 0.5;
    let edgeWidth = max(
        abs(dpdx(input.texCoord)) + abs(dpdy(input.texCoord)),
        vec2<f32>(0.0001));
    let distance = solid_rect_distance(input.texCoord, input.shapeSize);
    let thinStrokeInset = select(
        0.0,
        0.0625,
        !aliasedEdge && input.strokeThickness <= 1.0001);
    let strokeDistance = abs(distance) -
        max(input.strokeThickness * 0.5 - thinStrokeInset, 0.0);
    // dFdx/dFdy evaluate the exact analytic rectangle distance in screen
    // space, avoiding the general shader's four extra SDF evaluations.
    let strokeFilterWidth = max(
        abs(dpdx(strokeDistance)) + abs(dpdy(strokeDistance)),
        0.0001);

    var shapeAlpha = 1.0;
    if (input.strokeThickness <= 0.0) {
        // Match the general fill-rectangle path exactly: separable derivative AA
        // keeps axis-aligned one-pixel edges crisp and remains affine-safe.
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
    } else {
        let antialiasedAlpha = 1.0 - smoothstep(
            -0.5 * strokeFilterWidth,
            0.5 * strokeFilterWidth,
            strokeDistance);
        let aliasedAlpha = select(0.0, 1.0, strokeDistance <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    }

    if (shapeAlpha <= 0.0 || maskAlpha <= 0.0) {
        discard;
    }
    return vec4<f32>(input.color.rgb, input.color.a * shapeAlpha * maskAlpha);
}

@fragment
fn fs_solid_rect_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return solid_rect_fs_main(input, sample_mask_alpha(input.position.xy));
}

@fragment
fn fs_solid_rect_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return solid_rect_fs_main(input, 1.0);
}

@fragment
fn fs_solid_rect_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rect_fs_main(input, sample_mask_alpha(input.position.xy));
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_solid_rect_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rect_fs_main(input, 1.0);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_solid_rect_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rect_fs_main(input, sample_mask_alpha(input.position.xy));
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

@fragment
fn fs_solid_rect_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rect_fs_main(input, 1.0);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

// Circular rounded rectangles use a separate bounded specialization only when
// the compositor has observed enough of them to amortize a pipeline boundary.
// The retained vertex ABI, transforms, coverage, opacity, and blend behavior
// remain identical to the general vector path.
@vertex
fn vs_solid_rounded(input: VertexInput) -> VertexOutput {
    var encodedShapeType = input.shapeType;
    let aliasedEdge = encodedShapeType >= 1000.0;
    if (aliasedEdge) {
        encodedShapeType = encodedShapeType - 1000.0;
    }

    var position = input.position;
    if (encodedShapeType >= 195.0) {
        position = (uniforms.mvp * vec4<f32>(position, 0.0, 1.0)).xy;
    } else if (encodedShapeType >= 95.0) {
        position = (uniforms.view * vec4<f32>(position, 0.0, 1.0)).xy;
    }

    var output: VertexOutput;
    output.position = uniforms.projection * vec4<f32>(position, 0.0, 1.0);
    output.color = input.color;
    output.texCoord = input.texCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = input.shapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = select(2.0, 1002.0, aliasedEdge);
    output.gridIndex = 0.0;
    output.localStrokeMode = 0.0;
    output.brushCoord = input.texCoord;
    return output;
}

fn solid_rounded_fs_main(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let atlasCoordDx = dpdx(input.texCoord);
    let atlasCoordDy = dpdy(input.texCoord);
    let aliasedEdge = input.shapeType >= 1000.0;
    let distanceGradient = box_distance_gradient(
        input.texCoord,
        input.shapeSize * 0.5,
        input.cornerRadius);
    let distance = distanceGradient.x;
    let gradient = distanceGradient.yz;
    let thinStrokeInset = select(
        0.0,
        0.0625,
        !aliasedEdge && input.strokeThickness <= 1.0001);
    let strokeDistance = abs(distance) -
        max(input.strokeThickness * 0.5 - thinStrokeInset, 0.0);
    let shapeDistance = select(
        distance,
        strokeDistance,
        input.strokeThickness > 0.0);
    let filterWidth = max(
        abs(dot(gradient, atlasCoordDx)) + abs(dot(gradient, atlasCoordDy)),
        0.0001);
    let antialiasedAlpha = 1.0 - smoothstep(
        -0.5 * filterWidth,
        0.5 * filterWidth,
        shapeDistance);
    let aliasedAlpha = select(0.0, 1.0, shapeDistance <= 0.0);
    let shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    if (shapeAlpha <= 0.0 || maskAlpha <= 0.0) {
        discard;
    }
    return vec4<f32>(input.color.rgb, input.color.a * shapeAlpha * maskAlpha);
}

@fragment
fn fs_solid_rounded_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return solid_rounded_fs_main(input, sample_mask_alpha(input.position.xy));
}

@fragment
fn fs_solid_rounded_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return solid_rounded_fs_main(input, 1.0);
}

@fragment
fn fs_solid_rounded_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rounded_fs_main(input, sample_mask_alpha(input.position.xy));
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_solid_rounded_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rounded_fs_main(input, 1.0);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_solid_rounded_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rounded_fs_main(input, sample_mask_alpha(input.position.xy));
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

@fragment
fn fs_solid_rounded_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = solid_rounded_fs_main(input, 1.0);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
fn mesh_unpremultiply(color: vec4<f32>) -> vec4<f32> {
    if (color.a <= 0.0) {
        return vec4<f32>(0.0);
    }
    return vec4<f32>(color.rgb / color.a, color.a);
}

fn mesh_screen(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return backdrop + source - backdrop * source;
}

fn mesh_hard_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop * (2.0 * source);
    }
    return backdrop + (2.0 * source - 1.0) - backdrop * (2.0 * source - 1.0);
}

fn mesh_hard_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        mesh_hard_light_component(backdrop.r, source.r),
        mesh_hard_light_component(backdrop.g, source.g),
        mesh_hard_light_component(backdrop.b, source.b));
}

fn mesh_color_dodge_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop <= 0.0) { return 0.0; }
    if (source >= 1.0) { return 1.0; }
    return min(1.0, backdrop / (1.0 - source));
}

fn mesh_color_dodge(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        mesh_color_dodge_component(backdrop.r, source.r),
        mesh_color_dodge_component(backdrop.g, source.g),
        mesh_color_dodge_component(backdrop.b, source.b));
}

fn mesh_color_burn_component(backdrop: f32, source: f32) -> f32 {
    if (backdrop >= 1.0) { return 1.0; }
    if (source <= 0.0) { return 0.0; }
    return 1.0 - min(1.0, (1.0 - backdrop) / source);
}

fn mesh_color_burn(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        mesh_color_burn_component(backdrop.r, source.r),
        mesh_color_burn_component(backdrop.g, source.g),
        mesh_color_burn_component(backdrop.b, source.b));
}

fn mesh_soft_light_component(backdrop: f32, source: f32) -> f32 {
    if (source <= 0.5) {
        return backdrop - (1.0 - 2.0 * source) * backdrop * (1.0 - backdrop);
    }
    var curve = sqrt(backdrop);
    if (backdrop <= 0.25) {
        curve = ((16.0 * backdrop - 12.0) * backdrop + 4.0) * backdrop;
    }
    return backdrop + (2.0 * source - 1.0) * (curve - backdrop);
}

fn mesh_soft_light(backdrop: vec3<f32>, source: vec3<f32>) -> vec3<f32> {
    return vec3<f32>(
        mesh_soft_light_component(backdrop.r, source.r),
        mesh_soft_light_component(backdrop.g, source.g),
        mesh_soft_light_component(backdrop.b, source.b));
}

fn mesh_luminosity(color: vec3<f32>) -> f32 {
    return dot(color, vec3<f32>(0.3, 0.59, 0.11));
}

fn mesh_saturation(color: vec3<f32>) -> f32 {
    return max(max(color.r, color.g), color.b) - min(min(color.r, color.g), color.b);
}

fn mesh_clip_color(input: vec3<f32>) -> vec3<f32> {
    var color = input;
    let lightness = mesh_luminosity(color);
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (minimum < 0.0 && lightness > minimum) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * lightness / (lightness - minimum);
    }
    if (maximum > 1.0 && maximum > lightness) {
        color = vec3<f32>(lightness) +
            (color - vec3<f32>(lightness)) * (1.0 - lightness) / (maximum - lightness);
    }
    return color;
}

fn mesh_set_luminosity(color: vec3<f32>, lightness: f32) -> vec3<f32> {
    return mesh_clip_color(color + vec3<f32>(lightness - mesh_luminosity(color)));
}

fn mesh_set_saturation(color: vec3<f32>, targetSaturation: f32) -> vec3<f32> {
    let minimum = min(min(color.r, color.g), color.b);
    let maximum = max(max(color.r, color.g), color.b);
    if (maximum <= minimum) {
        return vec3<f32>(0.0);
    }
    return (color - vec3<f32>(minimum)) * targetSaturation / (maximum - minimum);
}

fn mesh_advanced_blend(backdrop: vec3<f32>, source: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 14u: { return mesh_screen(backdrop, source); }
        case 15u: { return mesh_hard_light(source, backdrop); }
        case 16u: { return min(backdrop, source); }
        case 17u: { return max(backdrop, source); }
        case 18u: { return mesh_color_dodge(backdrop, source); }
        case 19u: { return mesh_color_burn(backdrop, source); }
        case 20u: { return mesh_hard_light(backdrop, source); }
        case 21u: { return mesh_soft_light(backdrop, source); }
        case 22u: { return abs(backdrop - source); }
        case 23u: { return backdrop + source - 2.0 * backdrop * source; }
        case 24u: { return backdrop * source; }
        case 25u: {
            return mesh_set_luminosity(
                mesh_set_saturation(source, mesh_saturation(backdrop)),
                mesh_luminosity(backdrop));
        }
        case 26u: {
            return mesh_set_luminosity(
                mesh_set_saturation(backdrop, mesh_saturation(source)),
                mesh_luminosity(backdrop));
        }
        case 27u: { return mesh_set_luminosity(source, mesh_luminosity(backdrop)); }
        case 28u: { return mesh_set_luminosity(backdrop, mesh_luminosity(source)); }
        default: { return source; }
    }
}

fn blend_mesh_colors(source: vec4<f32>, destinationPremultiplied: vec4<f32>, mode: u32) -> vec4<f32> {
    let sourcePremultiplied = vec4<f32>(source.rgb * source.a, source.a);
    let destination = mesh_unpremultiply(destinationPremultiplied);
    var result = vec4<f32>(0.0);
    switch mode {
        case 0u: { result = vec4<f32>(0.0); }
        case 1u: { result = sourcePremultiplied; }
        case 2u: { result = destinationPremultiplied; }
        case 3u: { result = sourcePremultiplied + destinationPremultiplied * (1.0 - source.a); }
        case 4u: { result = destinationPremultiplied + sourcePremultiplied * (1.0 - destination.a); }
        case 5u: { result = sourcePremultiplied * destination.a; }
        case 6u: { result = destinationPremultiplied * source.a; }
        case 7u: { result = sourcePremultiplied * (1.0 - destination.a); }
        case 8u: { result = destinationPremultiplied * (1.0 - source.a); }
        case 9u: {
            result = sourcePremultiplied * destination.a +
                destinationPremultiplied * (1.0 - source.a);
        }
        case 10u: {
            result = destinationPremultiplied * source.a +
                sourcePremultiplied * (1.0 - destination.a);
        }
        case 11u: {
            result = sourcePremultiplied * (1.0 - destination.a) +
                destinationPremultiplied * (1.0 - source.a);
        }
        case 12u: { result = min(sourcePremultiplied + destinationPremultiplied, vec4<f32>(1.0)); }
        case 13u: { result = sourcePremultiplied * destinationPremultiplied; }
        default: {
            let mixed = clamp(
                mesh_advanced_blend(destination.rgb, source.rgb, mode),
                vec3<f32>(0.0),
                vec3<f32>(1.0));
            result = vec4<f32>(
                sourcePremultiplied.rgb * (1.0 - destination.a) +
                    destinationPremultiplied.rgb * (1.0 - source.a) +
                    mixed * source.a * destination.a,
                source.a + destination.a - source.a * destination.a);
        }
    }
    return mesh_unpremultiply(clamp(result, vec4<f32>(0.0), vec4<f32>(1.0)));
}

fn analytic_shape_distance(
    shapeType: u32,
    coordinate: vec2<f32>,
    shapeSize: vec2<f32>,
    cornerRadius: f32) -> f32 {
    if (shapeType == 0u) {
        let distanceVector = abs(coordinate) - shapeSize * 0.5;
        return length(max(distanceVector, vec2<f32>(0.0))) +
            min(max(distanceVector.x, distanceVector.y), 0.0);
    }
    if (shapeType == 1u) {
        let rx = shapeSize.x * 0.5;
        let ry = shapeSize.y * 0.5;
        if (rx <= 0.0001 || ry <= 0.0001) {
            return -1.0;
        }
        if (abs(rx - ry) <= 0.0001) {
            return length(coordinate) - rx;
        }
        let value = (coordinate.x * coordinate.x) / (rx * rx) +
            (coordinate.y * coordinate.y) / (ry * ry);
        let gradient = vec2<f32>(
            (2.0 * coordinate.x) / (rx * rx),
            (2.0 * coordinate.y) / (ry * ry));
        return (value - 1.0) / max(length(gradient), 0.0001);
    }
    if (shapeType == 2u) {
        let distanceVector = abs(coordinate) -
            (shapeSize * 0.5 - vec2<f32>(cornerRadius));
        return length(max(distanceVector, vec2<f32>(0.0))) +
            min(max(distanceVector.x, distanceVector.y), 0.0) - cornerRadius;
    }
    return -1.0;
}

// Returns the exact signed distance and its local-space gradient for an
// axis-aligned box with an optional circular corner radius. The gradient is
// analytic everywhere except the SDF's intentional medial-axis ties, where a
// stable single-axis subgradient is selected. This replaces four extra SDF
// evaluations for rectangle strokes and circular rounded rectangles.
fn box_distance_gradient(
    coordinate: vec2<f32>,
    halfSize: vec2<f32>,
    cornerRadius: f32) -> vec3<f32> {
    let q = abs(coordinate) - (halfSize - vec2<f32>(cornerRadius));
    let outside = max(q, vec2<f32>(0.0));
    let outsideLength = length(outside);
    let distance = outsideLength + min(max(q.x, q.y), 0.0) - cornerRadius;
    let coordinateSign = select(
        vec2<f32>(-1.0),
        vec2<f32>(1.0),
        coordinate >= vec2<f32>(0.0));
    var gradient: vec2<f32>;
    if (outsideLength > 0.000001) {
        gradient = (outside / outsideLength) * coordinateSign;
    } else {
        gradient = select(
            vec2<f32>(0.0, coordinateSign.y),
            vec2<f32>(coordinateSign.x, 0.0),
            q.x >= q.y);
    }
    return vec3<f32>(distance, gradient);
}

fn vector_fs_main(input: VertexOutput, maskAlpha: f32) -> vec4<f32> {
    let atlasCoordDx = dpdx(input.texCoord);
    let atlasCoordDy = dpdy(input.texCoord);
    let strokeDistanceDx = dpdx(input.gridIndex);
    let strokeDistanceDy = dpdy(input.gridIndex);
    var encodedShapeType = input.shapeType;
    let aliasedEdge = encodedShapeType >= 1000.0;
    if (aliasedEdge) {
        encodedShapeType = encodedShapeType - 1000.0;
    }

    let sType = u32(round(encodedShapeType));

    if (input.localStrokeMode < -0.5) {
        discard;
    }

    var evalCoord = input.brushCoord;
    if (sType < 3u) {
        evalCoord = input.color.xy + input.texCoord;
    } else if (sType == 4u) {
        evalCoord = input.shapeSize;
    }

    var shapeAlpha: f32 = 1.0;
    if (sType == 0u && input.strokeThickness <= 0.0) {
        let edgeDistance = abs(input.texCoord) - input.shapeSize * 0.5;
        let edgeWidth = max(
            abs(atlasCoordDx) + abs(atlasCoordDy),
            vec2<f32>(0.0001));
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
        var d: f32;
        var gradient: vec2<f32>;
        if (sType == 0u || sType == 2u) {
            let radius = select(0.0, input.cornerRadius, sType == 2u);
            let distanceGradient = box_distance_gradient(
                input.texCoord,
                input.shapeSize * 0.5,
                radius);
            d = distanceGradient.x;
            gradient = distanceGradient.yz;
        } else {
            d = analytic_shape_distance(
                sType,
                input.texCoord,
                input.shapeSize,
                input.cornerRadius);
            // Elliptical distance uses the established finite-difference
            // gradient because its first-order distance approximation is not
            // the exact ellipse SDF.
            let gradientStep = 0.01;
            gradient = vec2<f32>(
                analytic_shape_distance(
                    sType,
                    input.texCoord + vec2<f32>(gradientStep, 0.0),
                    input.shapeSize,
                    input.cornerRadius) -
                    analytic_shape_distance(
                        sType,
                        input.texCoord - vec2<f32>(gradientStep, 0.0),
                        input.shapeSize,
                        input.cornerRadius),
                analytic_shape_distance(
                    sType,
                    input.texCoord + vec2<f32>(0.0, gradientStep),
                    input.shapeSize,
                    input.cornerRadius) -
                    analytic_shape_distance(
                        sType,
                        input.texCoord - vec2<f32>(0.0, gradientStep),
                        input.shapeSize,
                        input.cornerRadius)) / (2.0 * gradientStep);
        }
        // Transform the local SDF gradient with derivatives obtained before
        // non-uniform branching. Device hairlines use exactly one framebuffer
        // pixel of width expressed in these local-distance units.
        let fw = max(
            abs(dot(gradient, atlasCoordDx)) + abs(dot(gradient, atlasCoordDy)),
            0.0001);
        let isHairline =
            input.localStrokeMode > 1.5 &&
            input.localStrokeMode < 2.5;
        var d_shape: f32 = 0.0;
        if (input.strokeThickness > 0.0) {
            var strokeDistance = d;
            if (aliasedEdge && sType == 1u) {
                strokeDistance = strokeDistance + 0.08;
            }
            let thinStrokeInset = select(
                0.0,
                0.0625 * select(
                    1.0,
                    fw,
                    input.localStrokeMode > 2.5),
                !aliasedEdge && input.strokeThickness <= 1.0001);
            let ordinaryHalfWidth = max(
                input.strokeThickness *
                    select(1.0, fw, input.localStrokeMode > 2.5) *
                    0.5 - thinStrokeInset,
                0.0);
            let hairlineHalfWidth = max(
                fw * select(0.5, 0.4375, !aliasedEdge),
                0.0);
            d_shape = abs(strokeDistance) - select(
                ordinaryHalfWidth,
                hairlineHalfWidth,
                isHairline);
        } else {
            d_shape = d;
        }
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 12u) {
        // Fixed-quad elliptical arc stroke SDF. Under non-conformal transforms
        // every value remains in command-local coordinates and only the quad
        // positions are transformed. The distance gradient and interpolated
        // local-coordinate derivatives convert AA back to framebuffer pixels.
        // Conformal transforms retain the equivalent screen-space fast path.
        // color.xy carries the center, color.zw theta1/deltaTheta,
        // cornerRadius/gridIndex axisX, shapeSize axisY, and texCoord the point.
        let center = input.color.xy;
        let theta1 = input.color.z;
        let deltaTheta = input.color.w;
        let axisX = vec2<f32>(input.cornerRadius, input.gridIndex);
        let axisY = input.shapeSize;
        let point = input.texCoord;
        let theta = nearest_ellipse_theta(point, center, axisX, axisY);
        let nearest = arc_eval(center, axisX, axisY, theta);
        var distanceGradient = safe_normalize(point - nearest);
        let initialDerivativeWidth = max(
            abs(dot(distanceGradient, atlasCoordDx)) +
                abs(dot(distanceGradient, atlasCoordDy)),
            0.0001);
        let initialFilterWidth = select(
            1.0,
            initialDerivativeWidth,
            input.localStrokeMode > 0.5);
        var effectiveStrokeThickness = select(
            input.strokeThickness,
            initialFilterWidth,
            input.localStrokeMode > 1.5);
        effectiveStrokeThickness = select(
            effectiveStrokeThickness,
            input.strokeThickness * initialFilterWidth,
            input.localStrokeMode > 2.5);
        let deviceStrokeThickness =
            effectiveStrokeThickness / initialFilterWidth;
        let thinStrokeInset = select(
            0.0,
            0.0625 * initialFilterWidth,
            !aliasedEdge && deviceStrokeThickness <= 1.0001);
        let effectiveHalfWidth = max(
            effectiveStrokeThickness * 0.5 - thinStrokeInset,
            0.0);
        var d_shape = length(point - nearest) - effectiveHalfWidth;
        let radiusX = length(axisX);
        let radiusY = length(axisY);
        if (abs(radiusX - radiusY) <= 0.0001) {
            let signedDistance = length(point - center) - radiusX;
            let radialGradient = safe_normalize(point - center);
            if (aliasedEdge) {
                d_shape = abs(signedDistance + 0.08) -
                    effectiveStrokeThickness * 0.5;
                distanceGradient = radialGradient *
                    select(-1.0, 1.0, signedDistance + 0.08 >= 0.0);
            } else {
                d_shape = abs(signedDistance) - effectiveHalfWidth;
                distanceGradient = radialGradient *
                    select(-1.0, 1.0, signedDistance >= 0.0);
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
            let capGradient = select(
                -startTangent,
                endTangent,
                endCap >= startCap);
            let insideSweep = is_angle_inside_arc(theta, theta1, deltaTheta);
            if (!insideSweep && capDistance > d_shape) {
                d_shape = capDistance;
                distanceGradient = capGradient;
            }
        }

        let derivativeWidth = max(
            abs(dot(distanceGradient, atlasCoordDx)) +
                abs(dot(distanceGradient, atlasCoordDy)),
            0.0001);
        let fw = select(
            1.0,
            derivativeWidth,
            input.localStrokeMode > 0.5);
        let antialiasedAlpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, d_shape);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 13u) {
        // Antialiased stroke join/cap triangle. color.xy/color.zw/shapeSize
        // carry its three pre-late-transform points and texCoord carries the
        // point in that same coordinate space.
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
        let gradient0 = orientation *
            vec2<f32>(edge0.y, -edge0.x) / max(length(edge0), 0.0001);
        let gradient1 = orientation *
            vec2<f32>(edge1.y, -edge1.x) / max(length(edge1), 0.0001);
        let gradient2 = orientation *
            vec2<f32>(edge2.y, -edge2.x) / max(length(edge2), 0.0001);
        let width0 = max(
            abs(dot(gradient0, atlasCoordDx)) +
                abs(dot(gradient0, atlasCoordDy)),
            0.0001);
        let width1 = max(
            abs(dot(gradient1, atlasCoordDx)) +
                abs(dot(gradient1, atlasCoordDy)),
            0.0001);
        let width2 = max(
            abs(dot(gradient2, atlasCoordDx)) +
                abs(dot(gradient2, atlasCoordDy)),
            0.0001);
        let allDistance = max(distance0, max(distance1, distance2));
        let edgeMask = u32(round(input.cornerRadius));
        let ownedInternalEdgeMask = u32(round(input.strokeThickness));
        let internalEdgeTolerance = 0.001;
        let internalInside =
            select(
                select(distance0 < -internalEdgeTolerance, distance0 <= internalEdgeTolerance, (ownedInternalEdgeMask & 1u) != 0u),
                true,
                (edgeMask & 1u) != 0u) &&
            select(
                select(distance1 < -internalEdgeTolerance, distance1 <= internalEdgeTolerance, (ownedInternalEdgeMask & 2u) != 0u),
                true,
                (edgeMask & 2u) != 0u) &&
            select(
                select(distance2 < -internalEdgeTolerance, distance2 <= internalEdgeTolerance, (ownedInternalEdgeMask & 4u) != 0u),
                true,
                (edgeMask & 4u) != 0u);
        var exteriorDistance = -1000000.0;
        var fw = 1.0;
        if ((edgeMask & 1u) != 0u && distance0 > exteriorDistance) {
            exteriorDistance = distance0;
            fw = width0;
        }
        if ((edgeMask & 2u) != 0u && distance1 > exteriorDistance) {
            exteriorDistance = distance1;
            fw = width1;
        }
        if ((edgeMask & 4u) != 0u && distance2 > exteriorDistance) {
            exteriorDistance = distance2;
            fw = width2;
        }
        let antialiasedAlpha = select(
            0.0,
            1.0 - smoothstep(-0.5 * fw, 0.5 * fw, exteriorDistance),
            internalInside);
        let aliasedAlpha = select(0.0, 1.0, allDistance <= 0.0 && internalInside);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType >= 14u && sType <= 17u) {
        // Antialiased affine stroke segment. color.xy/color.zw/shapeSize and
        // cornerRadius/strokeThickness carry its four pre-late-transform
        // corners; texCoord remains in the same space.
        // Types 15-17 keep shared curve cross-sections hard-owned so adjacent
        // sections do not introduce antialiased seams.
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
        let gradient0 = orientation *
            vec2<f32>(edge0.y, -edge0.x) / max(length(edge0), 0.0001);
        let gradient1 = orientation *
            vec2<f32>(edge1.y, -edge1.x) / max(length(edge1), 0.0001);
        let gradient2 = orientation *
            vec2<f32>(edge2.y, -edge2.x) / max(length(edge2), 0.0001);
        let gradient3 = orientation *
            vec2<f32>(edge3.y, -edge3.x) / max(length(edge3), 0.0001);
        let width0 = max(
            abs(dot(gradient0, atlasCoordDx)) +
                abs(dot(gradient0, atlasCoordDy)),
            0.0001);
        let width1 = max(
            abs(dot(gradient1, atlasCoordDx)) +
                abs(dot(gradient1, atlasCoordDy)),
            0.0001);
        let width2 = max(
            abs(dot(gradient2, atlasCoordDx)) +
                abs(dot(gradient2, atlasCoordDy)),
            0.0001);
        let width3 = max(
            abs(dot(gradient3, atlasCoordDx)) +
                abs(dot(gradient3, atlasCoordDy)),
            0.0001);
        let allDistance = max(max(distance0, distance1), max(distance2, distance3));
        var exteriorEdgeMask = 15u;
        if (sType == 15u) {
            exteriorEdgeMask = 5u;
        } else if (sType == 16u) {
            exteriorEdgeMask = 13u;
        } else if (sType == 17u) {
            exteriorEdgeMask = 7u;
        }
        let internalInside =
            ((exteriorEdgeMask & 1u) != 0u || distance0 <= 0.0) &&
            ((exteriorEdgeMask & 2u) != 0u || distance1 <= 0.0) &&
            ((exteriorEdgeMask & 4u) != 0u || distance2 <= 0.0) &&
            ((exteriorEdgeMask & 8u) != 0u || distance3 <= 0.0);
        var exteriorDistance = -1000000.0;
        var fw = 1.0;
        if ((exteriorEdgeMask & 1u) != 0u && distance0 > exteriorDistance) {
            exteriorDistance = distance0;
            fw = width0;
        }
        if ((exteriorEdgeMask & 2u) != 0u && distance1 > exteriorDistance) {
            exteriorDistance = distance1;
            fw = width1;
        }
        if ((exteriorEdgeMask & 4u) != 0u && distance2 > exteriorDistance) {
            exteriorDistance = distance2;
            fw = width2;
        }
        if ((exteriorEdgeMask & 8u) != 0u && distance3 > exteriorDistance) {
            exteriorDistance = distance3;
            fw = width3;
        }
        let antialiasedAlpha = select(
            0.0,
            1.0 - smoothstep(-0.5 * fw, 0.5 * fw, exteriorDistance),
            internalInside);
        let aliasedAlpha = select(0.0, 1.0, allDistance <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 3u || sType == 5u || sType == 6u) {
        // Conformal strokes use signed pixel distance. Non-conformal strokes
        // interpolate signed local distance across the transformed outline and
        // use derivatives to express the AA ramp in framebuffer pixels.
        let derivativeWidth = max(
            abs(strokeDistanceDx) + abs(strokeDistanceDy),
            0.0001);
        let filterWidth = select(
            1.0,
            derivativeWidth,
            input.localStrokeMode > 0.5);
        let deviceStrokeThickness = input.strokeThickness / filterWidth;
        let d_stroke = abs(input.gridIndex);
        let thinStrokeInset = select(
            0.0,
            0.0625 * filterWidth,
            !aliasedEdge && deviceStrokeThickness <= 1.0001);
        let d_shape = d_stroke -
            max(input.strokeThickness * 0.5 - thinStrokeInset, 0.0);
        let antialiasHalfWidth = 0.5 * filterWidth;
        let linearAntialiasedAlpha = 1.0 - smoothstep(
            -antialiasHalfWidth,
            antialiasHalfWidth,
            d_shape);
        let antialiasedAlpha = pow(linearAntialiasedAlpha, 0.7);
        let aliasedAlpha = select(0.0, 1.0, d_shape <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 4u) {
        // Path rendering: sample coverage directly from PathAtlas
        let pathAtlasDims = textureDimensions(pathAtlasTexture);
        let pathAtlasSize = vec2<f32>(f32(pathAtlasDims.x), f32(pathAtlasDims.y));
        let pathAtlasCoord = input.texCoord / pathAtlasSize;
        // atlasCoordDx/atlasCoordDy are evaluated at fragment-function entry,
        // before shape-type control flow. Division by the constant texture
        // dimensions is exactly the derivative of the normalized coordinate
        // and remains valid under WebGPU's uniformity analysis.
        let pathAtlasCoordDx = atlasCoordDx / pathAtlasSize;
        let pathAtlasCoordDy = atlasCoordDy / pathAtlasSize;
        let coverage = textureSampleGrad(pathAtlasTexture, pathAtlasSampler, pathAtlasCoord, pathAtlasCoordDx, pathAtlasCoordDy).r;
        let coverageGamma = select(1.0, input.cornerRadius, input.cornerRadius > 0.0);
        let correctedCoverage = pow(coverage, coverageGamma);
        shapeAlpha = select(correctedCoverage, select(0.0, 1.0, coverage >= 0.5), aliasedEdge);
    } else if (sType == 7u) {
        // Direct solid fill
        shapeAlpha = 1.0;
    } else if (sType == 21u) {
        // One quad covers the grid. Per-fragment work has fixed bounds and no
        // dependency on the number of visible dots.
        let spacing = max(input.shapeSize.x, 0.0001);
        let radius = max(input.shapeSize.y, 0.0001);
        let phase = vec2<f32>(input.cornerRadius, input.strokeThickness);
        let cellIndex = round((input.texCoord - phase) / spacing);
        let unsnappedCenter = cellIndex * spacing + phase;
        // Derivatives evaluated at fragment-function entry convert the
        // quarter-physical-pixel lattice into the command's local coordinate
        // system, including display scale. Reuse them here because shape type
        // is fragment-varying and WGSL derivatives require uniform control.
        let localUnitsPerPhysicalPixel = vec2<f32>(
            length(vec2<f32>(atlasCoordDx.x, atlasCoordDy.x)),
            length(vec2<f32>(atlasCoordDx.y, atlasCoordDy.y)));
        let quarterPhysicalStep = max(
            localUnitsPerPhysicalPixel * 0.25,
            vec2<f32>(0.0001));
        let snappedCenter = round(unsnappedCenter / quarterPhysicalStep) *
            quarterPhysicalStep;
        let dotOffset = input.texCoord - snappedCenter;
        let dotDistance = length(dotOffset) - radius;
        let dotGradient = dotOffset / max(length(dotOffset), 0.0001);
        let filterWidth = max(
            abs(dot(dotGradient, atlasCoordDx)) +
                abs(dot(dotGradient, atlasCoordDy)),
            0.0001);
        shapeAlpha = 1.0 -
            smoothstep(-0.5 * filterWidth, 0.5 * filterWidth, dotDistance);
    } else if (sType == 22u || sType == 24u) {
        // Analytic path cap. Shape 22 is expanded as a fixed-device adornment
        // in the vertex stage; shape 24 arrives as an already affine-expanded
        // positive-width quad. The cap/body interface is a hard-owned internal
        // seam; only the exterior contributes AA coverage. Work/storage are O(1).
        let point = input.texCoord;
        let capKind = u32(round(input.cornerRadius));
        let isFullCap = input.strokeThickness > 0.5;
        let halfStrokeThickness = input.shapeSize.x * 0.5;
        var exteriorDistance = 0.0;
        var exteriorGradient = vec2<f32>(1.0, 0.0);
        var internalInside = true;
        var allDistance = 0.0;
        if (capKind == 2u) {
            exteriorDistance = length(point) - halfStrokeThickness;
            exteriorGradient = safe_normalize(point);
            internalInside = isFullCap || point.x >= -0.001;
            allDistance = exteriorDistance;
        } else if (capKind == 1u) {
            if (isFullCap) {
                let distanceGradient = box_distance_gradient(
                    point,
                    vec2<f32>(halfStrokeThickness),
                    0.0);
                exteriorDistance = distanceGradient.x;
                exteriorGradient = distanceGradient.yz;
                allDistance = exteriorDistance;
            } else {
                let forwardDistance = point.x - halfStrokeThickness;
                let sideDistance = abs(point.y) - halfStrokeThickness;
                exteriorDistance = max(forwardDistance, sideDistance);
                exteriorGradient = select(
                    vec2<f32>(0.0, select(-1.0, 1.0, point.y >= 0.0)),
                    vec2<f32>(1.0, 0.0),
                    forwardDistance >= sideDistance);
                internalInside = point.x >= -0.001;
                allDistance = max(exteriorDistance, -point.x);
            }
        } else {
            let p0 = vec2<f32>(0.0, -halfStrokeThickness);
            let p1 = vec2<f32>(halfStrokeThickness, 0.0);
            let p2 = vec2<f32>(0.0, halfStrokeThickness);
            let edge0 = p1 - p0;
            let edge1 = p2 - p1;
            let edge2 = p0 - p2;
            let distance0 = -cross_2d(edge0, point - p0) /
                max(length(edge0), 0.0001);
            let distance1 = -cross_2d(edge1, point - p1) /
                max(length(edge1), 0.0001);
            let distance2 = -cross_2d(edge2, point - p2) /
                max(length(edge2), 0.0001);
            let gradient0 = vec2<f32>(edge0.y, -edge0.x) /
                max(length(edge0), 0.0001);
            let gradient1 = vec2<f32>(edge1.y, -edge1.x) /
                max(length(edge1), 0.0001);
            exteriorDistance = max(distance0, distance1);
            exteriorGradient = select(
                gradient1,
                gradient0,
                distance0 >= distance1);
            internalInside = distance2 <= 0.001;
            allDistance = max(exteriorDistance, distance2);
        }

        let filterWidth = max(
            abs(dot(exteriorGradient, atlasCoordDx)) +
                abs(dot(exteriorGradient, atlasCoordDy)),
            0.0001);
        let antialiasedAlpha = select(
            0.0,
            1.0 - smoothstep(
                -0.5 * filterWidth,
                0.5 * filterWidth,
                exteriorDistance),
            internalInside);
        let aliasedAlpha = select(
            0.0,
            1.0,
            internalInside && allDistance <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    } else if (sType == 23u) {
        // Analytic one-device-pixel path join. color stores the two outer
        // offsets, shapeSize stores a valid miter intersection, cornerRadius
        // selects miter/bevel/round, and gridIndex preserves turn direction.
        // Body-facing radial edges are hard-owned to prevent overlap seams.
        let point = input.texCoord;
        let previousOuter = input.color.xy;
        let nextOuter = input.color.zw;
        let miterPoint = input.shapeSize;
        let joinKind = u32(round(input.cornerRadius));
        var exteriorDistance = 0.0;
        var exteriorGradient = vec2<f32>(1.0, 0.0);
        var internalInside = true;
        var allDistance = 0.0;

        if (joinKind == 2u) {
            let startAngle = atan2(previousOuter.y, previousOuter.x);
            let endAngle = atan2(nextOuter.y, nextOuter.x);
            var sweep = endAngle - startAngle;
            if (input.gridIndex > 0.0 && sweep < 0.0) {
                sweep = sweep + PROGPU_TWO_PI;
            } else if (input.gridIndex < 0.0 && sweep > 0.0) {
                sweep = sweep - PROGPU_TWO_PI;
            }
            let pointAngle = atan2(point.y, point.x);
            internalInside = is_angle_inside_arc(
                pointAngle,
                startAngle,
                sweep);
            exteriorDistance = length(point) - length(previousOuter);
            exteriorGradient = safe_normalize(point);
            allDistance = exteriorDistance;
        } else if (joinKind == 0u && input.strokeThickness > 0.5) {
            // Convex miter polygon order: previous outer, intersection, next
            // outer, center. Only the first two edges are exterior.
            let p0 = previousOuter;
            let p1 = miterPoint;
            let p2 = nextOuter;
            let p3 = vec2<f32>(0.0);
            let edge0 = p1 - p0;
            let edge1 = p2 - p1;
            let edge2 = p3 - p2;
            let edge3 = p0 - p3;
            let orientation = select(
                -1.0,
                1.0,
                cross_2d(edge0, p2 - p0) >= 0.0);
            let distance0 = -orientation * cross_2d(edge0, point - p0) /
                max(length(edge0), 0.0001);
            let distance1 = -orientation * cross_2d(edge1, point - p1) /
                max(length(edge1), 0.0001);
            let distance2 = -orientation * cross_2d(edge2, point - p2) /
                max(length(edge2), 0.0001);
            let distance3 = -orientation * cross_2d(edge3, point - p3) /
                max(length(edge3), 0.0001);
            let gradient0 = orientation *
                vec2<f32>(edge0.y, -edge0.x) /
                max(length(edge0), 0.0001);
            let gradient1 = orientation *
                vec2<f32>(edge1.y, -edge1.x) /
                max(length(edge1), 0.0001);
            exteriorDistance = max(distance0, distance1);
            exteriorGradient = select(
                gradient1,
                gradient0,
                distance0 >= distance1);
            internalInside = distance2 <= 0.001 && distance3 <= 0.001;
            allDistance = max(
                max(distance0, distance1),
                max(distance2, distance3));
        } else {
            // Bevel, including a miter that exceeds its transformed limit.
            let p0 = previousOuter;
            let p1 = vec2<f32>(0.0);
            let p2 = nextOuter;
            let edge0 = p1 - p0;
            let edge1 = p2 - p1;
            let edge2 = p0 - p2;
            let orientation = select(
                -1.0,
                1.0,
                cross_2d(edge0, p2 - p0) >= 0.0);
            let distance0 = -orientation * cross_2d(edge0, point - p0) /
                max(length(edge0), 0.0001);
            let distance1 = -orientation * cross_2d(edge1, point - p1) /
                max(length(edge1), 0.0001);
            let distance2 = -orientation * cross_2d(edge2, point - p2) /
                max(length(edge2), 0.0001);
            exteriorGradient = orientation *
                vec2<f32>(edge2.y, -edge2.x) /
                max(length(edge2), 0.0001);
            exteriorDistance = distance2;
            internalInside = distance0 <= 0.001 && distance1 <= 0.001;
            allDistance = max(distance2, max(distance0, distance1));
        }

        let filterWidth = max(
            abs(dot(exteriorGradient, atlasCoordDx)) +
                abs(dot(exteriorGradient, atlasCoordDy)),
            0.0001);
        let antialiasedAlpha = select(
            0.0,
            1.0 - smoothstep(
                -0.5 * filterWidth,
                0.5 * filterWidth,
                exteriorDistance),
            internalInside);
        let aliasedAlpha = select(
            0.0,
            1.0,
            internalInside && allDistance <= 0.0);
        shapeAlpha = select(antialiasedAlpha, aliasedAlpha, aliasedEdge);
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    // Process Color Brush
    let bIdx = u32(round(input.brushIndex));
    let brush = brushes[bIdx];

    var finalColor = input.color;
    if (brush.brushType == 0u) {
        if (sType == 5u || sType == 6u ||
            (sType >= 12u && sType <= 18u) ||
            sType == 22u || sType == 23u || sType == 24u) {
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
            // Sweep gradient: atan2 produces one clockwise turn in [0, 360). The affine
            // angular remap places startAngle at t=0 and endAngle at t=1 before the common
            // clamp/repeat/mirror/decal policy. This is O(1) time and O(1) local storage.
            let direction = brushCoord - brush.gradientCenter;
            var angleTurns = atan2(direction.y, direction.x) / (2.0 * 3.141592653589793);
            if (angleTurns < 0.0) {
                angleTurns = angleTurns + 1.0;
            }
            let angleDegrees = angleTurns * 360.0;
            let angleSpan = max(brush.gradientStart.y - brush.gradientStart.x, 0.000001);
            t = (angleDegrees - brush.gradientStart.x) / angleSpan;
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
        } else if ((brush.spreadMethod & 0x7fffffffu) == 3u && (t < 0.0 || t > 1.0)) {
            finalColor = vec4<f32>(0.0);
        } else {
            t = apply_gradient_spread(t, brush.spreadMethod & 0x7fffffffu);
            let gradColor = sample_gradient_color(brush, t);
            finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
        }
    }

    if (sType == 18u) {
        finalColor = blend_mesh_colors(finalColor, input.color, u32(round(input.cornerRadius)));
    }

    // Vertex meshes apply semantic state opacity after brush/vertex blending.
    // Applying it to the brush first is not equivalent for Porter-Duff and
    // advanced color blend modes. Other shapes retain their existing alpha.
    let stateOpacity = select(1.0, clamp(input.strokeThickness, 0.0, 1.0), sType == 18u);
    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha * maskAlpha * stateOpacity);
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let maskAlpha = sample_mask_alpha(input.position.xy);
    let color = vector_fs_main(input, maskAlpha);
    if (maskAlpha <= 0.0) {
        discard;
    }
    return color;
}

@fragment
fn fs_main_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    return vector_fs_main(input, 1.0);
}

@fragment
fn fs_main_premultiplied(input: VertexOutput) -> @location(0) vec4<f32> {
    let maskAlpha = sample_mask_alpha(input.position.xy);
    let color = vector_fs_main(input, maskAlpha);
    if (maskAlpha <= 0.0) {
        discard;
    }
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_main_premultiplied_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = vector_fs_main(input, 1.0);
    return vec4<f32>(color.rgb * color.a, color.a);
}

@fragment
fn fs_mask(input: VertexOutput) -> @location(0) vec4<f32> {
    let maskAlpha = sample_mask_alpha(input.position.xy);
    let color = vector_fs_main(input, maskAlpha);
    if (maskAlpha <= 0.0) {
        discard;
    }
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}

@fragment
fn fs_mask_unmasked(input: VertexOutput) -> @location(0) vec4<f32> {
    let color = vector_fs_main(input, 1.0);
    return vec4<f32>(color.a, 0.0, 0.0, 1.0);
}
