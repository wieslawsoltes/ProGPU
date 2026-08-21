// Algorithm: Classify pairwise path-segment intersections and emit split records used to construct boolean path-operation contours.
// Time complexity: O(A*B) total for A subject and B clip segments, with bounded analytic work per pair.
// Space complexity: O(I) output storage for I emitted intersection/split records and O(1) local storage per invocation.
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
