// Algorithm: Draw retained glyph-outline instances as transformed quads, evaluate analytic non-zero/even-odd winding, and reconstruct a one-device-pixel antialiasing ramp from the nearest contour distance.
// Time complexity: O(P*S + E*S*K), where P is covered fragments, E is edge-band fragments, S is segments in the referenced unique glyph outline, and K is the fixed curve-distance subdivision bound (8 quadratic, 12 cubic).
// Space complexity: O(G+S+I) retained GPU storage for G glyph records, S analytic line/quadratic/cubic segments, and I glyph instances; per-fragment private storage is O(1).
// Quality: fill boundaries use analytic curves and exact winding at every device pixel; only the bounded nearest-distance estimate used for the one-pixel antialiasing ramp samples curve chords. Aliased text uses the exact center winding.
// Winding: quadratic and cubic roots use direction-aware half-open endpoint intervals to avoid transition-vertex double counting.
struct Uniforms {
    projection: mat4x4<f32>,
    mvp: mat4x4<f32>,
    view: mat4x4<f32>,
    canvasSize: vec2<f32>,
    dpiScale: f32,
    pad0: f32,
};

struct PathRecord {
    startSegment: u32,
    segmentCount: u32,
    minX: f32,
    minY: f32,
    maxX: f32,
    maxY: f32,
    fillRule: u32,
    pad1: u32,
};

struct Segment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segmentType: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

struct GlyphInstance {
    transform: mat4x4<f32>,
    color: vec4<f32>,
    minBounds: vec2<f32>,
    maxBounds: vec2<f32>,
    metadata: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var<storage, read> pathRecords: array<PathRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var<storage, read> glyphInstances: array<GlyphInstance>;
@group(1) @binding(0) var maskSampler: sampler;
@group(1) @binding(1) var maskTexture: texture_2d<f32>;

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) localPosition: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) coverageGamma: f32,
    @location(3) @interpolate(flat) recordIndex: u32,
    @location(4) @interpolate(flat) sampleGrid: u32,
};

@vertex
fn vs_main(
    @builtin(vertex_index) vertexIndex: u32,
    @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    let instance = glyphInstances[instanceIndex];
    var corner = vec2<f32>(0.0, 0.0);
    switch vertexIndex {
        case 1u: { corner = vec2<f32>(1.0, 0.0); }
        case 2u, 4u: { corner = vec2<f32>(1.0, 1.0); }
        case 5u: { corner = vec2<f32>(0.0, 1.0); }
        default: {}
    }
    let localPosition = mix(instance.minBounds, instance.maxBounds, corner);
    let modelPosition = instance.transform * vec4<f32>(localPosition, 0.0, 1.0);

    var output: VertexOutput;
    output.position = uniforms.projection * uniforms.mvp * modelPosition;
    output.localPosition = localPosition;
    output.color = instance.color;
    output.coverageGamma = instance.metadata.y;
    output.recordIndex = u32(round(instance.metadata.x));
    output.sampleGrid = u32(round(instance.metadata.z));
    return output;
}

fn solve_quadratic(a: f32, b: f32, c: f32, roots: ptr<function, array<f32, 2>>) -> u32 {
    if (abs(a) < 0.00001) {
        if (abs(b) < 0.00001) { return 0u; }
        (*roots)[0] = -c / b;
        return 1u;
    }
    let discriminant = b * b - 4.0 * a * c;
    if (discriminant < 0.0) { return 0u; }
    if (discriminant == 0.0) {
        (*roots)[0] = -b / (2.0 * a);
        return 1u;
    }
    let sqrtD = sqrt(discriminant);
    (*roots)[0] = (-b - sqrtD) / (2.0 * a);
    (*roots)[1] = (-b + sqrtD) / (2.0 * a);
    return 2u;
}

fn signed_cbrt(value: f32) -> f32 {
    return select(pow(value, 1.0 / 3.0), -pow(-value, 1.0 / 3.0), value < 0.0);
}

fn solve_cubic(aIn: f32, bIn: f32, cIn: f32, dIn: f32, roots: ptr<function, array<f32, 3>>) -> u32 {
    if (abs(aIn) < 0.00001) {
        var quadraticRoots = array<f32, 2>(0.0, 0.0);
        let count = solve_quadratic(bIn, cIn, dIn, &quadraticRoots);
        for (var index = 0u; index < count; index = index + 1u) { (*roots)[index] = quadraticRoots[index]; }
        return count;
    }
    let a = bIn / aIn;
    let b = cIn / aIn;
    let c = dIn / aIn;
    let p = b - a * a / 3.0;
    let q = c - a * b / 3.0 + 2.0 * a * a * a / 27.0;
    let discriminant = q * q / 4.0 + p * p * p / 27.0;
    if (discriminant > 0.0) {
        let sqrtD = sqrt(discriminant);
        (*roots)[0] = signed_cbrt(-q / 2.0 + sqrtD) + signed_cbrt(-q / 2.0 - sqrtD) - a / 3.0;
        return 1u;
    }
    if (p < 0.0) {
        let radius = 2.0 * sqrt(-p / 3.0);
        let value = clamp(-q / (2.0 * sqrt(-p * p * p / 27.0)), -1.0, 1.0);
        let theta = acos(value);
        let pi = 3.14159265359;
        (*roots)[0] = radius * cos(theta / 3.0) - a / 3.0;
        (*roots)[1] = radius * cos((theta + 2.0 * pi) / 3.0) - a / 3.0;
        (*roots)[2] = radius * cos((theta + 4.0 * pi) / 3.0) - a / 3.0;
        return 3u;
    }
    (*roots)[0] = -a / 3.0;
    return 1u;
}

fn root_is_half_open(t: f32, derivativeY: f32) -> bool {
    if (derivativeY > 0.0) { return t >= 0.0 && t < 1.0; }
    if (derivativeY < 0.0) { return t > 0.0 && t <= 1.0; }
    return false;
}

fn winding_and_distance(
    point: vec2<f32>,
    record: PathRecord,
    edgeBand: f32,
    calculateDistance: bool,
    minimumDistance: ptr<function, f32>) -> i32 {
    var winding = 0;
    let endSegment = record.startSegment + record.segmentCount;
    for (var index = record.startSegment; index < endSegment; index = index + 1u) {
        let segment = segments[index];
        if (segment.segmentType == 0u) {
            let derivativeY = segment.p1.y - segment.p0.y;
            if (derivativeY != 0.0) {
                let t = (point.y - segment.p0.y) / derivativeY;
                if (root_is_half_open(t, derivativeY)) {
                    let intersectionX = mix(segment.p0.x, segment.p1.x, t);
                    if (point.x < intersectionX) { winding = winding + select(-1, 1, derivativeY > 0.0); }
                }
            }
        } else if (segment.segmentType == 1u) {
            let a = segment.p0.y - 2.0 * segment.p1.y + segment.p2.y;
            let b = 2.0 * (segment.p1.y - segment.p0.y);
            let c = segment.p0.y - point.y;
            var roots = array<f32, 2>(0.0, 0.0);
            let count = solve_quadratic(a, b, c, &roots);
            for (var rootIndex = 0u; rootIndex < count; rootIndex = rootIndex + 1u) {
                let t = roots[rootIndex];
                let derivativeY = 2.0 * (1.0 - t) * (segment.p1.y - segment.p0.y) + 2.0 * t * (segment.p2.y - segment.p1.y);
                if (root_is_half_open(t, derivativeY)) {
                    let oneMinusT = 1.0 - t;
                    let intersectionX = oneMinusT * oneMinusT * segment.p0.x + 2.0 * oneMinusT * t * segment.p1.x + t * t * segment.p2.x;
                    if (point.x < intersectionX) { winding = winding + select(-1, 1, derivativeY > 0.0); }
                }
            }
        } else if (segment.segmentType == 2u) {
            let a = -segment.p0.y + 3.0 * segment.p1.y - 3.0 * segment.p2.y + segment.p3.y;
            let b = 3.0 * segment.p0.y - 6.0 * segment.p1.y + 3.0 * segment.p2.y;
            let c = -3.0 * segment.p0.y + 3.0 * segment.p1.y;
            let d = segment.p0.y - point.y;
            var roots = array<f32, 3>(0.0, 0.0, 0.0);
            let count = solve_cubic(a, b, c, d, &roots);
            for (var rootIndex = 0u; rootIndex < count; rootIndex = rootIndex + 1u) {
                let t = roots[rootIndex];
                let derivativeY = 3.0 * a * t * t + 2.0 * b * t + c;
                if (root_is_half_open(t, derivativeY)) {
                    let oneMinusT = 1.0 - t;
                    let intersectionX = oneMinusT * oneMinusT * oneMinusT * segment.p0.x
                        + 3.0 * oneMinusT * oneMinusT * t * segment.p1.x
                        + 3.0 * oneMinusT * t * t * segment.p2.x
                        + t * t * t * segment.p3.x;
                    if (point.x < intersectionX) { winding = winding + select(-1, 1, derivativeY > 0.0); }
                }
            }
        }

        if (calculateDistance) {
            if (segment.segmentType == 0u) {
                let boxMin = min(segment.p0, segment.p1);
                let boxMax = max(segment.p0, segment.p1);
                if (distance_to_aabb(point, boxMin, boxMax) <= edgeBand) {
                    *minimumDistance = min(*minimumDistance, distance_to_line(point, segment.p0, segment.p1));
                }
            } else {
                var boxMin = min(segment.p0, segment.p1);
                var boxMax = max(segment.p0, segment.p1);
                boxMin = min(boxMin, segment.p2);
                boxMax = max(boxMax, segment.p2);
                if (segment.segmentType == 2u) {
                    boxMin = min(boxMin, segment.p3);
                    boxMax = max(boxMax, segment.p3);
                }
                if (distance_to_aabb(point, boxMin, boxMax) <= edgeBand) {
                    let subdivisionCount = select(8u, 12u, segment.segmentType == 2u);
                    var previous = segment.p0;
                    for (var subdivision = 1u; subdivision <= subdivisionCount; subdivision = subdivision + 1u) {
                        let current = evaluate_segment(segment, f32(subdivision) / f32(subdivisionCount));
                        *minimumDistance = min(*minimumDistance, distance_to_line(point, previous, current));
                        previous = current;
                    }
                }
            }
        }
    }
    return winding;
}

fn distance_to_line(point: vec2<f32>, start: vec2<f32>, end: vec2<f32>) -> f32 {
    let edge = end - start;
    let lengthSquared = dot(edge, edge);
    if (lengthSquared <= 0.0000001) { return distance(point, start); }
    let t = clamp(dot(point - start, edge) / lengthSquared, 0.0, 1.0);
    return distance(point, start + edge * t);
}

fn distance_to_aabb(point: vec2<f32>, minimum: vec2<f32>, maximum: vec2<f32>) -> f32 {
    let delta = max(minimum - point, point - maximum);
    let outside = max(delta, vec2<f32>(0.0));
    return length(outside);
}

fn evaluate_segment(segment: Segment, t: f32) -> vec2<f32> {
    if (segment.segmentType == 1u) {
        let oneMinusT = 1.0 - t;
        return oneMinusT * oneMinusT * segment.p0
            + 2.0 * oneMinusT * t * segment.p1
            + t * t * segment.p2;
    }
    let oneMinusT = 1.0 - t;
    return oneMinusT * oneMinusT * oneMinusT * segment.p0
        + 3.0 * oneMinusT * oneMinusT * t * segment.p1
        + 3.0 * oneMinusT * t * t * segment.p2
        + t * t * t * segment.p3;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let record = pathRecords[input.recordIndex];
    let localDx = dpdx(input.localPosition);
    let localDy = dpdy(input.localPosition);
    let grid = max(1u, input.sampleGrid);
    let localPixelSize = max(0.000001, max(length(localDx), length(localDy)));
    let edgeBand = 1.5 * localPixelSize;
    var contourDistance = edgeBand + 1.0;
    let centerWinding = winding_and_distance(
        input.localPosition,
        record,
        edgeBand,
        grid > 1u,
        &contourDistance);
    let centerInside = select(centerWinding != 0, (abs(centerWinding) & 1) != 0, record.fillRule == 1u);
    var rawCoverage = select(0.0, 1.0, centerInside);
    if (grid > 1u) {
        // The maximum local displacement represented by one screen pixel is a
        // conservative metric for rotated, sheared, and anisotropic instances.
        if (contourDistance <= edgeBand) {
            let signedDeviceDistance = contourDistance / localPixelSize * select(1.0, -1.0, centerInside);
            rawCoverage = 1.0 - smoothstep(-0.5, 0.5, signedDeviceDistance);
        }
    }
    let coverage = select(
        pow(clamp(rawCoverage * 1.15, 0.0, 1.0), input.coverageGamma),
        select(0.0, 1.0, rawCoverage >= 0.5),
        grid == 1u);
    if (coverage <= 0.0) { discard; }

    let maskUv = input.position.xy / uniforms.canvasSize;
    let maskAlpha = textureSample(maskTexture, maskSampler, maskUv).r;
    let alpha = input.color.a * coverage * maskAlpha;
    return vec4<f32>(input.color.rgb * alpha, alpha);
}
