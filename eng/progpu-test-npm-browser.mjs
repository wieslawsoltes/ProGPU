import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs/promises';
import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyInstalledNpmTypes } from './progpu-test-npm-types.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.resolve(process.argv[2] ?? path.join(repo, 'artifacts/npm'));
const evidence = path.join(output, 'evidence');
await fs.mkdir(evidence, { recursive: true });
const artifact = JSON.parse(await fs.readFile(path.join(output, 'npm-artifact.json'), 'utf8'));
assert.match(artifact.archive, /^[a-zA-Z0-9._-]+\.tgz$/);
const archive = path.join(output, artifact.archive);
assert.equal(createHash('sha256').update(await fs.readFile(archive)).digest('hex'), artifact.sha256);
const consumer = await fs.mkdtemp(path.join(output, 'consumer-'));
await fs.writeFile(path.join(consumer, 'package.json'), JSON.stringify({ private: true, type: 'module' }));
execFileSync('npm', ['install', '--ignore-scripts', '--no-audit', '--no-fund', '--package-lock=false', archive],
  { cwd: consumer, stdio: 'inherit' });
const installedRoot = path.join(consumer, 'node_modules', artifact.name);
const installed = JSON.parse(await fs.readFile(path.join(installedRoot, 'package.json'), 'utf8'));
assert.equal(installed.name, artifact.name);
assert.equal(installed.version, artifact.version);
const info = JSON.parse(await fs.readFile(path.join(installedRoot, 'build-info.json'), 'utf8'));
assert.equal(info.sourceCommit, artifact.sourceCommit);
for (const [name, digest] of Object.entries(info.licenses)) {
  assert.equal(createHash('sha256').update(await fs.readFile(path.join(installedRoot, name))).digest('hex'), digest);
}
const typeQualification = await verifyInstalledNpmTypes(consumer, artifact.name);
const importMap = { imports: { progpu: `./node_modules/${artifact.name}/index.js` } };
await fs.writeFile(path.join(consumer, 'index.html'), `<!doctype html>
<meta charset="utf-8"><title>ProGPU installed npm package</title>
<style>body{margin:12px;background:#202026;color:white;font:16px/24px sans-serif}h1{font-size:24px;line-height:32px;margin:0 0 12px}p{margin:0 0 12px}canvas{display:block;width:320px;height:180px;margin:12px 0}</style>
<h1>ProGPU installed npm package</h1><p>Independent native renderer instances, one borrowed WebGPU device.</p>
<script type="importmap">${JSON.stringify(importMap)}</script>
<canvas id="first" width="320" height="180"></canvas><canvas id="second" width="320" height="180"></canvas>`);
const server = createServer(async (request, response) => {
  try {
    const requested = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    if (requested === '/favicon.ico') { response.writeHead(204); response.end(); return; }
    const file = path.resolve(consumer, `.${requested === '/' ? '/index.html' : requested}`);
    if (!file.startsWith(consumer + path.sep)) throw new Error('Invalid path');
    const content = await fs.readFile(file);
    const type = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
      '.wasm': 'application/wasm', '.json': 'application/json' }[path.extname(file)] ?? 'application/octet-stream';
    response.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-store' });
    response.end(content);
  } catch {
    response.writeHead(404); response.end();
  }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const require = createRequire(path.join(repo, 'src/ProGPU.Native/browser/package.json'));
const { chromium } = require('playwright');
const { default: utilities } = await import(pathToFileURL(path.join(
  path.dirname(require.resolve('playwright-core')), 'lib/utilsBundle.js')));
const errors = [];
let browser;
let timer;
try {
  browser = await chromium.launch({ channel: 'chromium', headless: true,
    args: ['--enable-unsafe-webgpu', '--use-angle=swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 720, height: 620 }, deviceScaleFactor: 1 });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto(origin, { waitUntil: 'domcontentloaded' });
  const result = await Promise.race([page.evaluate(async () => {
    const { createRenderer, SceneBuilder, Path } = await import('progpu');
    const verify = (condition, message) => { if (!condition) throw new Error(message); };
    const reject = (action, message) => {
      let rejected = false;
      try { action(); } catch { rejected = true; }
      verify(rejected, message);
    };
    verify(!Object.hasOwn(globalThis, 'Module'), 'ES import must not create a global Module');
    const adapter = await navigator.gpu.requestAdapter();
    verify(adapter, 'WebGPU adapter is required');
    const device = await adapter.requestDevice();
    const gpuErrors = [];
    device.addEventListener('uncapturederror', event => gpuErrors.push(event.error.message));
    let borrowedDestroyed = false;
    device.lost.then(() => { borrowedDestroyed = true; });
    const firstCanvas = document.querySelector('#first');
    const secondCanvas = document.querySelector('#second');
    const start = performance.now();
    const first = await createRenderer({ canvas: firstCanvas, device });
    const factoryMilliseconds = performance.now() - start;
    first.resize({ width: 320, height: 180, pixelRatio: 1 });
    const curve = new Path().moveTo(64, 56).quadraticTo(64, 8, 88, 8)
      .cubicTo(112, 8, 120, 56, 104, 56).lineTo(64, 56).close();
    const hole = new Path().moveTo(128, 8).lineTo(184, 8).lineTo(184, 64).lineTo(128, 64).close()
      .moveTo(144, 24).lineTo(168, 24).lineTo(168, 48).lineTo(144, 48).close();
    const stops = [{ offset: 0, color: [1, 0, 0, 1] }, { offset: 1, color: [0, 0, 1, 1] }];
    const builder = new SceneBuilder({ sceneId: 7n, generation: 1n })
      .fillRect(8, 8, 40, 40, [1, 0, 0, 1])
      .fillPath(curve, [0, 0.6, 1, 1])
      .fillPath(hole, [0, 1, 0, 1], { fillRule: 'evenodd' })
      .fillRect(196, 8, 104, 56, { type: 'linearGradient', start: [196, 8], end: [300, 8], stops })
      .strokePolyline([[12, 100], [80, 100], [80, 150]], [1, 1, 0, 1],
        { width: 8, startCap: 'round', endCap: 'round', lineJoin: 'round' })
      .save({ clipRect: [110, 88, 40, 40], opacity: 0.5 })
      .fillRect(100, 80, 70, 70, [1, 1, 1, 1]).restore()
      .pushLayer({ opacity: 0.5, bounds: [180, 88, 48, 48] })
      .fillRect(180, 88, 48, 48, [1, 0, 0, 1]).popLayer()
      .strokePolyline([[12, 164], [132, 164]], [1, 1, 1, 1],
        { width: 4, dashes: [2, 2], startCap: 'flat', endCap: 'flat', dashCap: 'flat' })
      .fillRect(0, 0, 24, 16, [0, 1, 1, 1], { transform: [1, 0, 0, 1, 260, 90] });
    // Mutations after recording must not alter owned geometry or brush bytes.
    curve.lineTo(310, 170); stops[0].color.fill(0);
    const scene = builder.build();
    verify(Object.isFrozen(scene), 'Scene must be immutable');
    const update = first.updateScene(scene);
    verify(update.sceneId === 7n && update.generation === 1n && update.commandCount > 0,
      'Actual native scene compilation must report the supplied identity');
    verify(first.updateScene(scene) === update, 'Repeated immutable scene must avoid another native update');
    const coldStart = performance.now();
    const cold = first.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const coldMilliseconds = performance.now() - coldStart;
    const firstPng = firstCanvas.toDataURL();
    const warmStart = performance.now();
    const warm = first.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const warmMilliseconds = performance.now() - warmStart;
    verify(warm.submissionCount === 1n && warm.drawCallCount > 0, 'Frame must use the actual native GPU renderer');
    for (const name of ['vertexUploadBytes', 'indexUploadBytes', 'textureUploadBytes', 'uniformUploadBytes',
      'coverageStagingBytes', 'brushUploadBytes', 'gradientStopUploadBytes', 'textStyleUploadBytes', 'colorGlyphUploadBytes']) {
      verify(warm[name] === 0, `Unchanged retained frame must not upload ${name}`);
    }
    const warmPng = firstCanvas.toDataURL();
    const raw = first.getSceneStream();
    const retained = raw.slice();
    const rawReuse = first.updateScene(raw);
    verify(rawReuse.snapshotReused && rawReuse.sceneId === 7n && rawReuse.generation === 1n,
      'Equal raw bytes must reuse the actual native snapshot');
    reject(() => first.updateScene(new SceneBuilder({ sceneId: 7n, generation: 1n })
      .fillRect(8, 8, 40, 40, [0, 0, 1, 1]).build()),
    'Changed bytes with the same native identity must fail');
    verify(first.getSceneStream().every((byte, index) => byte === retained[index]),
      'An invalid repeated generation must preserve all accepted bytes');
    const second = await createRenderer({ canvas: secondCanvas, device });
    second.resize({ width: 320, height: 180, pixelRatio: 1 });
    second.updateScene(raw);
    raw.fill(0);
    reject(() => second.updateScene(raw), 'Malformed full native stream must fail');
    verify(second.getSceneStream().every((byte, index) => byte === retained[index]),
      'A rejected update must preserve the complete accepted stream');
    second.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const secondPng = secondCanvas.toDataURL();
    const changed = new SceneBuilder({ sceneId: 7n, generation: 2n })
      .fillRect(8, 8, 40, 40, [1, 0, 1, 1]).build();
    const changedUpdate = first.updateScene(changed);
    verify(changedUpdate.generation === 2n && !changedUpdate.snapshotReused,
      'A changed generation must compile its actual content');
    first.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const changedPng = firstCanvas.toDataURL();
    const resized = first.resize({ width: 320, height: 180, pixelRatio: 2 });
    verify(resized.width === 640 && resized.height === 360 && resized.scale === 2,
      'Resize must preserve logical size and actual physical DPI');
    verify(first.updateScene(changed) === changedUpdate, 'Resize must not replace the immutable scene');
    first.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const resizedPng = firstCanvas.toDataURL();
    first.dispose(); first.dispose();
    reject(() => first.render(), 'A disposed renderer must reject rendering');
    verify(!borrowedDestroyed, 'Disposal must not destroy a supplied device');
    second.render({ clearColor: [0.1, 0.1, 0.1, 1] });
    await device.queue.onSubmittedWorkDone();
    const survivingPng = secondCanvas.toDataURL();
    const owned = await createRenderer({ canvas: firstCanvas });
    const ownedLost = owned.device.lost;
    owned.dispose();
    verify((await ownedLost).reason === 'destroyed', 'Disposal must destroy a factory-owned device');
    verify(!borrowedDestroyed, 'Disposing another factory-owned device must preserve the borrowed device');
    // Keep this live surface for an actual page-composition screenshot.
    globalThis.npmConsumerCleanup = () => { second.dispose(); device.destroy(); };
    verify(!Object.hasOwn(globalThis, 'Module'), 'Multiple isolated modules must not pollute global Module');
    verify(gpuErrors.length === 0, gpuErrors.join('\n'));
    return { firstPng, warmPng, secondPng, survivingPng, changedPng, resizedPng,
      update, rawReuse, changedUpdate, resized, cold, warm,
      timings: { factoryMilliseconds, coldMilliseconds, warmMilliseconds },
      gpuErrors, borrowedDestroyed, sourceBytes: retained.length };
  }), new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error('Installed npm package browser contracts exceeded 120 seconds')), 120_000);
  })]);
  clearTimeout(timer);
  const decode = data => utilities.PNG.sync.read(Buffer.from(data.split(',')[1], 'base64'));
  const first = decode(result.firstPng);
  assert.equal(first.width, 320); assert.equal(first.height, 180);
  for (const name of ['warmPng', 'secondPng', 'survivingPng']) {
    assert.deepEqual(decode(result[name]).data, first.data, `${name} must preserve every pixel`);
  }
  const pixel = (x, y) => Array.from(first.data.subarray((y * first.width + x) * 4, (y * first.width + x) * 4 + 4));
  const sample = (x, y, expected) => {
    const actual = pixel(x, y);
    for (let channel = 0; channel < 4; channel++) {
      assert.ok(Math.abs(actual[channel] - expected[channel]) <= 2,
        `Pixel ${x},${y}: ${actual} != ${expected}`);
    }
  };
  sample(20, 20, [255, 0, 0, 255]);
  sample(88, 32, [0, 153, 255, 255]);
  sample(136, 16, [0, 255, 0, 255]);
  sample(156, 32, [26, 26, 26, 255]);
  sample(32, 100, [255, 255, 0, 255]);
  sample(120, 100, [140, 140, 140, 255]);
  sample(104, 100, [26, 26, 26, 255]);
  sample(190, 100, [140, 13, 13, 255]);
  sample(16, 164, [255, 255, 255, 255]);
  sample(24, 164, [26, 26, 26, 255]);
  sample(32, 164, [255, 255, 255, 255]);
  sample(264, 96, [0, 255, 255, 255]);
  sample(4, 4, [26, 26, 26, 255]);
  const changed = decode(result.changedPng);
  const resized = decode(result.resizedPng);
  assert.equal(changed.width, 320); assert.equal(changed.height, 180);
  assert.equal(resized.width, 640); assert.equal(resized.height, 360);
  for (const [pixels, x, y] of [[changed, 20, 20], [resized, 40, 40]]) {
    const offset = (y * pixels.width + x) * 4;
    assert.deepEqual(Array.from(pixels.data.subarray(offset, offset + 4)), [255, 0, 255, 255]);
  }
  // The transformed cyan rectangle from generation one must disappear entirely.
  assert.deepEqual(Array.from(changed.data.subarray((96 * 320 + 264) * 4, (96 * 320 + 264) * 4 + 4)), [26, 26, 26, 255]);
  assert.ok(pixel(205, 24)[0] > 200 && pixel(205, 24)[2] < 55, 'Gradient left must retain original red stop');
  assert.ok(pixel(290, 24)[0] < 55 && pixel(290, 24)[2] > 200, 'Gradient right must retain blue stop');
  await fs.writeFile(path.join(evidence, 'npm-native-canvas.png'), Buffer.from(result.secondPng.split(',')[1], 'base64'));
  const presented = await page.locator('#second').screenshot({ path: path.join(evidence, 'npm-native-presented.png'), timeout: 15_000 });
  const presentedPixels = utilities.PNG.sync.read(presented);
  assert.equal(presentedPixels.width, first.width);
  assert.equal(presentedPixels.height, first.height);
  assert.ok(presentedPixels.data.equals(first.data), 'Browser must actually present every rendered pixel');
  await page.screenshot({ path: path.join(evidence, 'npm-native-page.png'), timeout: 15_000 });
  await page.evaluate(() => globalThis.npmConsumerCleanup());
  assert.deepEqual(errors, []);
  const { firstPng, warmPng, secondPng, survivingPng, changedPng, resizedPng, ...metrics } = result;
  await fs.writeFile(path.join(evidence, 'npm-browser-contract.json'), JSON.stringify({
    package: artifact.name, version: artifact.version, archiveSha256: artifact.sha256,
    sourceCommit: artifact.sourceCommit, adapter: 'Chromium explicit SwiftShader',
    completePixelEquality: true, typeQualification, ...metrics
  }, (_, value) => typeof value === 'bigint' ? value.toString() : value, 2) + '\n');
  console.log(`Installed ${artifact.name}@${artifact.version}: native WebGPU, paths, gradients, clips, layers, retained pixels and borrowed-device ownership passed.`);
} finally {
  clearTimeout(timer);
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
