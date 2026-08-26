// Algorithm: Rasterize glyph coverage with 8x8 supersampling, sharing analytic line, quadratic, and cubic winding intersections across each eight-sample row.
// Time complexity: O(R*S + A) per texel for R=8 sample rows, A=64 anti-aliasing samples, and S outline segments.
// Space complexity: O(R) fixed vector-lane winding storage plus O(S) read-only segment bandwidth and one packed u32 output write per four R8 texels; no dynamically indexed private arrays are used.
struct GlyphUniforms {
    xStart: f32,
    yStart: f32,
    scale: f32,
    glyphIndex: u32,
    outputOffsetWords: u32,
    outputRowWords: u32,
    width: u32,
    height: u32,
    subpixelX: f32,
    atlasX: f32,
    atlasY: f32,
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

fn glyph_sample_x(pixel_x: f32, lane: u32) -> f32 {
    let dx = 0.0625 + f32(lane) * 0.125;
    return (
        pixel_x + dx - uniforms.subpixelX) /
        uniforms.scale;
}

fn accumulate_crossing(
    intersect_x: f32,
    direction: i32,
    sample_xs: SampleRow,
    windings: ptr<function, WindingRow>
) {
    let direction4 = vec4<i32>(direction);
    (*windings).low = (*windings).low + select(
        vec4<i32>(0),
        direction4,
        sample_xs.low < vec4<f32>(intersect_x));
    (*windings).high = (*windings).high + select(
        vec4<i32>(0),
        direction4,
        sample_xs.high < vec4<f32>(intersect_x));
}

fn accumulate_winding_row(
    sample_y: f32,
    sample_xs: SampleRow,
    record: GlyphRecord,
    windings: ptr<function, WindingRow>
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

            let roots = solve_quadratic(a, b, c);

            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = quadratic_root_at(roots, r);
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

            let roots = solve_cubic(a, b, c, d);

            for (var r: u32 = 0u; r < roots.count; r = r + 1u) {
                let t = cubic_root_at(roots, r);
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

fn glyph_coverage_byte(x: u32, y: u32) -> u32 {
    let glyphIndex = uniforms.glyphIndex;
    let record = glyphRecords[glyphIndex];

    let px = uniforms.xStart + f32(x);
    let py = uniforms.yStart + f32(y);

    let sample_xs = SampleRow(
        vec4<f32>(
            glyph_sample_x(px, 0u),
            glyph_sample_x(px, 1u),
            glyph_sample_x(px, 2u),
            glyph_sample_x(px, 3u)),
        vec4<f32>(
            glyph_sample_x(px, 4u),
            glyph_sample_x(px, 5u),
            glyph_sample_x(px, 6u),
            glyph_sample_x(px, 7u)));
    var covered_samples = 0u;

    // Fixed 8x8 sampling matches the previous quality policy exactly. Curve roots
    // depend on sample y, not sample x, so one row traversal serves all eight x taps.
    for (var sample_y = 0u; sample_y < 8u; sample_y = sample_y + 1u) {
        let dy = 0.0625 + f32(sample_y) * 0.125;
        let glyph_y = -(py + dy) / uniforms.scale;
        var windings = WindingRow(vec4<i32>(0), vec4<i32>(0));
        accumulate_winding_row(glyph_y, sample_xs, record, &windings);
        let low_coverage = select(
            vec4<u32>(0),
            vec4<u32>(1),
            windings.low != vec4<i32>(0));
        let high_coverage = select(
            vec4<u32>(0),
            vec4<u32>(1),
            windings.high != vec4<i32>(0));
        covered_samples = covered_samples +
            low_coverage.x + low_coverage.y +
            low_coverage.z + low_coverage.w +
            high_coverage.x + high_coverage.y +
            high_coverage.z + high_coverage.w;
    }

    return min(255u, u32(round(f32(covered_samples) * 3.984375)));
}

// Four adjacent pixels are packed by one invocation so the storage buffer has
// the exact byte layout required by WebGPU copyBufferToTexture for R8Unorm.
@compute @workgroup_size(16, 16)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
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
            packed = packed | (glyph_coverage_byte(x, y) << (lane * 8u));
        }
    }

    coverageOutput[uniforms.outputOffsetWords + y * uniforms.outputRowWords + wordX] = packed;
}

// The fragment entry is the same per-pixel algorithm without compute-only
// storage writes. A three-vertex viewport-local triangle writes coverage
// directly into the retained R8 atlas on adapters whose compute profile is
// rejected, avoiding CPU readback, repacking, and upload.
@vertex
fn vs_raster_fallback(@builtin(vertex_index) vertex_index: u32) ->
    @builtin(position) vec4<f32> {
    var position = vec2<f32>(-1.0, -1.0);
    if (vertex_index == 1u) {
        position = vec2<f32>(3.0, -1.0);
    } else if (vertex_index == 2u) {
        position = vec2<f32>(-1.0, 3.0);
    }
    return vec4<f32>(position, 0.0, 1.0);
}

@fragment
fn fs_raster_fallback(@builtin(position) position: vec4<f32>) ->
    @location(0) vec4<f32> {
    let local_x = u32(position.x - uniforms.atlasX);
    let local_y = u32(position.y - uniforms.atlasY);
    let coverage = f32(glyph_coverage_byte(local_x, local_y)) / 255.0;
    return vec4<f32>(coverage, 0.0, 0.0, 1.0);
}
