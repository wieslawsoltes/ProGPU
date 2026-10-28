// Algorithm: Original ProGPU binary contour scan conversion with sorted winding
// crossings and two ordered horizontal/vertical dropout passes.
// Time complexity: O((W+H)*512*log(512)^2 + P*(log(512)+S) + D*S), where
// W/H are glyph dimensions, P scanned pixels, S <= 256 segments and D dropouts.
// Smart curved dropouts add at most two 32-step subdivisions per dropout.
// Space complexity: 10 KiB workgroup storage and two packed R8 W*H output buffers;
// no CPU bitmap or readback. No foreign rasterizer code.
// One 256-thread workgroup owns a scanline. At most 256 original line/quadratic
// segments yield 512 crossings, sorted in shared memory before a prefix winding
// scan. Horizontal and vertical entry points MUST be separate ordered dispatches.
// The immutable horizontal result prevents dropout read/write scheduling races.
// Preparation admits physical scale 1, phase 0 and validated contour metadata.
// Work: O((W+H)*(512*log(512)^2 + P*log(512)) + D*S), with additional O(P*S)
// exact flat-boundary checks; shared storage 10 KiB, no CPU readback or bitmap.
// This explicit candidate is not Microsoft/source pixel qualification.

struct GlyphUniforms {
    xStart: f32, yStart: f32, scale: f32, glyphIndex: u32,
    outputOffsetWords: u32, outputRowWords: u32, width: u32, height: u32,
    subpixelX: f32, atlasX: f32, atlasY: f32, _pad2: f32,
};
struct GlyphRecord {
    startSegment: u32, segmentCount: u32,
    minX: f32, minY: f32, maxX: f32, maxY: f32, _pad0: u32, _pad1: u32,
};
struct Segment {
    p0: vec2<f32>, p1: vec2<f32>, p2: vec2<f32>, p3: vec2<f32>,
    segmentType: u32, scanDescriptor: u32, contourStart: u32, contourCount: u32,
};
struct Crossing { x: f32, delta: i32, parameter: f32 };
struct Crossings { x: vec2<f32>, delta: vec2<i32>, parameter: vec2<f32> };
struct Event { x: f32, delta: i32, source: u32, order: u32 };

@group(0) @binding(0) var<uniform> uniforms: GlyphUniforms;
@group(0) @binding(1) var<storage, read> glyphRecords: array<GlyphRecord>;
@group(0) @binding(2) var<storage, read> segments: array<Segment>;
@group(0) @binding(3) var<storage, read_write> output: array<atomic<u32>>;
@group(0) @binding(4) var<storage, read_write> horizontal: array<atomic<u32>>;

var<workgroup> events: array<Event, 512>;
var<workgroup> winding: array<i32, 512>;

fn axis_point(p: vec2<f32>, vertical: bool) -> vec2<f32> {
    return select(p, p.yx, vertical);
}

fn quadratic_at(a: f32, b: f32, c: f32, t: f32) -> f32 {
    let u = 1.0 - t;
    return (u * u) * a + (2.0 * u * t) * b + (t * t) * c;
}

fn quadratic_crossing(p0: vec2<f32>, p1: vec2<f32>, p2: vec2<f32>,
    a: f32, b: f32, t: f32) -> Crossing {
    var derivative = 2.0 * a * t + b;
    // An endpoint can have zero first derivative while its one-sided curve
    // still crosses the lower scan boundary. Interior extrema do not cross.
    if derivative == 0.0 && a != 0.0 {
        if t == 0.0 { derivative = a; }
        else if t == 1.0 { derivative = -a; }
    }
    let admitted = select((t > 0.0 && t <= 1.0), (t >= 0.0 && t < 1.0), (derivative > 0.0));
    if admitted && derivative != 0.0 {
        return Crossing(quadratic_at(p0.x, p1.x, p2.x, t),
            select(-1, 1, derivative > 0.0), t);
    }
    return Crossing(3.0e38, 0, -1.0);
}

// Exact degree and endpoint admission. No epsilon changes a retained curve or
// turns an interior tangent into a crossing. Endpoint ownership follows the
// lower-inclusive, upper-exclusive scan interval, independently of edge direction.
fn crossings(segment: Segment, level: f32, vertical: bool) -> Crossings {
    let p0 = axis_point(segment.p0, vertical);
    let p1 = axis_point(segment.p1, vertical);
    let p2 = axis_point(segment.p2, vertical);
    var result = Crossings(vec2<f32>(3.0e38), vec2<i32>(0), vec2<f32>(-1.0));
    if segment.segmentType == 0u {
        let dy = p1.y - p0.y;
        if dy == 0.0 { return result; }
        let t = (level - p0.y) / dy;
        if select((t > 0.0 && t <= 1.0), (t >= 0.0 && t < 1.0), (dy > 0.0)) {
            result.x.x = p0.x + t * (p1.x - p0.x);
            result.delta.x = select(-1, 1, dy > 0.0);
            result.parameter.x = t;
        }
        return result;
    }
    let a = p0.y - 2.0 * p1.y + p2.y;
    let b = 2.0 * (p1.y - p0.y);
    let c = p0.y - level;
    var roots = vec2<f32>(-1.0);
    if a == 0.0 {
        if b != 0.0 { roots.x = -c / b; }
    } else {
        let discriminant = b * b - 4.0 * a * c;
        if discriminant > 0.0 {
            let d = sqrt(discriminant);
            // Stable quadratic formula; both original roots remain distinct.
            let q = -0.5 * (b + select(-d, d, b >= 0.0));
            roots = vec2<f32>(q / a, c / q);
        } else if discriminant == 0.0 {
            roots.x = -b / (2.0 * a);
        }
    }
    // FXC cannot dynamically address vector components as l-values. Resolve
    // the same two roots in order without a lane loop or indexed destination.
    let first = quadratic_crossing(p0, p1, p2, a, b, roots.x);
    let second = quadratic_crossing(p0, p1, p2, a, b, roots.y);
    return Crossings(vec2<f32>(first.x, second.x), vec2<i32>(first.delta, second.delta),
        vec2<f32>(first.parameter, second.parameter));
}

fn event_less(a: Event, b: Event) -> bool {
    return a.x < b.x || (a.x == b.x && a.order < b.order);
}

fn prepare_scan(record: GlyphRecord, level: f32, vertical: bool, lane: u32) {
    var roots = Crossings(vec2<f32>(3.0e38), vec2<i32>(0), vec2<f32>(-1.0));
    if lane < record.segmentCount {
        roots = crossings(segments[record.startSegment + lane], level, vertical);
    }
    events[lane * 2u] = Event(roots.x.x, roots.delta.x, lane, lane * 2u);
    events[lane * 2u + 1u] = Event(roots.x.y, roots.delta.y, lane, lane * 2u + 1u);
    workgroupBarrier();
    for (var size = 2u; size <= 512u; size *= 2u) {
        for (var distance = size / 2u; distance > 0u; distance /= 2u) {
            // Each lane owns one disjoint pair at this sorting stage.
            let left = (lane / distance) * (2u * distance) + lane % distance;
            let right = left + distance;
            let a = events[left];
            let b = events[right];
            let ascending = (left & size) == 0u;
            if select(event_less(a, b), event_less(b, a), ascending) {
                events[left] = b;
                events[right] = a;
            }
            workgroupBarrier();
        }
    }
    winding[lane] = events[lane].delta;
    winding[lane + 256u] = events[lane + 256u].delta;
    workgroupBarrier();
    for (var distance = 1u; distance < 512u; distance *= 2u) {
        var low = winding[lane];
        var high = winding[lane + 256u];
        if lane >= distance { low += winding[lane - distance]; }
        if lane + 256u >= distance { high += winding[lane + 256u - distance]; }
        workgroupBarrier();
        winding[lane] = low;
        winding[lane + 256u] = high;
        workgroupBarrier();
    }
}

fn raster_bounds(record: GlyphRecord) -> vec4<f32> {
    let low = ceil(vec2<f32>(record.minX, record.minY) - vec2<f32>(0.5));
    let high = max(low + vec2<f32>(1.0), floor(vec2<f32>(record.maxX, record.maxY) + vec2<f32>(0.5)));
    return vec4<f32>(low, high);
}

fn in_bounds(point: vec2<f32>, bounds: vec4<f32>) -> bool {
    return all(point >= bounds.xy) && all(point < bounds.zw);
}

fn on_flat_boundary(record: GlyphRecord, x: f32, level: f32, vertical: bool) -> bool {
    for (var i = 0u; i < record.segmentCount; i++) {
        let segment = segments[record.startSegment + i];
        let a = axis_point(segment.p0, vertical);
        let b = axis_point(segment.p1, vertical);
        let c = axis_point(segment.p2, vertical);
        if a.y != level || b.y != level { continue; }
        var low = min(a.x, b.x);
        var high = max(a.x, b.x);
        if segment.segmentType != 0u {
            if c.y != level { continue; }
            low = min(a.x, c.x);
            high = max(a.x, c.x);
            let denominator = a.x - 2.0 * b.x + c.x;
            if denominator != 0.0 {
                let t = (a.x - b.x) / denominator;
                if t > 0.0 && t < 1.0 {
                    let extreme = quadratic_at(a.x, b.x, c.x, t);
                    low = min(low, extreme);
                    high = max(high, extreme);
                }
            }
        }
        if x >= low && x <= high { return true; }
    }
    return false;
}

fn base_covered(record: GlyphRecord, x: f32, level: f32, vertical: bool) -> bool {
    let point = axis_point(vec2<f32>(x, level), vertical);
    if !in_bounds(point, raster_bounds(record)) { return false; }
    var low = 0u;
    var high = 512u;
    while low < high {
        let middle = (low + high) / 2u;
        if events[middle].x < x { low = middle + 1u; }
        else { high = middle; }
    }
    // An exact nondegenerate quadratic crossing retains the closed curve
    // boundary. Straight/flat boundaries keep their independent admission.
    // No epsilon or antialiasing threshold participates in this decision.
    if low < 512u && events[low].x == x {
        let edge = segments[record.startSegment + events[low].source];
        let a = edge.p1 - edge.p0;
        let b = edge.p2 - edge.p0;
        return edge.segmentType == 1u && a.x * b.y != a.y * b.x;
    }
    return low > 0u && winding[low - 1u] != 0 && !on_flat_boundary(record, x, level, vertical);
}

fn pixel_address(point: vec2<f32>) -> vec2<u32> {
    let x = u32(floor(point.x) - uniforms.xStart);
    let y = u32(-floor(point.y) - 1.0 - uniforms.yStart);
    return vec2<u32>(uniforms.outputOffsetWords + y * uniforms.outputRowWords + x / 4u, (x % 4u) * 8u);
}

fn paint(point: vec2<f32>, first_pass: bool) {
    let address = pixel_address(point);
    let bits = 255u << address.y;
    atomicOr(&output[address.x], bits);
    if first_pass { atomicOr(&horizontal[address.x], bits); }
}

fn horizontal_covered(point: vec2<f32>, record: GlyphRecord) -> bool {
    if !in_bounds(point, raster_bounds(record)) { return false; }
    let address = pixel_address(point);
    return ((atomicLoad(&horizontal[address.x]) >> address.y) & 255u) != 0u;
}

// Examine the other three sides of a unit square. A contour which leaves through
// a shared-edge corner still intersects a side. Count distinct positions, so an
// endpoint observed by two adjacent edges is not two continuation crossings.
fn continuation_crossings(record: GlyphRecord, left: f32, level: f32, direction: f32, vertical: bool) -> u32 {
    let opposite = level + direction;
    let bottom = min(level, opposite);
    let top = max(level, opposite);
    var count = 0u;
    var first = vec2<f32>(0.0);
    for (var i = 0u; i < record.segmentCount; i++) {
        let segment = segments[record.startSegment + i];
        let across = crossings(segment, opposite, vertical);
        let side0 = crossings(segment, left, !vertical);
        let side1 = crossings(segment, left + 1.0, !vertical);
        if across.delta.x != 0 && across.x.x >= left && across.x.x <= left + 1.0 {
            let position = vec2<f32>(across.x.x, opposite);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
        if side0.delta.x != 0 && side0.x.x >= bottom && side0.x.x <= top {
            let position = vec2<f32>(left, side0.x.x);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
        if side1.delta.x != 0 && side1.x.x >= bottom && side1.x.x <= top {
            let position = vec2<f32>(left + 1.0, side1.x.x);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
        if across.delta.y != 0 && across.x.y >= left && across.x.y <= left + 1.0 {
            let position = vec2<f32>(across.x.y, opposite);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
        if side0.delta.y != 0 && side0.x.y >= bottom && side0.x.y <= top {
            let position = vec2<f32>(left, side0.x.y);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
        if side1.delta.y != 0 && side1.x.y >= bottom && side1.x.y <= top {
            let position = vec2<f32>(left + 1.0, side1.x.y);
            if count == 0u { first = position; count = 1u; }
            else if any(first != position) { return 2u; }
        }
    }
    return count;
}

fn segment_end(segment: Segment) -> vec2<f32> {
    return select(segment.p2, segment.p1, segment.segmentType == 0u);
}

// The original orthogonal side pair keeps its finite flat cap. A nonorthogonal
// pair can also end at the exact pixel-center side of the unit square: retain
// that closed boundary contact, without widening it into a fractional tolerance.
// Both cases require the actual connecting edge and original contour endpoints.
fn has_flat_cap(record: GlyphRecord, beginning: Event, finish: Event,
    left: f32, level: f32, direction: f32, vertical: bool) -> bool {
    let a = segments[record.startSegment + beginning.source];
    let b = segments[record.startSegment + finish.source];
    if beginning.source == finish.source || a.contourStart != b.contourStart { return false; }
    let orthogonal = a.segmentType == 0u && b.segmentType == 0u &&
        axis_point(a.p0, vertical).x == axis_point(a.p1, vertical).x &&
        axis_point(b.p0, vertical).x == axis_point(b.p1, vertical).x;
    for (var i = 0u; i < a.contourCount; i++) {
        let cap = segments[record.startSegment + a.contourStart + i];
        if cap.segmentType != 0u { continue; }
        let p = axis_point(cap.p0, vertical);
        let q = axis_point(cap.p1, vertical);
        let distance = (p.y - level) * direction;
        if p.y != q.y || p.x == q.x || distance <= 0.0 || distance > 1.0 { continue; }
        let boundary_contact = min(p.x, q.x) >= left && max(p.x, q.x) <= left + 1.0 &&
            (p.x == left || q.x == left || p.x == left + 1.0 || q.x == left + 1.0);
        if !orthogonal && !boundary_contact { continue; }
        if (all(segment_end(a) == cap.p0) && all(b.p0 == cap.p1)) ||
           (all(segment_end(b) == cap.p0) && all(a.p0 == cap.p1)) { return true; }
    }
    return false;
}

// A with-stubs scan may touch an exact original contour vertex without a
// positive winding interval. For a quadratic endpoint, its first nonzero
// one-sided polynomial coefficient determines which side the curve occupies.
// Keep interior curve extrema, flat edges and stub-excluding modes separate.
fn vertex_dropout(record: GlyphRecord, index: u32, level: f32, vertical: bool) {
    if index >= record.segmentCount { return; }
    let segment = segments[record.startSegment + index];
    let mode = segment.scanDescriptor & 7u;
    if mode != 0u && mode != 4u { return; }
    let previous_index = select(index - 1u, segment.contourStart + segment.contourCount - 1u,
        index == segment.contourStart);
    let previous = segments[record.startSegment + previous_index];
    let point = axis_point(segment.p0, vertical);
    var before = axis_point(previous.p0, vertical);
    if previous.segmentType == 1u {
        let control = axis_point(previous.p1, vertical);
        if control.y != point.y { before = control; }
    }
    var after = axis_point(segment.p1, vertical);
    if segment.segmentType == 1u && after.y == point.y {
        after = axis_point(segment.p2, vertical);
    }
    if point.y != level || !((before.y < level && after.y < level) ||
        (before.y > level && after.y > level)) { return; }
    let left = floor(point.x - 0.5);
    if point.x == left + 0.5 { return; }
    let a = axis_point(vec2<f32>(left + 0.5, level), vertical);
    let b = axis_point(vec2<f32>(left + 1.5, level), vertical);
    if vertical {
        if horizontal_covered(a, record) || horizontal_covered(b, record) { return; }
    } else {
        if base_covered(record, left + 0.5, level, false) ||
           base_covered(record, left + 1.5, level, false) { return; }
    }
    let selected = select(left, left + 1.0, mode == 4u && point.x > left + 1.0);
    let bounds = raster_bounds(record);
    let candidate = axis_point(vec2<f32>(selected + 0.5, level), vertical);
    paint(clamp(candidate, bounds.xy + vec2<f32>(0.5), bounds.zw - vec2<f32>(0.5)), !vertical);
}

// Resolve only the selected original root through bounded de Casteljau
// subdivision in increasing scan-axis direction. Stop once the control hull
// spans at most one 26.6 unit, retaining the lower-parameter endpoint's unit.
// Exact endpoint/dyadic roots retain their original coordinate. Base winding
// and gap admission continue to use independent analytic intersections.
fn smart_curve_crossing(record: GlyphRecord, event: Event, level: f32, vertical: bool) -> f32 {
    let segment = segments[record.startSegment + event.source];
    if segment.segmentType == 0u { return event.x; }
    let original = crossings(segment, level, vertical);
    var t = original.parameter[event.order % 2u];
    var a = axis_point(segment.p0, vertical);
    var b = axis_point(segment.p1, vertical);
    var c = axis_point(segment.p2, vertical);
    if event.delta < 0 {
        let swap = a; a = c; c = swap; t = 1.0 - t;
    }
    for (var depth = 0u; depth < 32u; depth++) {
        if t == 0.0 { return a.x; }
        if t == 1.0 { return c.x; }
        if max(a.x, max(b.x, c.x)) - min(a.x, min(b.x, c.x)) <= 1.0 / 64.0 {
            return floor(a.x * 64.0) / 64.0;
        }
        let ab = (a + b) * 0.5;
        let bc = (b + c) * 0.5;
        let middle = (ab + bc) * 0.5;
        if t == 0.5 { return middle.x; }
        if t < 0.5 { b = ab; c = middle; t *= 2.0; }
        else { a = middle; b = bc; t = (t - 0.5) * 2.0; }
    }
    return floor(a.x * 64.0) / 64.0;
}

fn dropout(record: GlyphRecord, index: u32, level: f32, vertical: bool) {
    let beginning = events[index];
    if beginning.delta == 0 || winding[index] == 0 { return; }
    if index > 0u && winding[index - 1u] != 0 { return; }
    let mode = segments[record.startSegment + beginning.source].scanDescriptor & 7u;
    if mode == 2u || mode == 3u || mode == 6u || mode == 7u { return; }
    var end = index + 1u;
    while end < 512u && winding[end] != 0 { end++; }
    if end == 512u { return; }
    let finish = events[end].x;
    let left = floor(beginning.x - 0.5);
    let right = left + 1.0;
    // Horizontal source scans increase X; vertical source scans increase the
    // original bitmap row, which is decreasing contour Y. Preserve that ordered
    // closed gap when transposing into this Y-up basis. Merely swapping axes
    // would exclude an exact lower-Y crossing and admit the opposite boundary.
    let gap = select(
        (left + 0.5 < beginning.x && finish <= right + 0.5),
        (left + 0.5 <= beginning.x && finish < right + 0.5), vertical);
    if !(gap && beginning.x < finish) { return; }
    let left_point = axis_point(vec2<f32>(left + 0.5, level), vertical);
    let right_point = axis_point(vec2<f32>(right + 0.5, level), vertical);
    if vertical {
        if horizontal_covered(left_point, record) || horizontal_covered(right_point, record) { return; }
    } else {
        if base_covered(record, left + 0.5, level, false) || base_covered(record, right + 0.5, level, false) { return; }
    }
    if mode == 1u || mode == 5u {
        for (var side = 0u; side < 2u; side++) {
            let direction = select(-1.0, 1.0, side != 0u);
            if continuation_crossings(record, left + 0.5, level, direction, vertical) < 2u &&
               !has_flat_cap(record, beginning, events[end], left + 0.5, level, direction, vertical) { return; }
        }
    }
    var selected = left;
    if mode == 4u || mode == 5u {
        let first_segment = segments[record.startSegment + beginning.source];
        let last_segment = segments[record.startSegment + events[end].source];
        var midpoint = (beginning.x + finish) * 0.5;
        if first_segment.segmentType == 1u || last_segment.segmentType == 1u {
            // Curved transitions use the nearest 26.6 midpoint, with exact
            // half-unit ties toward the lower unit. Line-only intervals keep
            // their original arithmetic; this is not a global glyph snap.
            midpoint = ceil((smart_curve_crossing(record, beginning, level, vertical) +
                smart_curve_crossing(record, events[end], level, vertical)) * 32.0 - 0.5) / 64.0;
        }
        if midpoint > right { selected = right; }
    }
    let bounds = raster_bounds(record);
    let candidate = axis_point(vec2<f32>(selected + 0.5, level), vertical);
    paint(clamp(candidate, bounds.xy + vec2<f32>(0.5), bounds.zw - vec2<f32>(0.5)), !vertical);
}

@compute @workgroup_size(256)
fn cs_binary_horizontal(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let row = group.y;
    if row >= uniforms.height { return; }
    for (var word = lane; word < uniforms.outputRowWords; word += 256u) {
        let address = uniforms.outputOffsetWords + row * uniforms.outputRowWords + word;
        atomicStore(&output[address], 0u);
        atomicStore(&horizontal[address], 0u);
    }
    storageBarrier();
    let record = glyphRecords[uniforms.glyphIndex];
    let level = -(uniforms.yStart + f32(row) + 0.5);
    let bounds = raster_bounds(record);
    prepare_scan(record, level, false, lane);
    if level < bounds.y || level >= bounds.w { return; }
    for (var x = lane; x < uniforms.width; x += 256u) {
        let point = vec2<f32>(uniforms.xStart + f32(x) + 0.5, level);
        if base_covered(record, point.x, level, false) { paint(point, true); }
    }
    // Dropout eligibility reads the immutable analytic base, not these writes.
    for (var index = lane; index < 512u; index += 256u) {
        dropout(record, index, level, false);
    }
    vertex_dropout(record, lane, level, false);
}

@compute @workgroup_size(256)
fn cs_binary_vertical(@builtin(workgroup_id) group: vec3<u32>, @builtin(local_invocation_index) lane: u32) {
    let column = group.y;
    if column >= uniforms.width { return; }
    let record = glyphRecords[uniforms.glyphIndex];
    let level = uniforms.xStart + f32(column) + 0.5;
    let bounds = raster_bounds(record);
    prepare_scan(record, level, true, lane);
    if level < bounds.x || level >= bounds.z { return; }
    for (var index = lane; index < 512u; index += 256u) {
        dropout(record, index, level, true);
    }
    vertex_dropout(record, lane, level, true);
}
