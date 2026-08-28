// Algorithm: Rasterize ordinary, binary-boolean, or bounded postfix path-expression coverage by supersampling each atlas texel, applying analytic line, polynomial Bezier, positive-weight rational-quadratic/rational-cubic, and elliptic-arc winding tests, and combining per-sample membership before coverage averaging.
// Time complexity: O(A*(S+N)) per expression texel for A anti-aliasing samples, total leaf segment visits S, and N postfix instructions; ordinary paths are O(A*S).
// Space complexity: O(D) private mask storage for expression stack depth D<=16 plus O(S+N) read-only record/segment bandwidth and one packed u32 output write per four R8 texels.
const BOOLEAN_PROGRAM_FLAG: u32 = 0x80000000u;
const BOOLEAN_EMPTY_TOKEN: u32 = 0x40000000u;
const BOOLEAN_TOKEN_VALUE_MASK: u32 = 0x3fffffffu;
const MAX_BOOLEAN_STACK_DEPTH: u32 = 16u;

struct PathUniforms {
    xStart: f32,
    yStart: f32,
    scaleX: f32,
    scaleY: f32,
    pathIndex: u32,
    outputOffsetWords: u32,
    outputRowWords: u32,
    width: u32,
    height: u32,
    sampleGrid: u32,
    pathIndexB: u32,
    pathOpKind: u32,
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

@group(0) @binding(0) var<storage, read> pathUniforms: array<PathUniforms>;
@group(0) @binding(1) var<storage, read> pathRecords: array<PathRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var<storage, read_write> coverageOutput: array<u32>;

struct QuadraticRoots {
    values: vec2<f32>,
    count: u32,
};

struct CubicRoots {
    values: vec3<f32>,
    count: u32,
};

struct SampleRow {
    low: vec4<f32>,
    high: vec4<f32>,
};

struct WindingRow {
    low: vec4<i32>,
    high: vec4<i32>,
};

fn solve_quadratic(a: f32, b: f32, c: f32) -> QuadraticRoots {
    var result = QuadraticRoots(vec2<f32>(0.0), 0u);
    if (abs(a) < 0.00001) {
        if (abs(b) > 0.00001) {
            result.values.x = -c / b;
            result.count = 1u;
        }
    } else {
        let d = b * b - 4.0 * a * c;
        if (d == 0.0) {
            result.values.x = -b / (2.0 * a);
            result.count = 1u;
        } else {
            if (d > 0.0) {
                let sqrt_d = sqrt(d);
                result.values = vec2<f32>(
                    (-b - sqrt_d) / (2.0 * a),
                    (-b + sqrt_d) / (2.0 * a));
                result.count = 2u;
            }
        }
    }
    return result;
}

fn cbrt(x: f32) -> f32 {
    if (x < 0.0) {
        return -pow(-x, 1.0 / 3.0);
    }
    return pow(x, 1.0 / 3.0);
}

fn solve_cubic(a_in: f32, b_in: f32, c_in: f32, d_in: f32) -> CubicRoots {
    var result = CubicRoots(vec3<f32>(0.0), 0u);
    if (abs(a_in) < 0.00001) {
        let quadratic = solve_quadratic(b_in, c_in, d_in);
        result.values = vec3<f32>(
            quadratic.values.x,
            quadratic.values.y,
            0.0);
        result.count = quadratic.count;
        return result;
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
        result.values.x = u + v - a / 3.0;
        result.count = 1u;
    } else {
        if (p < 0.0) {
            let r = 2.0 * sqrt(-p / 3.0);
            let val = clamp(-q / (2.0 * sqrt(-p * p * p / 27.0)), -1.0, 1.0);
            let theta = acos(val);
            let pi = 3.14159265359;
            result.values = vec3<f32>(
                r * cos(theta / 3.0) - a / 3.0,
                r * cos((theta + 2.0 * pi) / 3.0) - a / 3.0,
                r * cos((theta + 4.0 * pi) / 3.0) - a / 3.0);
            result.count = 3u;
        } else {
            result.values.x = -a / 3.0;
            result.count = 1u;
        }
    }
    return result;
}

fn quadratic_root_at(roots: QuadraticRoots, index: u32) -> f32 {
    if (index == 0u) {
        return roots.values.x;
    }
    return roots.values.y;
}

fn cubic_root_at(roots: CubicRoots, index: u32) -> f32 {
    if (index == 0u) {
        return roots.values.x;
    }
    if (index == 1u) {
        return roots.values.y;
    }
    return roots.values.z;
}

fn add_crossing(
    winding: ptr<function, WindingRow>,
    samplePositionsX: SampleRow,
    intersectX: f32,
    direction: i32) {
    let direction4 = vec4<i32>(direction);
    (*winding).low = (*winding).low + select(
        vec4<i32>(0),
        direction4,
        samplePositionsX.low < vec4<f32>(intersectX));
    (*winding).high = (*winding).high + select(
        vec4<i32>(0),
        direction4,
        samplePositionsX.high < vec4<f32>(intersectX));
}

fn winding_is_inside(winding: i32, fill_rule: u32) -> bool {
    return select(
        winding != 0,
        abs(winding) % 2 == 1,
        fill_rule == 0u);
}

fn add_arc_crossing(
    dx: f32,
    sample_y: f32,
    center: vec2<f32>,
    rx: f32,
    ry: f32,
    theta1: f32,
    delta_theta: f32,
    cos_phi: f32,
    sin_phi: f32,
    sample_positions_x: SampleRow,
    winding: ptr<function, WindingRow>) {
    let intersect_x = center.x + dx;
    let dy = sample_y - center.y;
    let local_x = dx * cos_phi + dy * sin_phi;
    let local_y = -dx * sin_phi + dy * cos_phi;
    let theta = atan2(local_y / ry, local_x / rx);

    var t = 0.0;
    let pi2 = 6.283185307179586;
    if (delta_theta > 0.0) {
        let diff =
            (theta - theta1) -
            pi2 * floor((theta - theta1) / pi2);
        t = diff / delta_theta;
    } else {
        let diff =
            (theta1 - theta) -
            pi2 * floor((theta1 - theta) / pi2);
        t = diff / (-delta_theta);
    }

    let deriv_y = delta_theta *
        (-rx * sin(theta) * sin_phi +
            ry * cos(theta) * cos_phi);

    // Preserve the direction-aware half-open intervals exactly:
    // upward [0,1), downward (0,1].
    if (deriv_y > 0.0 && t >= 0.0 && t < 1.0) {
        add_crossing(
            winding,
            sample_positions_x,
            intersect_x,
            1);
    } else if (deriv_y < 0.0 && t > 0.0 && t <= 1.0) {
        add_crossing(
            winding,
            sample_positions_x,
            intersect_x,
            -1);
    }
}

fn path_sample_x(
    pixel_x: f32,
    lane: u32,
    sample_grid: u32,
    scale_x: f32) -> f32 {
    let sample_offset_x =
        (f32(lane) + 0.5) / f32(sample_grid);
    return (pixel_x + sample_offset_x) / scale_x;
}

fn row_coverage_mask(
    pixelX: f32,
    sampleY: f32,
    sampleGrid: u32,
    scaleX: f32,
    record: PathRecord) -> u32 {
    var winding = WindingRow(vec4<i32>(0), vec4<i32>(0));
    let samplePositionsX = SampleRow(
        vec4<f32>(
            path_sample_x(pixelX, 0u, sampleGrid, scaleX),
            path_sample_x(pixelX, 1u, sampleGrid, scaleX),
            path_sample_x(pixelX, 2u, sampleGrid, scaleX),
            path_sample_x(pixelX, 3u, sampleGrid, scaleX)),
        vec4<f32>(
            path_sample_x(pixelX, 4u, sampleGrid, scaleX),
            path_sample_x(pixelX, 5u, sampleGrid, scaleX),
            path_sample_x(pixelX, 6u, sampleGrid, scaleX),
            path_sample_x(pixelX, 7u, sampleGrid, scaleX)));

    let endIdx = record.startSegment + record.segmentCount;
    for (var i: u32 = record.startSegment; i < endIdx; i = i + 1u) {
        let seg = segments[i];
        if (seg.segmentType == 0u) {
            let A = seg.p0;
            let B = seg.p1;
            if (A.y == B.y) {
                continue;
            }
            if (A.y <= sampleY) {
                if (B.y > sampleY) {
                    let t = (sampleY - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    add_crossing(&winding, samplePositionsX, intersectX, 1);
                }
            } else {
                if (B.y <= sampleY) {
                    let t = (sampleY - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    add_crossing(&winding, samplePositionsX, intersectX, -1);
                }
            }
        } else if (seg.segmentType == 1u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;

            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - sampleY;

            let roots = solve_quadratic(a, b, c);

            // A quadratic contributes at most two roots, so this loop is fixed
            // and does not depend on path size or zoom.
            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = quadratic_root_at(roots, r);
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let omt_eval = 1.0 - t_eval;
                    let deriv_y = 2.0 * omt_eval * (B.y - A.y) + 2.0 * t_eval * (C.y - B.y);

                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY >= C.y);
                        }
                    } else {
                        is_valid = true;
                    }

                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (deriv_y > 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, -1);
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
            let d = A.y - sampleY;

            let roots = solve_cubic(a, b, c, d);

            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = cubic_root_at(roots, r);
                if (t >= -0.01 && t <= 1.01) {
                    let t_eval = clamp(t, 0.00001, 0.99999);
                    let deriv_y = 3.0 * a * t_eval * t_eval + 2.0 * b * t_eval + c;

                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY < D_pt.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY >= D_pt.y);
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
                        if (deriv_y > 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, -1);
                        }
                    }
                }
            }
        } else if (seg.segmentType == 4u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let weight = bitcast<f32>(seg._pad0);

            // Solve Ny(t) - sampleY*W(t) in the power basis. Positive
            // weights keep W(t) positive throughout the segment.
            let ay = A.y - sampleY;
            let by = B.y - sampleY;
            let cy = C.y - sampleY;
            let a = ay - 2.0 * weight * by + cy;
            let b = 2.0 * (weight * by - ay);
            let c = ay;
            let coefficient_scale = max(max(abs(a), abs(b)), abs(c));
            let roots = solve_quadratic(
                a / max(coefficient_scale, 1.0e-30),
                b / max(coefficient_scale, 1.0e-30),
                c / max(coefficient_scale, 1.0e-30));

            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = quadratic_root_at(roots, r);
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

                    // Preserve the direction-aware half-open intervals exactly:
                    // upward [0,1), downward (0,1].
                    var is_valid = false;
                    if (t < 0.005) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY >= C.y);
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
                        let x_t = (omt * omt * A.x
                            + 2.0 * weight * omt * tc * B.x
                            + tc * tc * C.x) / rational_denominator;
                        if (deriv_y > 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, -1);
                        }
                    }
                }
            }
        } else if (seg.segmentType == 5u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;
            let D_pt = seg.p3;
            let weight1 = bitcast<f32>(seg._pad0);
            let weight2 = bitcast<f32>(seg._pad1);

            // Solve Ny(t)-sampleY*W(t) exactly as one cubic. ABI validation
            // guarantees positive canonical weights, so W(t) stays positive.
            let ay = A.y - sampleY;
            let by = weight1 * (B.y - sampleY);
            let cy = weight2 * (C.y - sampleY);
            let dy = D_pt.y - sampleY;
            let a = -ay + 3.0 * by - 3.0 * cy + dy;
            let b = 3.0 * ay - 6.0 * by + 3.0 * cy;
            let c = -3.0 * ay + 3.0 * by;
            let d = ay;
            let coefficient_scale = max(
                max(abs(a), abs(b)),
                max(abs(c), abs(d)));
            let roots = solve_cubic(
                a / max(coefficient_scale, 1.0e-30),
                b / max(coefficient_scale, 1.0e-30),
                c / max(coefficient_scale, 1.0e-30),
                d / max(coefficient_scale, 1.0e-30));

            // A rational cubic contributes at most three roots. This bound is
            // fixed and independent of path size, zoom, and control geometry.
            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = cubic_root_at(roots, r);
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
                            is_valid = (sampleY >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sampleY < D_pt.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sampleY >= D_pt.y);
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
                        let x_t = (omt2 * omt * A.x
                            + 3.0 * weight1 * omt2 * tc * B.x
                            + 3.0 * weight2 * omt * t2 * C.x
                            + t2 * tc * D_pt.x) / rational_denominator;
                        if (deriv_y > 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, samplePositionsX, x_t, -1);
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

            let dy = sampleY - center.y;

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

                add_arc_crossing(
                    dx1,
                    sampleY,
                    center,
                    rx,
                    ry,
                    theta1,
                    delta_theta,
                    cos_phi,
                    sin_phi,
                    samplePositionsX,
                    &winding);
                add_arc_crossing(
                    dx2,
                    sampleY,
                    center,
                    rx,
                    ry,
                    theta1,
                    delta_theta,
                    cos_phi,
                    sin_phi,
                    samplePositionsX,
                    &winding);
            }
        }
    }

    var covered = 0u;
    covered = covered | select(
        0u, 1u << 0u,
        sampleGrid > 0u &&
            winding_is_inside(winding.low.x, record.fillRule));
    covered = covered | select(
        0u, 1u << 1u,
        sampleGrid > 1u &&
            winding_is_inside(winding.low.y, record.fillRule));
    covered = covered | select(
        0u, 1u << 2u,
        sampleGrid > 2u &&
            winding_is_inside(winding.low.z, record.fillRule));
    covered = covered | select(
        0u, 1u << 3u,
        sampleGrid > 3u &&
            winding_is_inside(winding.low.w, record.fillRule));
    covered = covered | select(
        0u, 1u << 4u,
        sampleGrid > 4u &&
            winding_is_inside(winding.high.x, record.fillRule));
    covered = covered | select(
        0u, 1u << 5u,
        sampleGrid > 5u &&
            winding_is_inside(winding.high.y, record.fillRule));
    covered = covered | select(
        0u, 1u << 6u,
        sampleGrid > 6u &&
            winding_is_inside(winding.high.z, record.fillRule));
    covered = covered | select(
        0u, 1u << 7u,
        sampleGrid > 7u &&
            winding_is_inside(winding.high.w, record.fillRule));
    return covered;
}

fn combine_coverage_masks(maskA: u32, maskB: u32, pathOpKind: u32) -> u32 {
    switch pathOpKind {
        case 1u: { return maskA & ~maskB; }
        case 2u: { return maskA & maskB; }
        case 3u: { return maskA | maskB; }
        case 4u: { return maskA ^ maskB; }
        case 5u: { return maskB & ~maskA; }
        default: { return maskA; }
    }
}

fn boolean_program_row_coverage_mask(
    pixelX: f32,
    sampleY: f32,
    sampleGrid: u32,
    scaleX: f32,
    pathIndex: u32,
    programIndex: u32,
    encodedProgramCount: u32) -> u32 {
    var stack: array<u32, 16>;
    var stackCount = 0u;
    let programCount = encodedProgramCount & ~BOOLEAN_PROGRAM_FLAG;
    for (var instructionIndex = 0u;
         instructionIndex < programCount;
         instructionIndex = instructionIndex + 1u) {
        let token = pathRecords[programIndex + instructionIndex].startSegment;
        if ((token & BOOLEAN_PROGRAM_FLAG) != 0u) {
            if (stackCount < 2u) {
                return 0u;
            }

            let maskB = stack[stackCount - 1u];
            let maskA = stack[stackCount - 2u];
            stackCount = stackCount - 1u;
            stack[stackCount - 1u] = combine_coverage_masks(
                maskA,
                maskB,
                token & BOOLEAN_TOKEN_VALUE_MASK);
        } else {
            if (stackCount >= MAX_BOOLEAN_STACK_DEPTH) {
                return 0u;
            }

            var mask = 0u;
            if (token != BOOLEAN_EMPTY_TOKEN) {
                let record = pathRecords[pathIndex + token];
                mask = row_coverage_mask(
                    pixelX,
                    sampleY,
                    sampleGrid,
                    scaleX,
                    record);
            }
            stack[stackCount] = mask;
            stackCount = stackCount + 1u;
        }
    }

    return select(0u, stack[0], stackCount == 1u);
}

fn path_coverage_byte(x: u32, y: u32, uniforms: PathUniforms) -> u32 {
    let pathIndex = uniforms.pathIndex;
    let record = pathRecords[pathIndex];

    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);

    var coveredSamples = 0u;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let sampleWeight = 1.0 / f32(sampleGrid * sampleGrid);
    for (var sampleY = 0u; sampleY < sampleGrid; sampleY = sampleY + 1u) {
        let samplePositionY = py + (f32(sampleY) + 0.5) / f32(sampleGrid);
        let samplePathY = samplePositionY / uniforms.scaleY;
        var combinedMask = 0u;
        if ((uniforms.pathOpKind & BOOLEAN_PROGRAM_FLAG) != 0u) {
            combinedMask = boolean_program_row_coverage_mask(
                px,
                samplePathY,
                sampleGrid,
                uniforms.scaleX,
                pathIndex,
                uniforms.pathIndexB,
                uniforms.pathOpKind);
        } else {
            let maskA = row_coverage_mask(
                px,
                samplePathY,
                sampleGrid,
                uniforms.scaleX,
                record);
            combinedMask = maskA;
            if (uniforms.pathOpKind != 0u) {
                let recordB = pathRecords[uniforms.pathIndexB];
                let maskB = row_coverage_mask(
                    px,
                    samplePathY,
                    sampleGrid,
                    uniforms.scaleX,
                    recordB);
                combinedMask = combine_coverage_masks(
                    maskA,
                    maskB,
                    uniforms.pathOpKind);
            }
        }
        let validMask = (1u << sampleGrid) - 1u;
        coveredSamples = coveredSamples + countOneBits(combinedMask & validMask);
    }
    return min(255u, u32(round(f32(coveredSamples) * sampleWeight * 255.0)));
}

// Four adjacent pixels are packed by one invocation so the storage buffer has
// the exact byte layout required by WebGPU copyBufferToTexture for R8Unorm.
@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = pathUniforms[global_id.z];
    let wordX = global_id.x;
    let y = global_id.y;
    let firstX = wordX * 4u;
    if (firstX >= uniforms.width || y >= uniforms.height) {
        return;
    }

    var packed = 0u;
    for (var lane = 0u; lane < 4u; lane = lane + 1u) {
        let x = firstX + lane;
        if (x < uniforms.width) {
            packed = packed | (path_coverage_byte(x, y, uniforms) << (lane * 8u));
        }
    }

    coverageOutput[uniforms.outputOffsetWords + y * uniforms.outputRowWords + wordX] = packed;
}
