// Authoring transport only. Geometry preparation, rasterization and retained
// scene validation remain in the original native semantic_scene_builder.
const identity = Object.freeze([1, 0, 0, 1, 0, 0]);
const packets = new WeakMap();
const pathSegments = new WeakMap();
const limit = 64 * 1024 * 1024;
let nextSceneId = 1n;

function finite(value, name) {
    if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isFinite(Math.fround(value)))
        throw new TypeError(`${name} must be a finite float32 number`);
    return Math.fround(value);
}
function tuple(value, length, name) {
    if (!value || value.length !== length) throw new TypeError(`${name} must have ${length} entries`);
    return Array.from(value, (v) => finite(v, name));
}
function unit(value, name) {
    const n = finite(value, name);
    if (n < 0 || n > 1) throw new RangeError(`${name} must be between zero and one`);
    return n;
}
function color(value) { return tuple(value, 4, 'color').map((v) => unit(v, 'color')); }
function rectangle(value) {
    const result = tuple(value, 4, 'rectangle');
    if (result[2] < 0 || result[3] < 0) throw new RangeError('Rectangle extents must be nonnegative');
    return result;
}
function choice(value, values, name) {
    const result = values.indexOf(value);
    if (result < 0) throw new TypeError(`Unsupported ${name}: ${value}`);
    return result;
}
function id(value, name) {
    if (typeof value !== 'bigint' || value <= 0n || value > 0xffffffffffffffffn)
        throw new RangeError(`${name} must be a nonzero uint64 bigint`);
    return value;
}
function brush(value) {
    if (Array.isArray(value) || ArrayBuffer.isView(value)) return {kind: 0, color: color(value)};
    if (!value || value.type !== 'linearGradient') throw new TypeError('Expected a color or linearGradient brush');
    if (!Array.isArray(value.stops) || value.stops.length < 2 || value.stops.length > 65536)
        throw new RangeError('A gradient requires 2 to 65536 ordered stops');
    let previous = -Infinity;
    const stops = value.stops.map((stop) => {
        const offset = unit(stop.offset, 'gradient stop');
        if (offset < previous) throw new RangeError('Gradient stops must be ordered');
        previous = offset;
        return {offset, color: color(stop.color)};
    });
    return {kind: 1, start: tuple(value.start, 2, 'gradient start'), end: tuple(value.end, 2, 'gradient end'),
        opacity: unit(value.opacity ?? 1, 'opacity'),
        spread: choice(value.spread ?? 'pad', ['pad', 'reflect', 'repeat'], 'gradient spread'), stops};
}

class Writer {
    constructor() { this.bytes = new Uint8Array(256); this.view = new DataView(this.bytes.buffer); this.length = 0; }
    grow(size) {
        if (this.length + size > limit) throw new RangeError('Typed scene exceeds the 64 MiB transport limit');
        if (this.length + size <= this.bytes.length) return;
        const bytes = new Uint8Array(Math.min(limit, Math.max(this.bytes.length * 2, this.length + size)));
        bytes.set(this.bytes); this.bytes = bytes; this.view = new DataView(bytes.buffer);
    }
    u32(value) { this.grow(4); this.view.setUint32(this.length, value, true); this.length += 4; }
    u64(value) { this.grow(8); this.view.setBigUint64(this.length, value, true); this.length += 8; }
    f32(value) { this.grow(4); this.view.setFloat32(this.length, value, true); this.length += 4; }
    f64(value) { this.grow(8); this.view.setFloat64(this.length, value, true); this.length += 8; }
    floats(values) { for (const value of values) this.f32(value); }
    paint(value) {
        this.u32(value.kind);
        if (value.kind === 0) { this.floats(value.color); return; }
        this.f32(value.opacity); this.floats(value.start); this.floats(value.end);
        this.u32(value.spread); this.u32(value.stops.length);
        for (const stop of value.stops) { this.f32(stop.offset); this.floats(stop.color); }
    }
    record(kind, write) {
        this.u32(kind); const start = this.length; this.u32(0); write(this);
        this.view.setUint32(start, this.length - start - 4, true);
    }
    finish() { return this.bytes.slice(0, this.length); }
}

/** Mutable path authoring; fillPath takes an independent snapshot immediately. */
export class Path {
    #segments = []; #current = null; #start = null;
    constructor() {
        pathSegments.set(this, () => {
            const result = this.#segments.map((segment) => [...segment]);
            this.#closeInto(result);
            return result;
        });
    }
    #closeInto(segments) {
        if (this.#current && (this.#current[0] !== this.#start[0] || this.#current[1] !== this.#start[1]))
            segments.push([0, ...this.#current, ...this.#start, 0, 0, 0, 0]);
    }
    moveTo(x, y) {
        const point = tuple([x, y], 2, 'point');
        this.#closeInto(this.#segments); this.#current = point; this.#start = point; return this;
    }
    #segment(kind, values, end) {
        if (!this.#current) throw new Error('A path must start with moveTo');
        const points = tuple(values, values.length, 'point');
        this.#segments.push([kind, ...this.#current, ...points, ...Array(6 - points.length).fill(0)]);
        this.#current = points.slice(end, end + 2); return this;
    }
    lineTo(x, y) { return this.#segment(0, [x, y], 0); }
    quadraticTo(cx, cy, x, y) { return this.#segment(1, [cx, cy, x, y], 2); }
    cubicTo(c1x, c1y, c2x, c2y, x, y) { return this.#segment(2, [c1x, c1y, c2x, c2y, x, y], 4); }
    close() {
        if (!this.#current) throw new Error('A path must start with moveTo');
        this.#closeInto(this.#segments); this.#current = this.#start; return this;
    }
}

/** Immutable authoring snapshot. Construct with SceneBuilder.build(). */
export class Scene {
    constructor(key, bytes, sceneId, generation) {
        if (key !== packets) throw new TypeError('Use SceneBuilder.build() to create a Scene');
        this.sceneId = sceneId; this.generation = generation;
        packets.set(this, bytes); Object.freeze(this);
    }
}

// Internal module seam, not a package export. Never return the owned byte array
// to an application: the renderer only borrows it for one synchronous update.
export function scenePacket(scene) { return packets.get(scene); }

export class SceneBuilder {
    #records = []; #stack = []; #sceneId; #generation;
    constructor({sceneId = nextSceneId++, generation = 1n} = {}) {
        this.#sceneId = id(sceneId, 'sceneId'); this.#generation = id(generation, 'generation');
    }
    fillRect(x, y, width, height, paint, {transform = identity} = {}) {
        const rect = rectangle([x, y, width, height]); const material = brush(paint);
        const matrix = tuple(transform, 6, 'transform');
        this.#records.push([1, (w) => { w.floats(rect); w.floats(matrix); w.paint(material); }]); return this;
    }
    fillPath(path, paint, {fillRule = 'nonzero', transform = identity} = {}) {
        const snapshot = pathSegments.get(path);
        if (!snapshot) throw new TypeError('Expected a Path');
        const segments = snapshot();
        if (segments.length === 0) throw new RangeError('A filled path requires segments');
        const rule = choice(fillRule, ['nonzero', 'evenodd'], 'fill rule');
        const matrix = tuple(transform, 6, 'transform'); const material = brush(paint);
        this.#records.push([2, (w) => {
            w.floats(matrix); w.u32(rule); w.u32(segments.length);
            for (const segment of segments) { w.u32(segment[0]); w.floats(segment.slice(1)); }
            w.paint(material);
        }]); return this;
    }
    strokePolyline(points, paint, {width = 1, closed = false, startCap = 'flat', endCap = 'flat',
        lineJoin = 'miter', dashCap = 'flat', dashes = [], dashOffset = 0, miterLimit = 10, transform = identity} = {}) {
        if (!Array.isArray(points) || points.length < 2) throw new RangeError('A polyline requires at least two points');
        const snapshot = points.map((point) => tuple(point, 2, 'point'));
        const thickness = finite(width, 'width'); const miter = finite(miterLimit, 'miterLimit');
        if (thickness <= 0 || miter < 1) throw new RangeError('Width must be positive and miterLimit at least one');
        if (typeof closed !== 'boolean') throw new TypeError('closed must be boolean');
        const caps = [startCap, endCap, dashCap].map((cap) => choice(cap, ['flat', 'square', 'round', 'triangle'], 'cap'));
        const join = choice(lineJoin, ['miter', 'bevel', 'round'], 'join');
        if (!Array.isArray(dashes)) throw new TypeError('dashes must be an array');
        const intervals = dashes.map((v) => {
            if (typeof v !== 'number' || !Number.isFinite(v) || v < 0) throw new RangeError('Dash multipliers must be finite and nonnegative');
            return v;
        });
        if (intervals.length && !intervals.some((v) => v > 0)) throw new RangeError('A dash pattern needs a positive interval');
        if (typeof dashOffset !== 'number' || !Number.isFinite(dashOffset)) throw new TypeError('dashOffset must be finite');
        const matrix = tuple(transform, 6, 'transform'); const material = brush(paint);
        this.#records.push([3, (w) => {
            w.floats(matrix); w.f32(thickness); w.f32(miter); w.u32(closed ? 1 : 0);
            w.u32(caps[0]); w.u32(caps[1]); w.u32(join); w.u32(caps[2]); w.f64(dashOffset);
            w.u32(snapshot.length); w.u32(intervals.length);
            for (const point of snapshot) w.floats(point);
            for (const interval of intervals) w.f64(interval);
            w.paint(material);
        }]); return this;
    }
    /** Absolute native draw state, not a concatenating Canvas2D transform. */
    save({transform = identity, clipRect, opacity = 1} = {}) {
        const matrix = tuple(transform, 6, 'transform'); const alpha = unit(opacity, 'opacity');
        const clip = clipRect === undefined ? null : rectangle(clipRect);
        this.#records.push([4, (w) => { w.floats(matrix); w.f32(alpha); w.u32(clip ? 1 : 0); if (clip) w.floats(clip); }]);
        this.#stack.push(4); return this;
    }
    restore() {
        if (this.#stack.at(-1) !== 4) throw new Error('restore must match the innermost save');
        this.#stack.pop(); this.#records.push([5, () => {}]); return this;
    }
    pushLayer({opacity = 1, bounds} = {}) {
        const alpha = unit(opacity, 'opacity'); const rect = bounds === undefined ? null : rectangle(bounds);
        this.#records.push([6, (w) => { w.f32(alpha); w.u32(rect ? 1 : 0); if (rect) w.floats(rect); }]);
        this.#stack.push(6); return this;
    }
    popLayer() {
        if (this.#stack.at(-1) !== 6) throw new Error('popLayer must match the innermost pushLayer');
        this.#stack.pop(); this.#records.push([7, () => {}]); return this;
    }
    build() {
        if (this.#stack.length) throw new Error('Scene scopes must be balanced before build');
        const writer = new Writer(); writer.u32(0x50534750); writer.u32(1);
        writer.u64(this.#sceneId); writer.u64(this.#generation); writer.u32(this.#records.length); writer.u32(0);
        for (const [kind, record] of this.#records) writer.record(kind, record);
        return new Scene(packets, writer.finish(), this.#sceneId, this.#generation);
    }
}
