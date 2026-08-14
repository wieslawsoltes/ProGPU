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
    coverageMasks: document.body.dataset.progpuNativeCoverageMasks,
    roundedMasks: document.body.dataset.progpuNativeRoundedMasks,
    stateMasks: document.body.dataset.progpuNativeStateMasks,
    stateMaskMedia: document.body.dataset.progpuNativeStateMaskMedia,
    semanticGeometry:
      document.body.dataset.progpuNativeSemanticGeometry,
    nativeSceneBuilder:
      document.body.dataset.progpuNativeSceneBuilder,
    nativePngDecode:
      document.body.dataset.progpuNativePngDecode,
    incrementalUpdate:
      document.body.dataset.progpuNativeIncrementalUpdate,
    deviceRecovery: document.body.dataset.progpuNativeDeviceRecovery,
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.deepEqual(contract, {
    status: "passed",
    semanticCommands: "2",
    semanticResources: "4",
    semanticDraws: "2",
    rendererSubmissions: "1",
    evidenceTarget: "offscreen-texture-readback",
    backendAbi: "3",
    explicitTimeline: "0",
    coverageMasks: "passed",
    roundedMasks: "passed",
    stateMasks: "passed",
    stateMaskMedia: "passed",
    semanticGeometry: "passed",
    nativeSceneBuilder: "passed",
    nativePngDecode: "passed",
    incrementalUpdate: "passed",
    deviceRecovery: "passed",
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
      image: sample(250, 250),
      glyph: sample(100, 150),
      maskInside: sample(310, 180),
      maskOutside: sample(330, 180),
      right: sample(480, 180)
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
  const matrixImage = (pixel) => near(pixel[0], 224, 4) &&
    near(pixel[1], 16, 4) && near(pixel[2], 96, 4) && opaque(pixel);
  const glyphMagenta = (pixel) => near(pixel[0], 255, 4) &&
    near(pixel[1], 32, 4) && near(pixel[2], 192, 4) && opaque(pixel);
  const clear = (pixel) => pixel[0] <= 16 && pixel[1] <= 16 &&
    pixel[2] <= 16 && opaque(pixel);
  assert.ok(matrixImage(pixels.image),
    `Browser per-draw mask lost the color-matrix image: ${diagnostics}`);
  assert.ok(glyphMagenta(pixels.glyph),
    `Browser per-draw mask lost the retained color glyph: ${diagnostics}`);
  assert.ok(matrixImage(pixels.maskInside),
    `Browser coverage mask clipped its included half: ${diagnostics}`);
  assert.ok(clear(pixels.maskOutside) && clear(pixels.right),
    `Browser coverage mask escaped its excluded half: ${diagnostics}`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU native browser contract ${contract.status}: ` +
    `${contract.semanticCommands} semantic commands, ` +
    `${contract.semanticDraws} GPU draws, exact per-draw vector/glyph/image ` +
    `masks, retained rounded masks, and coverage masks verified.\n`);
} finally {
  await browser.close();
}
