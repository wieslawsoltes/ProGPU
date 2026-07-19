// Algorithm: Rasterize arbitrary path coverage by supersampling each atlas texel and applying analytic non-zero or even-odd winding tests.
// Time complexity: O(A*S) per texel for A anti-aliasing samples and S path segments.
// Space complexity: O(1) local storage plus O(S) read-only segment bandwidth.
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
    sampleGrid: u32,
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

@group(0) @binding(0) var<storage, read> pathUniforms: array<PathUniforms>;
@group(0) @binding(1) var<storage, read> pathRecords: array<PathRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var atlasTexture: texture_storage_2d<rgba8unorm, write>;


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


fn add_crossing(
    winding: ptr<function, array<i32, 8>>,
    samplePositionsX: ptr<function, array<f32, 8>>,
    sampleGrid: u32,
    intersectX: f32,
    direction: i32) {
    for (var sampleX = 0u; sampleX < sampleGrid; sampleX = sampleX + 1u) {
        if ((*samplePositionsX)[sampleX] < intersectX) {
            (*winding)[sampleX] = (*winding)[sampleX] + direction;
        }
    }
}

fn count_row_coverage(
    pixelX: f32,
    sampleY: f32,
    sampleGrid: u32,
    scaleX: f32,
    record: PathRecord) -> u32 {
    var winding = array<i32, 8>(0, 0, 0, 0, 0, 0, 0, 0);
    var samplePositionsX = array<f32, 8>(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    for (var sampleX = 0u; sampleX < sampleGrid; sampleX = sampleX + 1u) {
        let sampleOffsetX = (f32(sampleX) + 0.5) / f32(sampleGrid);
        samplePositionsX[sampleX] = (pixelX + sampleOffsetX) / scaleX;
    }

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
                    add_crossing(&winding, &samplePositionsX, sampleGrid, intersectX, 1);
                }
            } else {
                if (B.y <= sampleY) {
                    let t = (sampleY - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    add_crossing(&winding, &samplePositionsX, sampleGrid, intersectX, -1);
                }
            }
        } else if (seg.segmentType == 1u) {
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;

            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - sampleY;

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
                            add_crossing(&winding, &samplePositionsX, sampleGrid, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, &samplePositionsX, sampleGrid, x_t, -1);
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
                            add_crossing(&winding, &samplePositionsX, sampleGrid, x_t, 1);
                        } else if (deriv_y < 0.0) {
                            add_crossing(&winding, &samplePositionsX, sampleGrid, x_t, -1);
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

                var roots = array<f32, 2>(dx1, dx2);
                for (var r_idx: u32 = 0u; r_idx < 2u; r_idx = r_idx + 1u) {
                    let dx = roots[r_idx];
                    let intersectX = center.x + dx;
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

                    if (deriv_y > 0.0 && t >= 0.0 && t < 1.0) {
                        add_crossing(&winding, &samplePositionsX, sampleGrid, intersectX, 1);
                    } else if (deriv_y < 0.0 && t > 0.0 && t <= 1.0) {
                        add_crossing(&winding, &samplePositionsX, sampleGrid, intersectX, -1);
                    }
                }
            }
        }
    }

    var covered = 0u;
    for (var sampleX = 0u; sampleX < sampleGrid; sampleX = sampleX + 1u) {
        let isInside = select(
            winding[sampleX] != 0,
            abs(winding[sampleX]) % 2 == 1,
            record.fillRule == 0u);
        covered = covered + select(0u, 1u, isInside);
    }
    return covered;
}

@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let uniforms = pathUniforms[global_id.z];
    let x = global_id.x;
    let y = global_id.y;

    if (x >= uniforms.width || y >= uniforms.height) {
        return;
    }

    let pathIndex = uniforms.pathIndex;
    let record = pathRecords[pathIndex];

    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);

    var coveredSamples = 0u;
    let sampleGrid = clamp(uniforms.sampleGrid, 1u, 8u);
    let sampleWeight = 1.0 / f32(sampleGrid * sampleGrid);
    for (var sampleY = 0u; sampleY < sampleGrid; sampleY = sampleY + 1u) {
        let samplePositionY = py + (f32(sampleY) + 0.5) / f32(sampleGrid);
        coveredSamples = coveredSamples + count_row_coverage(
            px,
            samplePositionY / uniforms.scaleY,
            sampleGrid,
            uniforms.scaleX,
            record);
    }
    let coverage = f32(coveredSamples) * sampleWeight;

    let writeCoord = vec2<u32>(uniforms.atlasX + x, uniforms.atlasY + y);
    textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));
}
