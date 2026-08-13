import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { chromium } from "playwright";

const url = process.env.PROGPU_NATIVE_BROWSER_URL ??
  "http://127.0.0.1:4173/progpu_native_browser_smoke.html";
const evidenceDirectory = path.resolve(
  process.env.PROGPU_NATIVE_BROWSER_EVIDENCE ??
    "../../../artifacts/progpu-native/browser-evidence");
await fs.mkdir(evidenceDirectory, { recursive: true });

const browser = await chromium.launch({
  channel: "chromium",
  headless: true,
  args: [
    "--enable-unsafe-webgpu",
    "--use-angle=swiftshader"
  ]
});
const page = await browser.newPage({ viewport: { width: 900, height: 680 } });
const errors = [];
page.on("console", (message) => {
  if (message.type() === "error") {
    errors.push(message.text());
  }
});
page.on("pageerror", (error) => errors.push(error.message));

try {
  await page.goto(url, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(
    () => document.body.dataset.progpuNative !== "loading",
    undefined,
    { timeout: 30_000 });
  const contract = await page.evaluate(() => ({
    status: document.body.dataset.progpuNative,
    semanticCommands: document.body.dataset.progpuNativeSemanticCommands,
    semanticResources: document.body.dataset.progpuNativeSemanticResources,
    semanticDraws: document.body.dataset.progpuNativeSemanticDraws,
    rendererSubmissions:
      document.body.dataset.progpuNativeRendererSubmissions,
    evidenceTarget: document.body.dataset.progpuNativeEvidenceTarget,
    backendAbi: document.body.dataset.progpuNativeBackendAbi,
    explicitTimeline:
      document.body.dataset.progpuNativeExplicitTimeline,
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.deepEqual(contract, {
    status: "passed",
    semanticCommands: "6",
    semanticResources: "3",
    semanticDraws: "6",
    rendererSubmissions: "1",
    evidenceTarget: "offscreen-texture-readback",
    backendAbi: "3",
    explicitTimeline: "0",
    error: ""
  }, errors.length === 0 ? "no browser errors" : errors.join(" | "));
  const screenshotPath = path.join(
    evidenceDirectory,
    "progpu-native-browser-webgpu.png");
  await page.waitForFunction(
    () => document.body.dataset.progpuNativeEvidence === "ready",
    undefined,
    { timeout: 30_000 });
  const screenshot = await page.locator("#progpu-native-evidence").screenshot({
    path: screenshotPath
  });
  const pixels = await page.evaluate(async (png) => {
    const image = new Image();
    image.src = `data:image/png;base64,${png}`;
    await image.decode();
    const copy = document.createElement("canvas");
    copy.width = image.naturalWidth;
    copy.height = image.naturalHeight;
    const context = copy.getContext("2d", { willReadFrequently: true });
    context.drawImage(image, 0, 0);
    const sample = (x, y) =>
      Array.from(context.getImageData(x, y, 1, 1).data);
    return {
      outsideLeft: sample(40, 20),
      outsideRight: sample(600, 20),
      boundedLeft: sample(140, 180),
      filteredLeft: sample(200, 180),
      transition: sample(319, 180),
      marker: sample(260, 180),
      filteredRight: sample(360, 180),
      initializedPrevious: sample(120, 300)
    };
  }, screenshot.toString("base64"));
  const near = (actual, expected, tolerance = 20) =>
    Math.abs(actual - expected) <= tolerance;
  contract.pixels = pixels;
  await fs.writeFile(
    path.join(evidenceDirectory, "progpu-native-browser-contract.json"),
    `${JSON.stringify(contract, null, 2)}\n`);
  const browserDiagnostics = errors.length === 0
    ? "no WebGPU console errors"
    : errors.join(" | ");
  const diagnostics = `${browserDiagnostics}; pixels=${JSON.stringify(pixels)}`;
  const opaque = (pixel) => pixel[3] >= 240;
  const red = (pixel) => near(pixel[0], 255) && near(pixel[1], 0) &&
    near(pixel[2], 0) && opaque(pixel);
  const blue = (pixel) => near(pixel[0], 0) && near(pixel[1], 0) &&
    near(pixel[2], 255) && opaque(pixel);
  assert.ok(red(pixels.outsideLeft) && red(pixels.boundedLeft),
    `Browser backdrop escaped or lost its left bound: ${diagnostics}`);
  assert.ok(blue(pixels.outsideRight) && blue(pixels.filteredRight),
    `Browser backdrop escaped or lost its right bound: ${diagnostics}`);
  assert.ok(
    pixels.filteredLeft[0] >= 160 && pixels.filteredLeft[2] <= 96 &&
      opaque(pixels.filteredLeft),
    `Browser backdrop lost its captured left source: ${diagnostics}`);
  assert.ok(
    pixels.transition[0] >= 40 && pixels.transition[2] >= 40 &&
      pixels.transition[0] <= 220 && pixels.transition[2] <= 220 &&
      opaque(pixels.transition),
    `Browser backdrop effect did not filter the parent transition: ` +
      diagnostics);
  assert.ok(
    near(pixels.marker[0], 0) && near(pixels.marker[1], 255) &&
      near(pixels.marker[2], 0) && opaque(pixels.marker),
    `Browser child content was not drawn over the filtered backdrop: ` +
      diagnostics);
  assert.ok(red(pixels.initializedPrevious),
    `Browser backdrop did not initialize from previous pixels: ${diagnostics}`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU native browser contract ${contract.status}: ` +
    `${contract.semanticCommands} semantic commands, ` +
    `${contract.semanticDraws} GPU draws, backdrop effect verified.\n`);
} finally {
  await browser.close();
}
