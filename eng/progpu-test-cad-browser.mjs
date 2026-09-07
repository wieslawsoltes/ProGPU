import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '../src/ProGPU.Native/browser/node_modules/playwright/index.mjs';

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
try {
  const args = ['--enable-unsafe-webgpu'];
  if (process.env.PROGPU_CAD_BROWSER_USE_SWIFTSHADER === '1') args.push('--use-angle=swiftshader');
  browser = await chromium.launch({
    channel: process.env.PROGPU_CAD_BROWSER_CHANNEL ?? 'chromium',
    headless: true,
    args,
  });
  page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2 });
  const recordError = message => { errors.push(message); console.error(message); };
  page.on('pageerror', error => recordError(error.message));
  page.on('console', message => { if (message.type() === 'error') recordError(message.text()); });
  // Exercise the supported input/download fallbacks without native OS dialogs.
  await page.addInitScript(() => { globalThis.showOpenFilePicker = undefined; });
  await page.goto(`http://127.0.0.1:${server.address().port}/?progpuSavePicker=download`,
    { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => Number(document.querySelector('#counter-frames')?.textContent) >= 3,
    undefined, { timeout: 120_000 });
  assert.deepEqual(errors, []);
  await page.screenshot({ path: path.join(evidence, 'initial.png') });
  // The current sample has a tall command area; wheel inside the drawing itself.
  const drawing = { x: 300, y: 560, width: 680, height: 200 };
  await page.mouse.move(700, 680);
  const beforeZoom = await page.screenshot({ clip: drawing });
  const framesBeforeZoom = await page.locator('#counter-frames').textContent();
  await page.mouse.wheel(0, -250);
  await page.waitForFunction(before =>
    Number(document.querySelector('#counter-frames').textContent) >= Number(before) + 3,
    framesBeforeZoom);
  let afterZoom;
  for (let attempt = 0; attempt < 30; attempt++) {
    afterZoom = await page.screenshot({ clip: drawing });
    if (!afterZoom.equals(beforeZoom)) break;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await fs.writeFile(path.join(evidence, 'zoomed.png'), afterZoom);
  assert.ok(!afterZoom.equals(beforeZoom), 'Wheel input did not change the CAD drawing.');
  const downloadPromise = page.waitForEvent('download', { timeout: 60_000 });
  await page.mouse.click(650, 22); // Save As, in the sample's first command row.
  const download = await downloadPromise;
  assert.match(download.suggestedFilename(), /\.dxf$/i);
  const savedDrawing = path.join(evidence, 'roundtrip.dxf');
  await download.saveAs(savedDrawing);
  assert.ok((await fs.stat(savedDrawing)).size > 100, 'Saved DXF is empty.');
  function modelEntityTypes(bytes) {
    const lines = bytes.toString('utf8').trim().split(/\r?\n/).map(line => line.trim());
    const entities = [];
    let inEntities = false;
    let recordType;
    let paperSpace = false;
    const appendRecord = () => {
      if (recordType && !paperSpace && !['SEQEND', 'ATTRIB', 'VERTEX'].includes(recordType)) {
        entities.push(recordType);
      }
    };
    for (let i = 0; i < lines.length - 1; i += 2) {
      if (lines[i] === '2' && lines[i + 1] === 'ENTITIES') inEntities = true;
      else if (inEntities && lines[i] === '0') {
        appendRecord();
        if (lines[i + 1] === 'ENDSEC') break;
        recordType = lines[i + 1];
        paperSpace = false;
      } else if (inEntities && lines[i] === '67') {
        paperSpace = lines[i + 1] === '1';
      }
    }
    return entities.sort();
  }
  const savedTypes = modelEntityTypes(await fs.readFile(savedDrawing));
  assert.equal(savedTypes.length, 15, 'The sample lost an entity during serialization.');
  assert.ok(savedTypes.includes('IMAGE'), 'The sample raster image was not serialized.');
  const chooserPromise = page.waitForEvent('filechooser', { timeout: 30_000 });
  await page.mouse.click(70, 22); // Open DXF/DWG.
  await (await chooserPromise).setFiles(savedDrawing);
  // Saving is disabled while the document loads. Retry the button until the
  // load completes, then verify the new session name and entity inventory.
  let reopenedDownload;
  const reopenedPromise = page.waitForEvent('download', { timeout: 30_000 })
    .then(value => { reopenedDownload = value; });
  // Attach rejection immediately so a timeout cannot become unhandled while
  // the bounded interaction loop is running.
  reopenedPromise.catch(() => {});
  for (let attempt = 0; attempt < 30 && !reopenedDownload; attempt++) {
    await page.mouse.click(650, 22);
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  await reopenedPromise;
  assert.equal(reopenedDownload.suggestedFilename(), 'roundtrip.dxf',
    'Open did not replace the current document.');
  const reopenedDrawing = path.join(evidence, 'reopened.dxf');
  await reopenedDownload.saveAs(reopenedDrawing);
  assert.deepEqual(modelEntityTypes(await fs.readFile(reopenedDrawing)), savedTypes);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(() => {
    const canvas = document.querySelector('#progpu-canvas');
    return canvas.width === 2880 && canvas.height === 1800;
  }, undefined, { timeout: 30_000 });
  await page.screenshot({ path: path.join(evidence, 'resized.png') });
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
  await fs.writeFile(path.join(evidence, 'result.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result));
} catch (error) {
  console.error('Browser errors:', errors);
  await fs.writeFile(path.join(evidence, 'errors.json'), JSON.stringify(errors, null, 2) + '\n');
  if (page && !page.isClosed()) await page.screenshot({ path: path.join(evidence, 'failed.png'), timeout: 10_000 });
  throw error;
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
