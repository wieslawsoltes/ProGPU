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
function screenshot(options) {
  return page.screenshot({ timeout: visualTimeoutMs, ...options });
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
    await page.mouse.click(x, y);
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
  page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2 });
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
  const drawing = { x: 300, y: 160, width: 680, height: 500 };
  // The host's frame counter can advance before application launch completes.
  // Inspect the default scene, not toolbar chrome or opaque target alpha. Reuse
  // the PNG decoder bundled with our pinned Playwright dependency.
  let visiblePixels = 0;
  let backgroundPixels = 0;
  const firstDrawingStarted = Date.now();
  const firstDrawingDeadline = firstDrawingStarted + 120_000;
  while ((visiblePixels < 100 || backgroundPixels < 1000) && Date.now() < firstDrawingDeadline) {
    // A cold software-rendered capture can outlast Playwright's 30-second
    // default. Use the remaining startup budget, without extending that budget
    // for retries or changing the required visible/background pixel counts.
    const pixels = browserUtilities.PNG.sync.read(await screenshot({
      clip: drawing, timeout: Math.max(1, firstDrawingDeadline - Date.now()),
    })).data;
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
  await fs.writeFile(path.join(evidence, 'startup-capture.json'), JSON.stringify({
    elapsedMs: Date.now() - firstDrawingStarted, budgetMs: 120_000,
    visiblePixels, backgroundPixels,
  }, null, 2) + '\n');
  assert.deepEqual(errors, []);
  await screenshot({ path: path.join(evidence, 'initial.png') });
  // File actions and basic edits occupy only the top 104 logical pixels.
  await page.mouse.click(1210, 22); // More tools, pinned at the right edge.
  await waitForPresentation();
  await screenshot({ path: path.join(evidence, 'expanded-tools.png') });
  await page.mouse.click(1210, 22); // Fewer tools.
  await waitForPresentation();
  await page.mouse.move(700, 400);
  const beforeZoom = await screenshot({ clip: drawing });
  await page.mouse.wheel(0, -250);
  const zoomDeadline = Date.now() + visualTimeoutMs;
  await waitForPresentation(zoomDeadline);
  let afterZoom = beforeZoom;
  for (let attempt = 0; attempt < 30 && Date.now() < zoomDeadline; attempt++) {
    afterZoom = await screenshot({ clip: drawing, timeout: Math.max(1, zoomDeadline - Date.now()) });
    if (!afterZoom.equals(beforeZoom)) break;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await fs.writeFile(path.join(evidence, 'zoomed.png'), afterZoom);
  assert.ok(!afterZoom.equals(beforeZoom), 'Wheel input did not change the CAD drawing.');
  await page.mouse.down({ button: 'middle' });
  await page.mouse.move(780, 450, { steps: 5 });
  await page.mouse.up({ button: 'middle' });
  const panDeadline = Date.now() + visualTimeoutMs;
  await waitForPresentation(panDeadline);
  let afterPan = afterZoom;
  for (let attempt = 0; attempt < 30 && Date.now() < panDeadline; attempt++) {
    afterPan = await screenshot({ clip: drawing, timeout: Math.max(1, panDeadline - Date.now()) });
    if (!afterPan.equals(afterZoom)) break;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await fs.writeFile(path.join(evidence, 'panned.png'), afterPan);
  assert.ok(!afterPan.equals(afterZoom), 'Middle-button drag did not change the CAD drawing.');
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
  const savedEntities = modelEntities(await fs.readFile(savedDrawing));
  const savedTypes = savedEntities.map(entity => entity.type).sort();
  assertColumnedText(savedEntities);
  assert.equal(savedTypes.length, 16, 'The sample lost an entity during serialization.');
  assert.ok(savedTypes.includes('IMAGE'), 'The sample raster image was not serialized.');
  assert.ok(savedTypes.includes('MTEXT'), 'The sample columned text was not serialized.');
  const chooser = await clickUntilEvent('filechooser', 70, 22); // Open DXF/DWG.
  await chooser.setFiles(savedDrawing);
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
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('#progpu-canvas');
    return canvas.width === 2880 && canvas.height === 1800;
  }, undefined, { timeout: visualTimeoutMs });
  await waitForPresentation();
  await screenshot({ path: path.join(evidence, 'resized.png') });
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
  result.visualTimeoutMs = visualTimeoutMs;
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
