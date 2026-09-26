import assert from 'node:assert/strict';
import test from 'node:test';
import {Path, Scene, SceneBuilder, scenePacket} from '../scene.js';
import * as publicApi from '../index.js';

test('ES module import does not initialize a device or require DOM globals', () => {
    assert.deepEqual(Object.keys(publicApi).sort(), ['Path', 'Scene', 'SceneBuilder', 'createRenderer']);
});

test('snapshot identity is exact uint64 and construction is controlled', () => {
    const scene = new SceneBuilder({sceneId: 0xfedcba9876543210n, generation: 0x123456789abcdef0n}).build();
    const view = new DataView(scenePacket(scene).buffer);
    assert.equal(view.getUint32(0, true), 0x50534750);
    assert.equal(view.getUint32(4, true), 1);
    assert.equal(view.getBigUint64(8, true), scene.sceneId);
    assert.equal(view.getBigUint64(16, true), scene.generation);
    assert.equal(Object.isFrozen(scene), true);
    assert.throws(() => new Scene(), /SceneBuilder/);
    for (const sceneId of [0n, -1n, 1, 1n << 64n]) assert.throws(() => new SceneBuilder({sceneId}));
});

test('recording snapshots paths, gradients, matrices and point/dash arrays', () => {
    const path = new Path().moveTo(1, 2).quadraticTo(3, 4, 5, 6).cubicTo(7, 8, 9, 10, 11, 12);
    const color = [1, 0, 0, 1];
    const matrix = [1, 0, 0, 1, 4, 5];
    const gradient = {type: 'linearGradient', start: [0, 0], end: [100, 0], stops: [{offset: 0, color}, {offset: 1, color: [0, 0, 1, 1]}]};
    const points = [[0, 0], [10, 20], [30, 0]], dashes = [2, 1];
    const builder = new SceneBuilder({sceneId: 1n}).fillPath(path, gradient, {transform: matrix})
        .strokePolyline(points, color, {dashes});
    const first = scenePacket(builder.build()).slice();
    path.lineTo(90, 90); color.fill(0); matrix.fill(0); gradient.stops[1].offset = 0;
    gradient.start.fill(7); points[0].fill(100); dashes.fill(9);
    assert.deepEqual(scenePacket(builder.build()), first);
    const scene = builder.build();
    builder.fillRect(1, 2, 3, 4, [1, 1, 1, 1]);
    assert.deepEqual(scenePacket(scene), first);
    assert.notDeepEqual(scenePacket(builder.build()), first);
});

test('paths retain quadratic/cubic controls and explicitly close each contour', () => {
    const path = new Path().moveTo(1, 2).quadraticTo(3, 4, 5, 6)
        .cubicTo(7, 8, 9, 10, 11, 12).moveTo(20, 20).lineTo(30, 30).close();
    const bytes = scenePacket(new SceneBuilder().fillPath(path, [1, 1, 1, 1], {fillRule: 'evenodd'}).build());
    const view = new DataView(bytes.buffer);
    assert.equal(view.getUint32(64, true), 1); // fill rule
    assert.equal(view.getUint32(68, true), 5); // quad, cubic, close, line, close
    const segment = (index) => {
        const offset = 72 + index * 36;
        return [view.getUint32(offset, true), ...Array.from({length: 8}, (_, i) => view.getFloat32(offset + 4 + i * 4, true))];
    };
    assert.deepEqual(segment(0), [1, 1, 2, 3, 4, 5, 6, 0, 0]);
    assert.deepEqual(segment(1), [2, 5, 6, 7, 8, 9, 10, 11, 12]);
    assert.deepEqual(segment(2), [0, 11, 12, 1, 2, 0, 0, 0, 0]);
    assert.deepEqual(segment(4), [0, 30, 30, 20, 20, 0, 0, 0, 0]);
});

test('complete typed command sequence remains ordered and framed', () => {
    const scene = new SceneBuilder().save({clipRect: [0, 0, 50, 50], opacity: 0.8})
        .pushLayer({opacity: 0.5, bounds: [1, 2, 30, 40]})
        .fillRect(0, 0, 10, 20, [1, 0, 0, 1])
        .strokePolyline([[0, 0], [10, 10]], [0, 1, 0, 1], {closed: true, startCap: 'round', lineJoin: 'bevel'})
        .popLayer().restore().build();
    const bytes = scenePacket(scene), view = new DataView(bytes.buffer);
    const kinds = []; let offset = 32;
    while (offset < bytes.length) { kinds.push(view.getUint32(offset, true)); offset += 8 + view.getUint32(offset + 4, true); }
    assert.deepEqual(kinds, [4, 6, 1, 3, 7, 5]);
    assert.equal(offset, bytes.length);
    assert.equal(view.getUint32(24, true), kinds.length);
});

test('invalid authoring fails closed without corrupting preceding records', () => {
    const builder = new SceneBuilder().fillRect(0, 0, 2, 2, [1, 1, 1, 1]);
    const before = scenePacket(builder.build()).slice();
    for (const value of [NaN, Infinity, -Infinity, 1e100])
        assert.throws(() => builder.fillRect(value, 0, 10, 10, [1, 0, 0, 1]));
    assert.throws(() => builder.fillRect(0, 0, -1, 2, [1, 1, 1, 1]));
    assert.throws(() => builder.fillRect(0, 0, 1, 2, [1, 1, 1, 2]));
    assert.throws(() => builder.fillPath(new Path(), [1, 1, 1, 1]));
    assert.throws(() => new Path().lineTo(1, 2));
    assert.throws(() => builder.strokePolyline([[0, 0], [1, 1]], [1, 1, 1, 1], {startCap: 'unknown'}));
    assert.throws(() => builder.strokePolyline([[0, 0], [1, 1]], [1, 1, 1, 1], {dashes: [0, 0]}));
    assert.throws(() => builder.strokePolyline([[0, 0], [1, 1]], [1, 1, 1, 1], {dashes: [-1, 1]}));
    assert.throws(() => builder.fillRect(0, 0, 1, 1, {type: 'linearGradient', start: [0, 0], end: [1, 1],
        stops: [{offset: 1, color: [1, 1, 1, 1]}, {offset: 0, color: [1, 1, 1, 1]}]}));
    assert.deepEqual(scenePacket(builder.build()), before);
});

test('save/layer scopes cannot cross or silently remain unbalanced', () => {
    const builder = new SceneBuilder();
    assert.throws(() => builder.restore()); assert.throws(() => builder.popLayer());
    builder.save().pushLayer();
    assert.throws(() => builder.restore()); assert.throws(() => builder.build());
    builder.popLayer().restore(); assert.doesNotThrow(() => builder.build());
});

test('unsupported browser targets reject instead of selecting a fallback', async () => {
    await assert.rejects(publicApi.createRenderer({canvas: {}}), /HTMLCanvasElement/);
});
