// Algorithm: Evaluate the original registered vector material at its retained source coordinate and derivatives, preserving stops, color space, spread, transformed hatch/noise/path-gradient policy and opacity.
// Time complexity: Original bounded stop/edge/family/octave loops; path gradients inspect at most 128 edges, noise at most 255 octaves, hatches at most six dashes per retained family.
// Space complexity: O(1) local storage and original brush/gradient-stop reads; no material flattening or intermediate paint texture.
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

fn apply_gradient_spread(t: f32, spreadMethod: u32) -> f32 {
    if (spreadMethod == 4u) {
        return clamp(t, 0.0, 1.0);
    }
    if (spreadMethod == 1u) {
        let period = fract(t * 0.5) * 2.0;
        return select(period, 2.0 - period, period > 1.0);
    }

    if (spreadMethod == 2u) {
        return fract(t);
    }

    // Keep Pad coordinates outside the unit interval until stop sampling.
    // sample_gradient_color clamps through its first/last stop while retaining
    // the distinction between an outside coordinate and an exact duplicate
    // endpoint, where the last stop at that offset must win.
    return t;
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

    if ((brush.spreadMethod & 0x40000000u) != 0u) {
        if (t < 0.0) {
            return brush.stopColors0;
        }
        if (t > 1.0) {
            return brush.stopColors1;
        }
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
        // An exact offset belongs to the last stop at that offset. Besides
        // matching Skia, this preserves hard transitions and ensures Pad
        // selects the final color when duplicate stops sit at t = 1.
        if (t < currentOffset) {
            let factor = (t - previousOffset) / max(currentOffset - previousOffset, 0.0001);
            return interpolate_gradient_color(brush, previousColor, currentColor, clamp(factor, 0.0, 1.0));
        }

        previousColor = currentColor;
        previousOffset = currentOffset;
        i = i + 1u;
    }

    return previousColor;
}

fn path_cross(left: vec2<f32>, right: vec2<f32>) -> f32 {
    return left.x * right.y - left.y * right.x;
}

fn path_gradient_point(brush: Brush, index: u32) -> vec2<f32> {
    return gradientStops[brush.stopOffset + index * 2u].color.xy;
}

fn path_gradient_surround_color(brush: Brush, index: u32) -> vec4<f32> {
    return gradientStops[brush.stopOffset + index * 2u + 1u].color;
}

fn sample_path_curve_color(
    brush: Brush,
    curveOffset: u32,
    curveCount: u32,
    t: f32) -> vec4<f32> {
    var previous = gradientStops[curveOffset];
    var index = 1u;
    loop {
        if (index >= curveCount) {
            break;
        }
        let current = gradientStops[curveOffset + index];
        if (t < current.offset) {
            let factor = clamp(
                (t - previous.offset) /
                    max(current.offset - previous.offset, 0.0001),
                0.0,
                1.0);
            return interpolate_gradient_color(
                brush,
                previous.color,
                current.color,
                factor);
        }
        previous = current;
        index = index + 1u;
    }
    return previous.color;
}

fn sample_path_blend_factor(
    curveOffset: u32,
    curveCount: u32,
    t: f32) -> f32 {
    var previous = gradientStops[curveOffset];
    var index = 1u;
    loop {
        if (index >= curveCount) {
            break;
        }
        let current = gradientStops[curveOffset + index];
        if (t < current.offset) {
            let interval = clamp(
                (t - previous.offset) /
                    max(current.offset - previous.offset, 0.0001),
                0.0,
                1.0);
            return mix(previous.color.x, current.color.x, interval);
        }
        previous = current;
        index = index + 1u;
    }
    return previous.color.x;
}

fn sample_path_gradient(brush: Brush, coordinate: vec2<f32>) -> vec4<f32> {
    let boundaryCount = min(u32(round(brush.gradientRadius)), 128u);
    let curveCount = u32(round(brush.gradientRadiusY));
    if (boundaryCount < 2u || curveCount == 0u) {
        return vec4<f32>(0.0);
    }

    let direction = coordinate - brush.gradientCenter;
    var bestRay = 1e30;
    var bestEdgeFactor = 0.0;
    var bestEdge = 0u;
    var bestFocusRay = 1e30;
    let focusScale = clamp(abs(brush.gradientEnd), vec2<f32>(0.0), vec2<f32>(1.0));
    var index = 0u;
    loop {
        if (index >= boundaryCount) {
            break;
        }
        let next = select(index + 1u, 0u, index + 1u == boundaryCount);
        let point0 = path_gradient_point(brush, index);
        let point1 = path_gradient_point(brush, next);
        let edge = point1 - point0;
        let relative = point0 - brush.gradientCenter;
        let denominator = path_cross(direction, edge);
        if (abs(denominator) > 0.000001) {
            let ray = path_cross(relative, edge) / denominator;
            let edgeFactor = path_cross(relative, direction) / denominator;
            if (ray > 0.0 && edgeFactor >= -0.00001 &&
                edgeFactor <= 1.00001 && ray < bestRay) {
                bestRay = ray;
                bestEdgeFactor = clamp(edgeFactor, 0.0, 1.0);
                bestEdge = index;
            }

            if (any(focusScale > vec2<f32>(0.000001))) {
                let focus0 = brush.gradientCenter +
                    (point0 - brush.gradientCenter) * focusScale;
                let focus1 = brush.gradientCenter +
                    (point1 - brush.gradientCenter) * focusScale;
                let focusEdge = focus1 - focus0;
                let focusDenominator = path_cross(direction, focusEdge);
                if (abs(focusDenominator) > 0.000001) {
                    let focusRay = path_cross(
                        focus0 - brush.gradientCenter,
                        focusEdge) / focusDenominator;
                    let focusEdgeFactor = path_cross(
                        focus0 - brush.gradientCenter,
                        direction) / focusDenominator;
                    if (focusRay > 0.0 && focusEdgeFactor >= -0.00001 &&
                        focusEdgeFactor <= 1.00001 && focusRay < bestFocusRay) {
                        bestFocusRay = focusRay;
                    }
                }
            }
        }
        index = index + 1u;
    }

    var t = 0.0;
    if (bestRay < 1e29) {
        t = 1.0 / bestRay;
        if (bestFocusRay < 1e29) {
            let focusFraction = clamp(bestFocusRay / bestRay, 0.0, 0.999999);
            t = max(0.0, (t - focusFraction) / (1.0 - focusFraction));
        }
    }

    let spread = brush.spreadMethod & 0x7fffffffu;
    if (spread == 3u && (t < 0.0 || t > 1.0)) {
        return vec4<f32>(0.0);
    }
    t = apply_gradient_spread(t, spread);

    let curveOffset = brush.stopOffset + boundaryCount * 2u;
    if (brush.stopColors1.x > 0.5) {
        return sample_path_curve_color(
            brush,
            curveOffset,
            curveCount,
            1.0 - t);
    }

    let nextEdge = select(bestEdge + 1u, 0u, bestEdge + 1u == boundaryCount);
    let surround = interpolate_gradient_color(
        brush,
        path_gradient_surround_color(brush, bestEdge),
        path_gradient_surround_color(brush, nextEdge),
        bestEdgeFactor);
    let factor = sample_path_blend_factor(curveOffset, curveCount, t);
    return interpolate_gradient_color(
        brush,
        surround,
        brush.stopColors0,
        clamp(factor, 0.0, 1.0));
}

fn transform_brush_coordinate(brush: Brush, coord: vec2<f32>) -> vec2<f32> {
    let p = vec3<f32>(coord, 1.0);
    return vec2<f32>(
        dot(p, brush.coordinateTransform0.xyz),
        dot(p, brush.coordinateTransform1.xyz));
}

fn transform_brush_vector(brush: Brush, value: vec2<f32>) -> vec2<f32> {
    return vec2<f32>(
        dot(value, brush.coordinateTransform0.xy),
        dot(value, brush.coordinateTransform1.xy));
}

// One periodic hatch family. A zero authored thickness is a one-device-pixel
// hairline derived from the projected pattern-coordinate footprint; positive
// widths remain in pattern coordinates. Work and storage are O(1).
fn hatch_axis_coverage(
    coord: vec2<f32>,
    coordDx: vec2<f32>,
    coordDy: vec2<f32>,
    direction: vec2<f32>,
    spacing: f32,
    thickness: f32) -> f32 {
    let distance = dot(coord, direction);
    let phase = abs(fract(distance / spacing) * spacing - spacing * 0.5);
    let filterWidth = max(
        abs(dot(direction, coordDx)) + abs(dot(direction, coordDy)),
        0.0001);
    var halfWidth = thickness * 0.5;
    if (thickness <= 0.0) {
        halfWidth = filterWidth * 0.5;
    }
    return 1.0 - smoothstep(
        max(halfWidth - filterWidth * 0.5, 0.0),
        halfWidth + filterWidth * 0.5,
        phase);
}

fn hatch_pattern_dash_value(record2: GradientStop, record3: GradientStop, index: u32) -> f32 {
    switch index {
        case 0u: { return record2.color.x; }
        case 1u: { return record2.color.y; }
        case 2u: { return record2.color.z; }
        case 3u: { return record2.color.w; }
        case 4u: { return record2.offset; }
        default: { return record3.color.x; }
    }
}

fn hatch_pattern_row_coverage(
    brush: Brush,
    familyRecord: u32,
    coord: vec2<f32>,
    coordDx: vec2<f32>,
    coordDy: vec2<f32>,
    row: f32) -> f32 {
    let record0 = gradientStops[familyRecord];
    let record1 = gradientStops[familyRecord + 1u];
    let record2 = gradientStops[familyRecord + 2u];
    let record3 = gradientStops[familyRecord + 3u];
    let base = record0.color.xy;
    let tangent = record0.color.zw;
    let normal = vec2<f32>(-tangent.y, tangent.x);
    let spacing = record0.offset;
    let delta = coord - base;
    let normalDistance = abs(dot(delta, normal) - row * spacing);
    let normalFilter = max(
        abs(dot(normal, coordDx)) + abs(dot(normal, coordDy)),
        0.0001);
    var halfWidth = brush.gradientRadius * 0.5;
    if (brush.gradientRadius <= 0.0) {
        halfWidth = normalFilter * 0.5;
    }
    let normalCoverage = 1.0 - smoothstep(
        max(halfWidth - normalFilter * 0.5, 0.0),
        halfWidth + normalFilter * 0.5,
        normalDistance);
    let dashCount = u32(round(record1.color.z));
    if (dashCount == 0u || normalCoverage <= 0.0) {
        return normalCoverage;
    }

    let period = record1.color.y;
    let tangentCoordinate = dot(delta, tangent) - row * record1.color.x;
    let phase = fract(tangentCoordinate / period) * period;
    let tangentFilter = max(
        abs(dot(tangent, coordDx)) + abs(dot(tangent, coordDy)),
        0.0001);
    let radialFilter = max(length(coordDx) + length(coordDy), 0.0001);
    var cursor = 0.0;
    var coverage = 0.0;
    var dashIndex = 0u;
    loop {
        if (dashIndex >= dashCount || dashIndex >= 6u) { break; }
        let dash = hatch_pattern_dash_value(record2, record3, dashIndex);
        let lengthValue = abs(dash);
        if (dash > 0.0) {
            let center = cursor + lengthValue * 0.5;
            let wrapped = abs(phase - center);
            let tangentDistance = max(
                min(wrapped, period - wrapped) - lengthValue * 0.5,
                0.0);
            let tangentCoverage = 1.0 - smoothstep(
                0.0,
                tangentFilter * 0.5,
                tangentDistance);
            coverage = max(coverage, normalCoverage * tangentCoverage);
        } else if (dash == 0.0) {
            let wrapped = abs(phase - cursor);
            let tangentDistance = min(wrapped, period - wrapped);
            let dotDistance = length(vec2<f32>(normalDistance, tangentDistance));
            var dotRadius = brush.gradientRadius * 0.5;
            if (brush.gradientRadius <= 0.0) {
                dotRadius = radialFilter * 0.5;
            }
            coverage = max(coverage, 1.0 - smoothstep(
                max(dotRadius - radialFilter * 0.5, 0.0),
                dotRadius + radialFilter * 0.5,
                dotDistance));
        }
        cursor = cursor + lengthValue;
        dashIndex = dashIndex + 1u;
    }
    return coverage;
}

fn hatch_pattern_set_coverage(
    brush: Brush,
    coord: vec2<f32>,
    coordDx: vec2<f32>,
    coordDy: vec2<f32>) -> f32 {
    var coverage = 0.0;
    var familyIndex = 0u;
    loop {
        if (familyIndex >= brush.spreadMethod) { break; }
        let familyRecord = brush.stopOffset + familyIndex * 4u;
        let record0 = gradientStops[familyRecord];
        let tangent = record0.color.zw;
        let normal = vec2<f32>(-tangent.y, tangent.x);
        let rowCoordinate = dot(coord - record0.color.xy, normal) / record0.offset;
        let nearestRow = floor(rowCoordinate + 0.5);
        coverage = max(coverage, hatch_pattern_row_coverage(
            brush, familyRecord, coord, coordDx, coordDy, nearestRow - 1.0));
        coverage = max(coverage, hatch_pattern_row_coverage(
            brush, familyRecord, coord, coordDx, coordDy, nearestRow));
        coverage = max(coverage, hatch_pattern_row_coverage(
            brush, familyRecord, coord, coordDx, coordDy, nearestRow + 1.0));
        familyIndex = familyIndex + 1u;
    }
    return coverage;
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

fn sample_registered_material(brush: Brush, solidColor: vec4<f32>, useBrushSolidColor: bool, evalCoord: vec2<f32>, evalCoordDx: vec2<f32>, evalCoordDy: vec2<f32>) -> vec4<f32> {
    var finalColor = solidColor;
    if (brush.brushType == 0u) {
        if (useBrushSolidColor) {
            finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
        } else {
            finalColor = vec4<f32>(solidColor.rgb, solidColor.a * brush.opacity);
        }

    } else {
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        let brushCoordDx = transform_brush_vector(brush, evalCoordDx);
        let brushCoordDy = transform_brush_vector(brush, evalCoordDy);
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
        } else if (brush.brushType == 3u || brush.brushType == 4u) {
            // Analytic hatch: project the transformed pattern point onto one
            // periodic normal axis; cross-hatch evaluates its perpendicular.
            // The semantic compilers validate positive spacing before upload.
            let theta = brush.gradientRadius;
            let spacing = brush.gradientCenter.x;
            let thickness = brush.gradientCenter.y;
            let direction0 = vec2<f32>(cos(theta), sin(theta));
            var hatchCoverage = hatch_axis_coverage(
                brushCoord,
                brushCoordDx,
                brushCoordDy,
                direction0,
                spacing,
                thickness);
            if (brush.brushType == 4u) {
                let direction1 = vec2<f32>(-direction0.y, direction0.x);
                hatchCoverage = max(
                    hatchCoverage,
                    hatch_axis_coverage(
                        brushCoord,
                        brushCoordDx,
                        brushCoordDy,
                        direction1,
                        spacing,
                        thickness));
            }
            if (hatchCoverage <= 0.0) {
                discard;
            }
            finalColor = vec4<f32>(
                brush.stopColors0.rgb,
                brush.stopColors0.a * brush.opacity * hatchCoverage);
        } else if (brush.brushType == 9u) {
            // Fixed 8x8 System.Drawing hatch tile. Signed remainder keeps the
            // pattern phase stable for negative world coordinates.
            let integerCoord = vec2<i32>(floor(brushCoord));
            let tileX = u32(((integerCoord.x % 8) + 8) % 8);
            let tileY = u32(((integerCoord.y % 8) + 8) % 8);
            let bitIndex = tileY * 8u + tileX;
            let word = select(brush.stopCount, brush.stopOffset, bitIndex >= 32u);
            let patternBit = (word >> (bitIndex & 31u)) & 1u;
            let patternColor = select(brush.stopColors1, brush.stopColors0, patternBit != 0u);
            finalColor = vec4<f32>(patternColor.rgb, patternColor.a * brush.opacity);
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
        } else if (brush.brushType == 8u) {
            let hatchCoverage = hatch_pattern_set_coverage(
                brush, brushCoord, brushCoordDx, brushCoordDy);
            if (hatchCoverage <= 0.0) {
                discard;
            }
            finalColor = vec4<f32>(
                brush.stopColors0.rgb,
                brush.stopColors0.a * brush.opacity * hatchCoverage);
        } else if (brush.brushType == 10u) {
            let pathColor = sample_path_gradient(brush, brushCoord);
            finalColor = vec4<f32>(pathColor.rgb, pathColor.a * brush.opacity);
        }
        if (brush.brushType == 3u || brush.brushType == 4u ||
            brush.brushType == 7u || brush.brushType == 8u ||
            brush.brushType == 9u || brush.brushType == 10u) {
            // Procedural hatch/noise was evaluated directly above.
        } else if (gradientCoverage <= 0.0) {
            if ((brush.spreadMethod & 0x80000000u) != 0u) {
                finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
            } else {
                finalColor = vec4<f32>(0.0);
            }
        } else if ((brush.spreadMethod & 0x3fffffffu) == 3u && (t < 0.0 || t > 1.0)) {
            finalColor = vec4<f32>(0.0);
        } else {
            t = apply_gradient_spread(t, brush.spreadMethod & 0x3fffffffu);
            let gradColor = sample_gradient_color(brush, t);
            finalColor = vec4<f32>(gradColor.rgb, gradColor.a * brush.opacity);
        }
    }

    return finalColor;
}
