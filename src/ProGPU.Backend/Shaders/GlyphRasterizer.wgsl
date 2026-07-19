// Algorithm: Rasterize glyph coverage with 8x8 supersampling, sharing analytic line, quadratic, and cubic winding intersections across each eight-sample row.
// Time complexity: O(R*S + A) per texel for R=8 sample rows, A=64 anti-aliasing samples, and S outline segments.
// Space complexity: O(R) private winding storage plus O(S) read-only segment bandwidth and one rgba8unorm output write per texel.
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


fn accumulate_crossing(
    intersect_x: f32,
    direction: i32,
    sample_xs: ptr<function, array<f32, 8>>,
    windings: ptr<function, array<i32, 8>>
) {
    for (var sample_x = 0u; sample_x < 8u; sample_x = sample_x + 1u) {
        if ((*sample_xs)[sample_x] < intersect_x) {
            (*windings)[sample_x] = (*windings)[sample_x] + direction;
        }
    }
}

fn accumulate_winding_row(
    sample_y: f32,
    sample_xs: ptr<function, array<f32, 8>>,
    record: GlyphRecord,
    windings: ptr<function, array<i32, 8>>
) {
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
            if (A.y <= sample_y) {
                if (B.y > sample_y) { // Upward crossing
                    let t = (sample_y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    accumulate_crossing(intersectX, 1, sample_xs, windings);
                }
            } else {
                if (B.y <= sample_y) { // Downward crossing
                    let t = (sample_y - A.y) / (B.y - A.y);
                    let intersectX = A.x + t * (B.x - A.x);
                    accumulate_crossing(intersectX, -1, sample_xs, windings);
                }
            }
        } else if (seg.segmentType == 1u) {
            // Quadratic Bezier from A to C with control point B
            let A = seg.p0;
            let B = seg.p1;
            let C = seg.p2;

            let a = A.y - 2.0 * B.y + C.y;
            let b = 2.0 * (B.y - A.y);
            let c = A.y - sample_y;

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
                            is_valid = (sample_y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sample_y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sample_y < C.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sample_y >= C.y);
                        }
                    } else {
                        is_valid = true;
                    }

                    if (is_valid) {
                        let tc = clamp(t, 0.0, 1.0);
                        let omt = 1.0 - tc;
                        let x_t = omt * omt * A.x + 2.0 * omt * tc * B.x + tc * tc * C.x;
                        if (deriv_y > 0.0) {
                            accumulate_crossing(x_t, 1, sample_xs, windings);
                        } else if (deriv_y < 0.0) {
                            accumulate_crossing(x_t, -1, sample_xs, windings);
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
            let d = A.y - sample_y;

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
                            is_valid = (sample_y >= A.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sample_y < A.y);
                        }
                    } else if (t > 0.995) {
                        if (deriv_y > 0.0) {
                            is_valid = (sample_y < D_pt.y);
                        } else if (deriv_y < 0.0) {
                            is_valid = (sample_y >= D_pt.y);
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
                            accumulate_crossing(x_t, 1, sample_xs, windings);
                        } else if (deriv_y < 0.0) {
                            accumulate_crossing(x_t, -1, sample_xs, windings);
                        }
                    }
                }
            }
        }
    }
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

    var sample_xs = array<f32, 8>();
    for (var sample_x = 0u; sample_x < 8u; sample_x = sample_x + 1u) {
        let dx = 0.0625 + f32(sample_x) * 0.125;
        sample_xs[sample_x] = (px + dx - uniforms.subpixelX) / uniforms.scale;
    }
    var covered_samples = 0u;

    // Fixed 8x8 sampling matches the previous quality policy exactly. Curve roots
    // depend on sample y, not sample x, so one row traversal serves all eight x taps.
    for (var sample_y = 0u; sample_y < 8u; sample_y = sample_y + 1u) {
        let dy = 0.0625 + f32(sample_y) * 0.125;
        let glyph_y = -(py + dy) / uniforms.scale;
        var windings = array<i32, 8>(0, 0, 0, 0, 0, 0, 0, 0);
        accumulate_winding_row(glyph_y, &sample_xs, record, &windings);
        for (var sample_x = 0u; sample_x < 8u; sample_x = sample_x + 1u) {
            if (windings[sample_x] != 0) {
                covered_samples = covered_samples + 1u;
            }
        }
    }

    let coverage = f32(covered_samples) * 0.015625;
    let writeCoord = vec2<u32>(uniforms.atlasX + x, uniforms.atlasY + y);
    textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));
}
