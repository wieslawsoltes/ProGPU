// Algorithm: Transform hatch geometry, evaluate analytic line/polynomial-Bezier/positive-weight rational-quadratic/rational-cubic winding, and evaluate brush, gradient, affine pattern-space hatch-family, and anti-aliased coverage functions.
// Time complexity: O(S + F * 6) per hatch fragment for S retained boundary segments and F retained DXF/PAT families with the specified six-dash maximum; non-hatch brush paths are O(1).
// Space complexity: O(1) local storage with bounded uniform/storage reads.
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

struct VSUniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
};

struct GpuHatchRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    _pad0: u32,
    _pad1: u32,
};

struct GpuHatchSegment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    _pad0: u32,
    _pad1: u32,
    _pad2: u32,
};

@group(0) @binding(0) var<uniform> uniforms: VSUniforms;
@group(0) @binding(1) var<storage, read> brushes: array<Brush>;
@group(0) @binding(2) var<storage, read> hatchRecords: array<GpuHatchRecord>;
@group(0) @binding(3) var<storage, read> hatchSegments: array<GpuHatchSegment>;
@group(0) @binding(4) var<storage, read> gradientStops: array<GradientStop>;

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
    let normalFilter = max(abs(dot(normal, coordDx)) + abs(dot(normal, coordDy)), 0.0001);
    var halfWidth = brush.gradientRadius * 0.5;
    if (brush.gradientRadius <= 0.0) { halfWidth = normalFilter * 0.5; }
    let normalCoverage = 1.0 - smoothstep(
        max(halfWidth - normalFilter * 0.5, 0.0),
        halfWidth + normalFilter * 0.5,
        normalDistance);
    let dashCount = u32(round(record1.color.z));
    if (dashCount == 0u || normalCoverage <= 0.0) { return normalCoverage; }
    let period = record1.color.y;
    let tangentCoordinate = dot(delta, tangent) - row * record1.color.x;
    let phase = fract(tangentCoordinate / period) * period;
    let tangentFilter = max(abs(dot(tangent, coordDx)) + abs(dot(tangent, coordDy)), 0.0001);
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
            let tangentDistance = max(min(wrapped, period - wrapped) - lengthValue * 0.5, 0.0);
            let tangentCoverage = 1.0 - smoothstep(0.0, tangentFilter * 0.5, tangentDistance);
            coverage = max(coverage, normalCoverage * tangentCoverage);
        } else if (dash == 0.0) {
            let wrapped = abs(phase - cursor);
            let tangentDistance = min(wrapped, period - wrapped);
            let dotDistance = length(vec2<f32>(normalDistance, tangentDistance));
            var dotRadius = brush.gradientRadius * 0.5;
            if (brush.gradientRadius <= 0.0) { dotRadius = radialFilter * 0.5; }
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
        coverage = max(coverage, hatch_pattern_row_coverage(brush, familyRecord, coord, coordDx, coordDy, nearestRow - 1.0));
        coverage = max(coverage, hatch_pattern_row_coverage(brush, familyRecord, coord, coordDx, coordDy, nearestRow));
        coverage = max(coverage, hatch_pattern_row_coverage(brush, familyRecord, coord, coordDx, coordDy, nearestRow + 1.0));
        familyIndex = familyIndex + 1u;
    }
    return coverage;
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

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
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

    var inPos = input.position;
    var inTexCoord = input.texCoord;
    var inShapeSize = input.shapeSize;
    var inColor = input.color;

    if (isStatic || useGpuTransforms) {
        if (useGpuTransforms) {
            inPos = (uniforms.view * vec4<f32>(input.position, 0.0, 1.0)).xy;
            inTexCoord = (uniforms.view * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
            inShapeSize = (uniforms.view * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
        } else {
            inPos = (uniforms.mvp * vec4<f32>(input.position, 0.0, 1.0)).xy;
            inTexCoord = (uniforms.mvp * vec4<f32>(input.texCoord, 0.0, 1.0)).xy;
            inShapeSize = (uniforms.mvp * vec4<f32>(input.shapeSize, 0.0, 1.0)).xy;
        }
    }

    output.position = uniforms.projection * vec4<f32>(inPos, 0.0, 1.0);
    output.color = inColor;
    output.texCoord = inTexCoord;
    output.brushIndex = input.brushIndex;
    output.shapeSize = inShapeSize;
    output.cornerRadius = input.cornerRadius;
    output.strokeThickness = input.strokeThickness;
    output.shapeType = f32(sType);
    output.gridIndex = 0.0;
    return output;
}


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


fn is_hatch_point_inside(p: vec2<f32>, record: GpuHatchRecord) -> bool {
    var winding: i32 = 0;
    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = hatchSegments[i];
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
                        let intersectX = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (p.x < intersectX) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 4u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let weight = bitcast<f32>(seg._pad0);
            // ABI validation guarantees a positive weight, so W(t) stays
            // positive. The quadratic has at most two roots; this loop is
            // fixed and independent of path size and zoom.
            let ay = A.y - p.y;
            let by = B.y - p.y;
            let cy = C.y - p.y;
            let a = ay - 2.0 * weight * by + cy;
            let b = 2.0 * (weight * by - ay);
            let c = ay;
            let coefficient_scale = max(max(abs(a), abs(b)), abs(c));
            var roots = array<f32, 2>(0.0, 0.0);
            var root_count: u32 = 0u;
            solve_quadratic(
                a / max(coefficient_scale, 1.0e-30),
                b / max(coefficient_scale, 1.0e-30),
                c / max(coefficient_scale, 1.0e-30),
                &roots,
                &root_count);

            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let numerator_y = omt_eval * omt_eval * A.y
                        + 2.0 * weight * omt_eval * t_eval * B.y
                        + t_eval * t_eval * C.y;
                    let denominator = omt_eval * omt_eval
                        + 2.0 * weight * omt_eval * t_eval
                        + t_eval * t_eval;
                    let numerator_derivative_y =
                        2.0 * omt_eval * (weight * B.y - A.y)
                        + 2.0 * t_eval * (C.y - weight * B.y);
                    let denominator_derivative =
                        2.0 * omt_eval * (weight - 1.0)
                        + 2.0 * t_eval * (1.0 - weight);
                    let deriv_y = numerator_derivative_y * denominator
                        - numerator_y * denominator_derivative;
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
                        let rational_denominator = omt * omt
                            + 2.0 * weight * omt * tc
                            + tc * tc;
                        let intersectX = (omt * omt * A.x
                            + 2.0 * weight * omt * tc * B.x
                            + tc * tc * C.x) / rational_denominator;
                        if (p.x < intersectX) {
                            if (deriv_y > 0.0) {
                                winding = winding + 1;
                            } else if (deriv_y < 0.0) {
                                winding = winding - 1;
                            }
                        }
                    }
                }
            }
        } else if (seg.segmentType == 2u || seg.segmentType == 5u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;
            let rational = seg.segmentType == 5u;
            let weight1 = select(1.0, bitcast<f32>(seg._pad0), rational);
            let weight2 = select(1.0, bitcast<f32>(seg._pad1), rational);

            // Polynomial cubics are the unit-weight case of this homogeneous
            // equation, so both exact path kinds share one bounded branch.
            let ay = A.y - p.y;
            let by = weight1 * (B.y - p.y);
            let cy = weight2 * (C.y - p.y);
            let dy = D_pt.y - p.y;
            let a = -ay + 3.0 * by - 3.0 * cy + dy;
            let b = 3.0 * ay - 6.0 * by + 3.0 * cy;
            let c = -3.0 * ay + 3.0 * by;
            let d_coeff = ay;
            let coefficient_scale = max(
                max(abs(a), abs(b)),
                max(abs(c), abs(d_coeff)));
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            var root_count: u32 = 0u;
            solve_cubic(
                a / max(coefficient_scale, 1.0e-30),
                b / max(coefficient_scale, 1.0e-30),
                c / max(coefficient_scale, 1.0e-30),
                d_coeff / max(coefficient_scale, 1.0e-30),
                &roots,
                &root_count);

            // A rational cubic contributes at most three roots. This bound is
            // fixed and independent of path size, zoom, and control geometry.
            for (var r: u32 = 0u; r < root_count; r = r + 1u) {
                let t = roots[r];
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let omt2_eval = omt_eval * omt_eval;
                    let t2_eval = t_eval * t_eval;
                    let numerator_y = omt2_eval * omt_eval * A.y
                        + 3.0 * weight1 * omt2_eval * t_eval * B.y
                        + 3.0 * weight2 * omt_eval * t2_eval * C.y
                        + t2_eval * t_eval * D_pt.y;
                    let denominator = omt2_eval * omt_eval
                        + 3.0 * weight1 * omt2_eval * t_eval
                        + 3.0 * weight2 * omt_eval * t2_eval
                        + t2_eval * t_eval;
                    let numerator_derivative_y = 3.0 * (
                        omt2_eval * (weight1 * B.y - A.y)
                        + 2.0 * omt_eval * t_eval *
                            (weight2 * C.y - weight1 * B.y)
                        + t2_eval * (D_pt.y - weight2 * C.y));
                    let denominator_derivative = 3.0 * (
                        omt2_eval * (weight1 - 1.0)
                        + 2.0 * omt_eval * t_eval * (weight2 - weight1)
                        + t2_eval * (1.0 - weight2));
                    let deriv_y = numerator_derivative_y * denominator
                        - numerator_y * denominator_derivative;

                    // Preserve the direction-aware half-open intervals exactly:
                    // upward [0,1), downward (0,1].
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
                        let omt2 = omt * omt;
                        let t2 = tc * tc;
                        let rational_denominator = omt2 * omt
                            + 3.0 * weight1 * omt2 * tc
                            + 3.0 * weight2 * omt * t2
                            + t2 * tc;
                        let intersectX = (omt2 * omt * A.x
                            + 3.0 * weight1 * omt2 * tc * B.x
                            + 3.0 * weight2 * omt * t2 * C.x
                            + t2 * tc * D_pt.x) / rational_denominator;
                        if (p.x < intersectX) {
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

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let evalCoordDx = dpdx(input.color.xy);
    let evalCoordDy = dpdy(input.color.xy);
    var shapeAlpha = 0.0;

    let hatchRecordIndex = u32(round(input.color.z));
    let record = hatchRecords[hatchRecordIndex];
    let p = input.color.xy;

    if (is_hatch_point_inside(p, record)) {
        shapeAlpha = 1.0;
    } else {
        shapeAlpha = 0.0;
    }

    if (shapeAlpha <= 0.0) {
        discard;
    }

    let bIdx = u32(round(input.brushIndex));
    let brush = brushes[bIdx];

    var finalColor = input.color;
    let evalCoord = input.color.xy;

    if (brush.brushType == 0u) {
        finalColor = vec4<f32>(brush.stopColors0.rgb, brush.stopColors0.a * brush.opacity);
    } else if (brush.brushType == 3u || brush.brushType == 4u) {
        let theta = brush.gradientRadius;
        let spacing = brush.gradientCenter.x;
        let thickness = brush.gradientCenter.y;
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        let brushCoordDx = transform_brush_vector(brush, evalCoordDx);
        let brushCoordDy = transform_brush_vector(brush, evalCoordDy);
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
    } else if (brush.brushType == 8u) {
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        let brushCoordDx = transform_brush_vector(brush, evalCoordDx);
        let brushCoordDy = transform_brush_vector(brush, evalCoordDy);
        let hatchCoverage = hatch_pattern_set_coverage(
            brush, brushCoord, brushCoordDx, brushCoordDy);
        if (hatchCoverage <= 0.0) {
            discard;
        }
        finalColor = vec4<f32>(
            brush.stopColors0.rgb,
            brush.stopColors0.a * brush.opacity * hatchCoverage);
    } else {
        let brushCoord = transform_brush_coordinate(brush, evalCoord);
        var t: f32 = 0.0;
        var gradientCoverage: f32 = 1.0;
        if (brush.brushType == 1u) {
            let gradVec = brush.gradientEnd - brush.gradientStart;
            let lenSq = dot(gradVec, gradVec);
            if (lenSq > 0.0001) {
                t = dot(brushCoord - brush.gradientStart, gradVec) / lenSq;
            }
        } else if (brush.brushType == 2u) {
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
            let solution = solve_two_point_conical_gradient(brush, brushCoord);
            t = solution.x;
            gradientCoverage = solution.y;
        } else if (brush.brushType == 6u) {
            let direction = brushCoord - brush.gradientCenter;
            t = atan2(direction.y, direction.x) / (2.0 * 3.141592653589793);
            if (t < 0.0) {
                t = t + 1.0;
            }
        }
        if (gradientCoverage <= 0.0) {
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

    return vec4<f32>(finalColor.rgb, finalColor.a * shapeAlpha);
}
