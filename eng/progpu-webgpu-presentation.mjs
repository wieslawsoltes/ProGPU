import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import utilities from '../src/ProGPU.Native/browser/node_modules/playwright-core/lib/utilsBundle.js';

// Test-only environment qualification. A successful GPU submission/readback
// does not prove that Chromium can present that texture into a page screenshot.
export async function verifyWebGpuPresentation(page, origin, evidence) {
  const url = `${origin}/__progpu-presentation-probe`;
  await page.route(url, route => route.fulfill({ contentType: 'text/html', body:
    '<!DOCTYPE html><body style="margin:0"><canvas width="64" height="64"></canvas>' }));
  try {
    await page.goto(url, { waitUntil: 'domcontentloaded' });
    let timer;
    let result;
    try {
      result = await Promise.race([page.evaluate(async () => {
        const adapter = await navigator.gpu?.requestAdapter();
        if (!adapter) throw new Error('WebGPU presentation probe found no adapter.');
        const device = await adapter.requestDevice();
        globalThis.progpuPresentationProbeDevice = device;
        const canvas = document.querySelector('canvas');
        const context = canvas.getContext('webgpu');
        const format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'opaque' });
        const encoder = device.createCommandEncoder();
        const pass = encoder.beginRenderPass({ colorAttachments: [{
          view: context.getCurrentTexture().createView(), loadOp: 'clear', storeOp: 'store',
          clearValue: { r: 1, g: 0, b: 0, a: 1 },
        }] });
        pass.end();
        device.queue.submit([encoder.finish()]);
        await device.queue.onSubmittedWorkDone();
        return { format, adapter: { vendor: adapter.info.vendor, architecture: adapter.info.architecture,
          device: adapter.info.device, description: adapter.info.description }, canvas: canvas.toDataURL() };
      }), new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('WebGPU presentation probe timed out.')), 15_000);
      })]);
    } finally {
      clearTimeout(timer);
    }
    const canvasPng = Buffer.from(result.canvas.split(',')[1], 'base64');
    await fs.writeFile(path.join(evidence, 'presentation-canvas.png'), canvasPng);
    const direct = countRed(canvasPng);
    const report = { format: result.format, adapter: result.adapter, direct };
    // Save adapter/readback evidence even if page-composition capture stalls.
    await fs.writeFile(path.join(evidence, 'presentation.json'), JSON.stringify(report, null, 2));
    const screenshot = await page.screenshot({ clip: { x: 0, y: 0, width: 64, height: 64 }, timeout: 15_000 });
    await fs.writeFile(path.join(evidence, 'presentation-page.png'), screenshot);
    report.presented = countRed(screenshot);
    await fs.writeFile(path.join(evidence, 'presentation.json'), JSON.stringify(report, null, 2));
    assert.equal(direct.red, direct.total, 'The WebGPU clear did not reach the canvas.');
    assert.equal(report.presented.red, report.presented.total,
      'WebGPU rendered the canvas but Chromium did not present it. Check the browser GPU configuration.');
    await page.evaluate(() => globalThis.progpuPresentationProbeDevice.destroy());
  } finally {
    // Failure cleanup belongs to the smoke's browser-close boundary: another
    // evaluation on an unresponsive page must not hide the original error.
    await page.unroute(url).catch(() => {});
  }
}

function countRed(bytes) {
  const png = utilities.PNG.sync.read(bytes);
  let red = 0;
  for (let i = 0; i < png.data.length; i += 4)
    if (png.data[i] > 200 && png.data[i + 1] < 20 && png.data[i + 2] < 20) red++;
  return { red, total: png.width * png.height };
}
