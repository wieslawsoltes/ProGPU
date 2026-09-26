import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import * as fs from 'node:fs/promises';
import {createRequire} from 'node:module';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(path.join(repo, 'src/ProGPU.Native/browser/package.json'));

/** Compile only against an actually installed package, never checkout aliases.
 * The caller owns a fresh consumer directory and installs its verified tgz first.
 * No generated JS runs, no GPU is requested, and skipLibCheck stays disabled. */
export async function verifyInstalledNpmTypes(consumerDirectory, packageName = 'progpu') {
  const consumer = await fs.realpath(consumerDirectory);
  assert.match(packageName, /^(?:@[a-z0-9._-]+\/)?[a-z0-9][a-z0-9._-]*$/);
  const installed = path.join(consumer, 'node_modules', packageName);
  const manifest = JSON.parse(await fs.readFile(path.join(installed, 'package.json'), 'utf8'));
  assert.equal(manifest.name, packageName);
  assert.equal(manifest.types, './index.d.ts');
  const compilerManifestPath = require.resolve('typescript/package.json');
  const compilerManifest = JSON.parse(await fs.readFile(compilerManifestPath, 'utf8'));
  assert.equal(compilerManifest.version, '6.0.3', 'Installed npm declaration gate requires the pinned TypeScript compiler');
  const compiler = path.join(path.dirname(compilerManifestPath), 'bin/tsc');
  const fixture = path.join(consumer, 'progpu-installed-types.mts');
  const source = `import {createRenderer, Scene, SceneBuilder, Path,
  type Brush, type Color, type Point, type Rect, type Transform,
  type StrokeOptions, type LinearGradient, type Renderer,
  type SceneUpdateMetrics, type FrameMetrics, type CanvasMetrics} from ${JSON.stringify(packageName)};

const canvas: HTMLCanvasElement = document.createElement('canvas');
const device: GPUDevice = await (await navigator.gpu.requestAdapter())!.requestDevice();
const color: Color = [1, 0.5, 0, 1];
const points: readonly Point[] = [[0, 0], [20, 20], [40, 0]];
const rect: Rect = [0, 0, 100, 100];
const transform: Transform = [1, 0, 0, 1, 10, 20];
const gradient: LinearGradient = {type: 'linearGradient', start: [0, 0], end: [100, 0],
  opacity: 0.8, spread: 'reflect', stops: [{offset: 0, color}, {offset: 1, color: [0, 0, 1, 1]}]};
const brush: Brush = gradient;
const stroke: StrokeOptions = {width: 4, closed: true, startCap: 'flat', endCap: 'square',
  lineJoin: 'round', dashCap: 'triangle', miterLimit: 10, dashes: [2, 1, 3], dashOffset: 0.5, transform};
const path = new Path().moveTo(0, 0).lineTo(20, 0).quadraticTo(40, 20, 20, 40)
  .cubicTo(10, 50, 0, 30, 0, 0).close();
const scene: Scene = new SceneBuilder({sceneId: 7n, generation: 2n})
  .save({transform, opacity: 0.8, clipRect: rect})
  .pushLayer({opacity: 0.5, bounds: rect})
  .fillRect(0, 0, 20, 20, color, {transform})
  .fillPath(path, brush, {fillRule: 'evenodd', transform})
  .strokePolyline(points, color, stroke).popLayer().restore().build();
const renderer: Renderer = await createRenderer({canvas, device, onError: error => console.error(error.message)});
const dimensions: CanvasMetrics = renderer.resize({width: 100, height: 100, pixelRatio: 2});
const update: SceneUpdateMetrics = renderer.updateScene(scene);
const raw: Uint8Array = renderer.getSceneStream();
renderer.updateScene(raw);
const frame: FrameMetrics = renderer.render({clearColor: color});
const engineDevice: GPUDevice = renderer.device;
const error: Error | null = renderer.error;
const sceneId: bigint = scene.sceneId;
const generation: bigint = update.generation;
const submission: bigint = frame.submissionCount;
const physicalWidth: number = dimensions.width;
const bytes: number = frame.vertexUploadBytes + frame.indexUploadBytes + frame.textureUploadBytes
  + frame.uniformUploadBytes + frame.coverageStagingBytes + frame.brushUploadBytes
  + frame.gradientStopUploadBytes + frame.textStyleUploadBytes + frame.colorGlyphUploadBytes;
await engineDevice.queue.onSubmittedWorkDone();
renderer.dispose();

// These must remain errors, not widened any/unknown authoring contracts.
// @ts-expect-error A Scene cannot be constructed outside the builder.
new Scene();
// @ts-expect-error Scene identities preserve uint64 bigint transport.
new SceneBuilder({sceneId: 7});
// @ts-expect-error Immutable scene identity cannot be rewritten.
scene.generation = 3n;
// @ts-expect-error Native metrics are immutable observations.
frame.drawCallCount = 0;
// @ts-expect-error Native submission identifiers are not numbers.
const wrongSubmission: number = frame.submissionCount;
// @ts-expect-error A matrix must contain all six coefficients.
new SceneBuilder().fillRect(0, 0, 1, 1, color, {transform: [1, 0]});
// @ts-expect-error Unknown stroke cap contracts are rejected.
new SceneBuilder().strokePolyline(points, color, {startCap: 'invented'});
// @ts-expect-error Unknown fill rules are rejected.
new SceneBuilder().fillPath(path, color, {fillRule: 'positive'});
// @ts-expect-error The initial typed surface does not invent radial gradients.
const unsupportedBrush: Brush = {type: 'radialGradient', center: [0, 0], radius: 10};
// @ts-expect-error Text authoring is not a fake convenience wrapper.
new SceneBuilder().fillText('text', 0, 0, color);
// @ts-expect-error Complete raw streams require a typed byte view.
renderer.updateScene(new ArrayBuffer(16));
// @ts-expect-error Clear color requires all four channels.
renderer.render({clearColor: [0, 0, 0]});
// @ts-expect-error The DOM host does not silently admit offscreen canvases.
createRenderer({canvas: new OffscreenCanvas(100, 100)});
// @ts-expect-error A borrowed device must have the real WebGPU contract.
createRenderer({canvas, device: {queue: {submit() {}}}});
// @ts-expect-error No backend fallback option is silently accepted.
createRenderer({canvas, fallback: 'canvas2d'});
`;
  await fs.writeFile(fixture, source, {flag: 'wx'});
  try {
    execFileSync(process.execPath, [compiler, '--strict', '--noEmit', '--skipLibCheck', 'false',
      '--module', 'NodeNext', '--moduleResolution', 'NodeNext', '--target', 'ES2022',
      '--lib', 'ES2022,DOM', fixture], {
      cwd: consumer, encoding: 'utf8', timeout: 60_000, maxBuffer: 4 * 1024 * 1024,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (error) {
    throw new Error(`Installed npm declaration compilation failed:\n${error.stdout ?? ''}${error.stderr ?? ''}`, {cause: error});
  }
  console.log(`Installed ${packageName}: strict TypeScript ${compilerManifest.version} public API and negative contracts passed.`);
  return Object.freeze({compiler: 'TypeScript', version: compilerManifest.version,
    strict: true, skipLibCheck: false, installedPackage: packageName});
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  assert.ok(process.argv.length === 3 || process.argv.length === 4,
    'Usage: node eng/progpu-test-npm-types.mjs INSTALLED_CONSUMER_DIRECTORY [PACKAGE_NAME]');
  await verifyInstalledNpmTypes(process.argv[2], process.argv[3]);
}
