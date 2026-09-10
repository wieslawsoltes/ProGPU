import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '../src/ProGPU.Native/browser/node_modules/playwright/index.mjs';
import browserUtilities from '../src/ProGPU.Native/browser/node_modules/playwright-core/lib/utilsBundle.js';
import { verifyWebGpuPresentation } from './progpu-webgpu-presentation.mjs';

// Exercise the published CAD app through its canonical browser host. Reuse the
// pinned browser-test dependency: npm ci --prefix src/ProGPU.Native/browser.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'src/ProGPU.CAD.Sample.Browser/bin/Release/net10.0');
const site = path.join(output, 'publish/wwwroot');
const evidence = path.join(root, 'artifacts/progpu-cad/browser-smoke');
await fs.access(path.join(root,
  'src/ProGPU.CAD.Sample.Browser/obj/Release/net10.0/wasm/for-publish/ProGPU.CAD.dll.o'));
await fs.access(path.join(site, 'index.html'));
await fs.mkdir(evidence, { recursive: true });

const types = { '.html': 'text/html', '.js': 'text/javascript',
  '.css': 'text/css', '.json': 'application/json', '.wasm': 'application/wasm' };
const server = http.createServer(async (request, response) => {
  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    const file = path.resolve(site, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!file.startsWith(site + path.sep)) {
      response.writeHead(403).end();
      return;
    }
    const bytes = await fs.readFile(file);
    response.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
    response.end(bytes);
  } catch {
    response.writeHead(404).end();
  }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
let page;
const errors = [];
const browserLog = [];
const softwareRendering = process.env.PROGPU_CAD_BROWSER_USE_SWIFTSHADER === '1';
const visualTimeoutMs = softwareRendering ? 120_000 : 30_000;
// A 2x, 2560x1600 SwiftShader surface can deadlock Chromium's WebGPU readback
// and compositor paths. Keep logical input coverage identical and use a 1x
// surface only for the hosted software adapter; hardware runs retain HiDPI.
const appDeviceScaleFactor = softwareRendering ? 1 : 2;
async function captureCadCanvas(timeout = visualTimeoutMs) {
  // Chromium's page-compositor screenshot can stall indefinitely when a large
  // SwiftShader WebGPU surface is active. Read the presented canvas directly
  // for the app's pixel oracle; verifyWebGpuPresentation separately proves that
  // a WebGPU canvas reaches Chromium's page-composition screenshot path.
  const dataUrl = await diagnosticDeadline(page.evaluate(() =>
    document.querySelector('#progpu-canvas')?.toDataURL('image/png')),
  'CAD canvas readback', timeout);
  assert.ok(dataUrl?.startsWith('data:image/png;base64,'),
    'The CAD canvas did not produce a PNG readback.');
  return Buffer.from(dataUrl.split(',')[1], 'base64');
}
function captureHostState() {
  // DOM-only: canvas readback can block even while these diagnostics work.
  return page.evaluate(() => {
    const canvas = document.querySelector('#progpu-canvas');
    return {
      userAgent: navigator.userAgent,
      title: document.querySelector('#status-title')?.textContent,
      detail: document.querySelector('#status-detail')?.textContent,
      frames: document.querySelector('#counter-frames')?.textContent,
      dispatches: document.querySelector('#counter-dispatches')?.textContent,
      commandBytes: document.querySelector('#counter-bytes')?.textContent,
      width: canvas?.width, height: canvas?.height, dpi: devicePixelRatio,
      rootBackground: getComputedStyle(document.documentElement).backgroundColor,
    };
  });
}
async function diagnosticDeadline(operation, label, timeoutMs = 10_000) {
  let timeout;
  try {
    return await Promise.race([operation, new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error(`${label} timed out.`)), timeoutMs);
    })]);
  } finally {
    clearTimeout(timeout);
  }
}
async function waitForPresentation(deadline = Date.now() + visualTimeoutMs) {
  // A retained static app need not submit three GPU frames after an input.
  // Let browser/host input and presentation callbacks run; pixel and file
  // assertions below verify the actual result without demanding idle redraws.
  const remaining = Math.max(1, deadline - Date.now());
  await diagnosticDeadline(page.evaluate(timeoutMs => new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('Browser presentation callbacks stalled.')), timeoutMs);
    requestAnimationFrame(() => requestAnimationFrame(() => {
      clearTimeout(timeout);
      resolve();
    }));
  }), remaining), 'Browser presentation callbacks', remaining);
}
async function clickUntilEvent(eventName, x, y, timeout = 30_000) {
  let observed;
  let failure;
  const pending = page.waitForEvent(eventName, { timeout }).then(
    value => { observed = value; },
    error => { failure = error; });
  const deadline = Date.now() + timeout;
  // File operations re-enable the toolbar after their asynchronous completion,
  // which can be later than the browser's download/filechooser event.
  while (!observed && !failure && Date.now() < deadline) {
    await diagnosticDeadline(page.mouse.click(x, y), `${eventName} button input`, Math.max(1, deadline - Date.now()));
    await Promise.race([pending, new Promise(resolve => setTimeout(resolve, 500))]);
  }
  await pending;
  if (failure) throw failure;
  return observed;
}
try {
  const args = ['--enable-unsafe-webgpu'];
  if (softwareRendering) {
    if (process.platform === 'linux') {
      // Select ANGLE and WebGPU independently. Do not disable Vulkan surfaces:
      // SwiftShader can render/read back correctly while page presentation is blank.
      // Preserve the pinned Playwright runner's screenshot feature when adding
      // Vulkan: Chromium uses the final --enable-features argument.
      args.push('--enable-features=Vulkan,CDPScreenshotNewSurface', '--use-gl=angle', '--use-angle=swiftshader',
        '--use-vulkan=swiftshader', '--use-webgpu-adapter=swiftshader');
    } else {
      args.push('--use-angle=swiftshader');
    }
  }
  const launchOptions = {
    channel: process.env.PROGPU_CAD_BROWSER_CHANNEL ?? 'chromium',
    headless: true,
    args,
  };
  // Qualify presentation in an independent browser process. Its device/cache
  // lifetime must not alter the actual app's cold-start workload.
  const presentationBrowser = await chromium.launch(launchOptions);
  try {
    const presentationPage = await presentationBrowser.newPage({
      viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2,
    });
    await verifyWebGpuPresentation(presentationPage, `http://127.0.0.1:${server.address().port}`, evidence);
  } finally {
    await presentationBrowser.close();
  }
  browser = await chromium.launch(launchOptions);
  const diagnosticsSession = await browser.newBrowserCDPSession();
  try {
    const gpu = await diagnosticsSession.send('SystemInfo.getInfo');
    const version = await diagnosticsSession.send('Browser.getVersion');
    await fs.writeFile(path.join(evidence, 'gpu.json'), JSON.stringify({ args, version, ...gpu }, null, 2));
  } finally {
    await diagnosticsSession.detach();
  }
  page = await browser.newPage({
    viewport: { width: 1280, height: 800 }, deviceScaleFactor: appDeviceScaleFactor,
  });
  const recordError = message => { errors.push(message); console.error(message); };
  page.on('pageerror', error => recordError(error.message));
  page.on('console', message => {
    browserLog.push({ type: message.type(), text: message.text() });
    if (message.type() === 'error') recordError(message.text());
  });
  // Exercise the supported input/download fallbacks without native OS dialogs.
  await page.addInitScript(() => { globalThis.showOpenFilePicker = undefined; });
  await page.goto(`http://127.0.0.1:${server.address().port}/?progpuSavePicker=download`,
    { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => Number(document.querySelector('#counter-frames')?.textContent) >= 3,
    undefined, { timeout: 120_000 });
  await fs.writeFile(path.join(evidence, 'pre-screenshot-state.json'),
    JSON.stringify(await diagnosticDeadline(captureHostState(), 'Initial host-state capture'), null, 2) + '\n');
  // The host's frame counter can advance before application launch completes.
  // Inspect the default scene through the actual WebGPU canvas readback. Reuse
  // the PNG decoder bundled with our pinned Playwright dependency.
  let visiblePixels = 0;
  let backgroundPixels = 0;
  let initialDrawingCapture;
  const firstDrawingStarted = Date.now();
  const firstDrawingDeadline = firstDrawingStarted + 120_000;
  while ((visiblePixels < 100 || backgroundPixels < 1000) && Date.now() < firstDrawingDeadline) {
    // A cold software-rendered capture can outlast Playwright's 30-second
    // default. Use the remaining startup budget, without extending that budget
    // for retries or changing the required visible/background pixel counts.
    initialDrawingCapture = await captureCadCanvas(Math.max(1, firstDrawingDeadline - Date.now()));
    const pixels = browserUtilities.PNG.sync.read(initialDrawingCapture).data;
    visiblePixels = 0;
    backgroundPixels = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i] > 160 && pixels[i + 1] > 160 && pixels[i + 2] > 160) visiblePixels++;
      if (pixels[i] < 80 && pixels[i + 1] < 80 && pixels[i + 2] < 80) backgroundPixels++;
    }
    if (visiblePixels < 100 || backgroundPixels < 1000) await new Promise(resolve => setTimeout(resolve, 500));
  }
  assert.ok(visiblePixels >= 100 && backgroundPixels >= 1000,
    'The representative CAD drawing remained blank (light or dark).');
  assert.ok(initialDrawingCapture, 'The representative CAD drawing produced no evidence capture.');
  await fs.writeFile(path.join(evidence, 'startup-capture.json'), JSON.stringify({
    elapsedMs: Date.now() - firstDrawingStarted, budgetMs: 120_000,
    visiblePixels, backgroundPixels,
  }, null, 2) + '\n');
  assert.deepEqual(errors, []);
  await fs.writeFile(path.join(evidence, 'initial.png'), initialDrawingCapture);
  // File actions and basic edits occupy only the top 104 logical pixels.
  await page.mouse.click(1210, 22); // More tools, pinned at the right edge.
  await waitForPresentation();
  await fs.writeFile(path.join(evidence, 'expanded-tools.png'), await captureCadCanvas());
  await page.mouse.click(1210, 22); // Fewer tools.
  await waitForPresentation();
  await page.mouse.move(700, 400);
  const beforeZoom = await captureCadCanvas();
  const beforeZoomPixels = browserUtilities.PNG.sync.read(beforeZoom).data;
  await page.mouse.wheel(0, -250);
  const zoomDeadline = Date.now() + visualTimeoutMs;
  // The bounded pixel poll is the readiness check. Waiting for additional
  // animation callbacks first consumes the same deadline without checking
  // output, and can queue expensive software-rendered frames ahead of capture.
  let afterZoom = beforeZoom;
  let afterZoomPixels = beforeZoomPixels;
  for (let attempt = 0; attempt < 30 && Date.now() < zoomDeadline; attempt++) {
    afterZoom = await captureCadCanvas(Math.max(1, zoomDeadline - Date.now()));
    afterZoomPixels = browserUtilities.PNG.sync.read(afterZoom).data;
    if (!afterZoomPixels.equals(beforeZoomPixels)) break;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await fs.writeFile(path.join(evidence, 'zoomed.png'), afterZoom);
  assert.ok(!afterZoomPixels.equals(beforeZoomPixels), 'Wheel input did not change the CAD drawing.');
  await page.mouse.down({ button: 'middle' });
  await page.mouse.move(780, 450, { steps: 5 });
  await page.mouse.up({ button: 'middle' });
  const panDeadline = Date.now() + visualTimeoutMs;
  let afterPan = afterZoom;
  let afterPanPixels = afterZoomPixels;
  for (let attempt = 0; attempt < 30 && Date.now() < panDeadline; attempt++) {
    afterPan = await captureCadCanvas(Math.max(1, panDeadline - Date.now()));
    afterPanPixels = browserUtilities.PNG.sync.read(afterPan).data;
    if (!afterPanPixels.equals(afterZoomPixels)) break;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await fs.writeFile(path.join(evidence, 'panned.png'), afterPan);
  assert.ok(!afterPanPixels.equals(afterZoomPixels), 'Middle-button drag did not change the CAD drawing.');
  const download = await clickUntilEvent('download', 195, 22, 60_000); // Save As.
  assert.match(download.suggestedFilename(), /\.dxf$/i);
  const savedDrawing = path.join(evidence, 'roundtrip.dxf');
  await download.saveAs(savedDrawing);
  assert.ok((await fs.stat(savedDrawing)).size > 100, 'Saved DXF is empty.');
  function modelEntities(bytes) {
    const lines = bytes.toString('utf8').trim().split(/\r?\n/).map(line => line.trim());
    const entities = [];
    let inEntities = false;
    let recordType;
    let paperSpace = false;
    let tags = [];
    const appendRecord = () => {
      if (recordType && !paperSpace && !['SEQEND', 'ATTRIB', 'VERTEX'].includes(recordType)) {
        entities.push({ type: recordType, tags });
      }
    };
    for (let i = 0; i < lines.length - 1; i += 2) {
      if (lines[i] === '2' && lines[i + 1] === 'ENTITIES') inEntities = true;
      else if (inEntities && lines[i] === '0') {
        appendRecord();
        if (lines[i + 1] === 'ENDSEC') break;
        recordType = lines[i + 1];
        paperSpace = false;
        tags = [];
      } else if (inEntities) {
        tags.push([lines[i], lines[i + 1]]);
        if (lines[i] === '67') paperSpace = lines[i + 1] === '1';
      }
    }
    return entities;
  }
  function assertColumnedText(entities) {
    const texts = entities.filter(entity => entity.type === 'MTEXT');
    assert.equal(texts.length, 1);
    const tags = texts[0].tags;
    assert.equal(tags.filter(([code]) => code === '1' || code === '3')
      .map(([, value]) => value).join(''), 'Column one\\NColumn two');
    const embedded = tags.findIndex(([code, value]) => code === '101' && value === 'Embedded Object');
    assert.ok(embedded >= 0, 'The sample MTEXT lost its column specification.');
    const columns = tags.slice(embedded + 1);
    assert.equal(Number(columns.find(([code]) => code === '72')?.[1]), 2);
    assert.deepEqual(columns.filter(([code]) => code === '46')
      .map(([, value]) => Number(value)), [12, 12]);
  }
  function assertImageClips(entities) {
    for (const entity of entities.filter(value => value.type === 'WIPEOUT' || value.type === 'IMAGE')) {
      const values = code => entity.tags.filter(([tag]) => tag === code).map(([, value]) => Number(value));
      const xs = values('14');
      const ys = values('24');
      assert.equal(xs.length, values('91')[0], `${entity.type}: clip vertex count is inconsistent.`);
      assert.equal(ys.length, xs.length);
      if (values('71')[0] === 1) {
        assert.equal(xs.length, 2, `${entity.type}: rectangle must retain two opposite corners.`);
      } else {
        assert.ok(xs.length >= 4, `${entity.type}: a closed polygon needs three distinct corners.`);
        assert.deepEqual([xs[0], ys[0]], [xs.at(-1), ys.at(-1)]);
        for (let i = 1; i < xs.length; i++) {
          assert.ok(xs[i] !== xs[i - 1] || ys[i] !== ys[i - 1],
            `${entity.type}: saving introduced a collapsed clipping edge.`);
        }
      }
    }
  }
  const savedEntities = modelEntities(await fs.readFile(savedDrawing));
  const savedTypes = savedEntities.map(entity => entity.type).sort();
  assertColumnedText(savedEntities);
  assertImageClips(savedEntities);
  assert.equal(savedTypes.length, 16, 'The sample lost an entity during serialization.');
  assert.ok(savedTypes.includes('IMAGE'), 'The sample raster image was not serialized.');
  assert.ok(savedTypes.includes('MTEXT'), 'The sample columned text was not serialized.');
  const chooser = await clickUntilEvent('filechooser', 70, 22); // Open DXF/DWG.
  await chooser.setFiles(savedDrawing, { timeout: 30_000 });
  // Saving is disabled while the document loads. Retry the button until the
  // load completes, then verify the new session name and entity inventory.
  const reopenedDownload = await clickUntilEvent('download', 195, 22);
  assert.equal(reopenedDownload.suggestedFilename(), 'roundtrip.dxf',
    'Open did not replace the current document.');
  const reopenedDrawing = path.join(evidence, 'reopened.dxf');
  await reopenedDownload.saveAs(reopenedDrawing);
  const reopenedEntities = modelEntities(await fs.readFile(reopenedDrawing));
  assert.deepEqual(reopenedEntities.map(entity => entity.type).sort(), savedTypes);
  assertColumnedText(reopenedEntities);
  assertImageClips(reopenedEntities);

  // Exercise ordinary UI input, not a test-only document mutation seam. Saved
  // entity coordinates are the oracle for edits and history; screenshots alone
  // cannot establish that the live document changed or survived serialization.
  const entityHandle = entity => entity.tags.find(([code]) => code === '5')?.[1];
  const originalHandles = new Set(reopenedEntities.map(entityHandle));
  const lineCoordinates = entity => ['10', '20', '30', '11', '21', '31'].map(code =>
    Number(entity.tags.find(([tag]) => tag === code)?.[1] ?? 0));
  async function saveEdit(name, count) {
    const editDownload = await clickUntilEvent('download', 195, 22, 60_000);
    const file = path.join(evidence, name + '.dxf');
    await editDownload.saveAs(file);
    const entities = modelEntities(await fs.readFile(file));
    assert.equal(entities.length, count, `${name}: unexpected model-space entity count.`);
    assertColumnedText(entities);
    assertImageClips(entities);
    for (const original of reopenedEntities) {
      assert.deepEqual(entities.find(entity => entityHandle(entity) === entityHandle(original)),
        original, `${name}: an unedited entity changed.`);
    }
    console.log(JSON.stringify({ editStage: name, entityCount: entities.length }));
    return { file, entities, fileName: editDownload.suggestedFilename(),
      added: entities.filter(entity => !originalHandles.has(entityHandle(entity))) };
  }
  async function editClick(x, y) {
    await page.mouse.click(x, y);
    await waitForPresentation();
  }
  function assertTranslation(before, after, dx, dy, label) {
    const expected = before.map((value, i) => value + (i % 3 === 0 ? dx : i % 3 === 1 ? dy : 0));
    for (let i = 0; i < expected.length; i++) {
      // Pointer unprojection uses the host's float viewport. Bound only that
      // input rounding to 1/256 logical pixel; saved/reopened values below must
      // still match exactly, and a plan edit must retain Z exactly.
      const tolerance = i % 3 === 2 ? 0 : Math.abs(authored[3] - authored[0]) / 120 / 256;
      assert.ok(Math.abs(after[i] - expected[i]) <= tolerance,
        `${label}: coordinate ${i} changed incorrectly (actual ${after[i]}, expected ${expected[i]}).`);
    }
  }
  await editClick(285, 22); // Fit: use a known drawing-space camera for picks.
  await editClick(40, 68); // Line.
  await editClick(120, 190);
  await editClick(240, 220);
  await page.keyboard.press('Escape'); // Finish one accepted segment.
  await waitForPresentation();
  const created = await saveEdit('edit-created', 17);
  assert.equal(created.added.length, 1);
  assert.equal(created.added[0].type, 'LINE');
  const authored = lineCoordinates(created.added[0]);
  assert.ok(Math.abs(authored[3] - authored[0]) > 1e-8, 'The new line is degenerate.');
  await editClick(360, 22); // Undo.
  assert.equal((await saveEdit('edit-undone', 16)).added.length, 0);
  await editClick(435, 22); // Redo.
  const redone = await saveEdit('edit-redone', 17);
  assert.deepEqual(lineCoordinates(redone.added[0]), authored);

  await editClick(580, 68); // Clear selection.
  await editClick(180, 205); // Pick the new line's midpoint.
  await editClick(365, 68); // Move points.
  await editClick(180, 205);
  await editClick(230, 255);
  const moved = await saveEdit('edit-moved', 17);
  const translated = lineCoordinates(moved.added[0]);
  const dx = (authored[3] - authored[0]) * 50 / 120;
  const dy = (authored[4] - authored[1]) * 50 / 30;
  assertTranslation(authored, translated, dx, dy, 'Move');

  await editClick(475, 68); // Copy points, using the retained selection.
  await editClick(230, 255);
  await editClick(280, 305);
  await page.keyboard.press('Escape'); // Finish after the placed copy.
  await waitForPresentation();
  const copied = await saveEdit('edit-copied', 18);
  const copy = copied.added.find(entity => entityHandle(entity) !== entityHandle(moved.added[0]));
  assert.ok(copy && copy.type === 'LINE', 'Copy did not create a distinct line.');
  assertTranslation(translated, lineCoordinates(copy), dx, dy, 'Copy');
  await editClick(580, 68); // Clear selection, then select only the copy.
  await editClick(280, 305);
  await editClick(515, 22); // Delete.
  const deleted = await saveEdit('edit-deleted', 17);
  assert.equal(deleted.added.length, 1);
  assert.deepEqual(lineCoordinates(deleted.added[0]), translated);
  await editClick(360, 22); // Undo deletion.
  const restored = await saveEdit('edit-delete-undone', 18);
  assert.ok(restored.added.some(entity => JSON.stringify(lineCoordinates(entity)) ===
    JSON.stringify(lineCoordinates(copy))), 'Undo deletion did not restore the copied geometry.');
  await editClick(435, 22); // Redo deletion.
  const finalEdit = await saveEdit('edit-final', 17);
  assert.deepEqual(lineCoordinates(finalEdit.added[0]), translated);
  const editChooser = await clickUntilEvent('filechooser', 70, 22);
  await diagnosticDeadline(editChooser.setFiles(finalEdit.file, { timeout: 30_000 }), 'Edited file input', 30_000);
  const editReopened = await saveEdit('edit-reopened', 17);
  assert.equal(editReopened.fileName, 'edit-final.dxf', 'Opening the edited file did not replace the session.');
  assert.deepEqual(lineCoordinates(editReopened.added[0]), translated);
  await fs.writeFile(path.join(evidence, 'edited.png'), await captureCadCanvas());

  // An invalid primitive must produce a diagnostic, not hang exception
  // propagation or prevent opening a subsequent valid drawing. Construct the
  // old duplicate-closure defect independently from the now-correct writer.
  const malformedLines = (await fs.readFile(finalEdit.file, 'utf8')).split(/\r?\n/);
  let wipeoutStart = -1;
  let wipeoutEnd = -1;
  for (let i = 0; i < malformedLines.length - 1; i += 2) {
    if (malformedLines[i].trim() !== '0') continue;
    if (wipeoutStart >= 0) { wipeoutEnd = i; break; }
    if (malformedLines[i + 1].trim() === 'WIPEOUT') wipeoutStart = i;
  }
  assert.ok(wipeoutStart >= 0 && wipeoutEnd > wipeoutStart);
  let countIndex = -1;
  let firstX;
  let firstY;
  for (let i = wipeoutStart + 2; i < wipeoutEnd; i += 2) {
    const code = malformedLines[i].trim();
    if (code === '91') countIndex = i + 1;
    if (code === '14' && firstX === undefined) firstX = malformedLines[i + 1];
    if (code === '24' && firstY === undefined) firstY = malformedLines[i + 1];
  }
  assert.ok(countIndex >= 0 && firstX !== undefined && firstY !== undefined);
  malformedLines[countIndex] = String(Number(malformedLines[countIndex]) + 1);
  malformedLines.splice(wipeoutEnd, 0, ' 14', firstX, ' 24', firstY);
  const malformedFile = path.join(evidence, 'invalid-clip.dxf');
  await fs.writeFile(malformedFile, malformedLines.join('\n'));
  const malformedChooser = await clickUntilEvent('filechooser', 70, 22);
  await diagnosticDeadline(malformedChooser.setFiles(malformedFile, { timeout: 30_000 }), 'Malformed file input', 30_000);
  const malformedDownload = await clickUntilEvent('download', 195, 22, 60_000);
  assert.equal(malformedDownload.suggestedFilename(), 'invalid-clip.dxf');
  const malformedSaved = path.join(evidence, 'invalid-clip-resaved.dxf');
  await malformedDownload.saveAs(malformedSaved);
  assert.deepEqual(modelEntities(await fs.readFile(malformedSaved)),
    modelEntities(await fs.readFile(malformedFile)), 'Rendering rejection mutated the source document.');
  const recoveryChooser = await clickUntilEvent('filechooser', 70, 22);
  await diagnosticDeadline(recoveryChooser.setFiles(finalEdit.file, { timeout: 30_000 }), 'Recovery file input', 30_000);
  const recovered = await saveEdit('edit-recovered', 17);
  assert.equal(recovered.fileName, 'edit-final.dxf');
  assert.deepEqual(lineCoordinates(recovered.added[0]), translated);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('#progpu-canvas');
    return canvas.width === Math.round(1440 * devicePixelRatio) &&
      canvas.height === Math.round(900 * devicePixelRatio);
  }, undefined, { timeout: visualTimeoutMs });
  await waitForPresentation();
  await fs.writeFile(path.join(evidence, 'resized.png'), await captureCadCanvas());
  assert.deepEqual(errors, []);
  const result = await page.evaluate(() => ({
    frames: Number(document.querySelector('#counter-frames').textContent),
    dispatches: Number(document.querySelector('#counter-dispatches').textContent),
    width: document.querySelector('#progpu-canvas').width,
    height: document.querySelector('#progpu-canvas').height,
  }));
  assert.ok(result.dispatches > 0, 'The CAD app submitted no GPU commands.');
  result.savedEntityTypes = savedTypes;
  result.reopenedFileName = reopenedDownload.suggestedFilename();
  result.editing = ['line', 'undo', 'redo', 'selection', 'move', 'copy', 'delete', 'save', 'reopen'];
  result.malformedClipRecovery = true;
  result.visualTimeoutMs = visualTimeoutMs;
  result.deviceScaleFactor = appDeviceScaleFactor;
  await fs.writeFile(path.join(evidence, 'result.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result));
} catch (error) {
  console.error('Browser errors:', errors);
  console.error('Original smoke failure:', error);
  await fs.writeFile(path.join(evidence, 'failure.json'), JSON.stringify({
    name: error.name, message: error.message, stack: error.stack,
  }, null, 2) + '\n');
  await fs.writeFile(path.join(evidence, 'errors.json'), JSON.stringify(errors, null, 2) + '\n');
  if (page && !page.isClosed()) {
    // Diagnostic failures must not mask the assertion/startup error. Capture
    // DOM state before attempting a potentially stalled browser screenshot.
    try {
      const state = await diagnosticDeadline(captureHostState(), 'Failure-state capture');
      await fs.writeFile(path.join(evidence, 'state.json'), JSON.stringify(state, null, 2) + '\n');
    } catch (diagnosticError) {
      console.error('Failure-state capture:', diagnosticError.message);
    }
    try {
      const canvasSnapshot = await diagnosticDeadline(page.evaluate(() =>
        document.querySelector('#progpu-canvas')?.toDataURL('image/png')), 'Failure canvas readback');
      if (canvasSnapshot?.startsWith('data:image/png;base64,'))
        await fs.writeFile(path.join(evidence, 'failed-canvas.png'), Buffer.from(canvasSnapshot.split(',')[1], 'base64'));
    } catch (diagnosticError) {
      console.error('Failure canvas readback:', diagnosticError.message);
    }
    try {
      await page.screenshot({ path: path.join(evidence, 'failed.png'), timeout: 10_000 });
    } catch (diagnosticError) {
      console.error('Failure screenshot:', diagnosticError.message);
    }
  }
  throw error;
} finally {
  await fs.writeFile(path.join(evidence, 'console.json'), JSON.stringify(browserLog, null, 2) + '\n');
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
