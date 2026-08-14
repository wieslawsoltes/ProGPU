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
    semanticGeometry:
      document.body.dataset.progpuNativeSemanticGeometry,
    deviceRecovery: document.body.dataset.progpuNativeDeviceRecovery,
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.deepEqual(contract, {
    status: "passed",
    semanticCommands: "3",
    semanticResources: "3",
    semanticDraws: "1",
    rendererSubmissions: "1",
    evidenceTarget: "offscreen-texture-readback",
    backendAbi: "3",
    explicitTimeline: "0",
    coverageMasks: "passed",
    roundedMasks: "passed",
    stateMasks: "passed",
    semanticGeometry: "passed",
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
      leftOnly: sample(150, 180),
      overlap: sample(300, 180),
      rightOnly: sample(480, 180),
      outside: sample(60, 180)
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
  const cyan = (pixel) => pixel[0] <= 16 && pixel[1] >= 90 &&
    pixel[2] >= 110 && opaque(pixel);
  const magenta = (pixel) => pixel[0] >= 110 && pixel[1] <= 50 &&
    pixel[2] >= 55 && opaque(pixel);
  const perDrawOverlap = (pixel) => near(pixel[0], 133, 18) &&
    near(pixel[1], 78, 14) && near(pixel[2], 138, 18) && opaque(pixel);
  const clear = (pixel) => pixel[0] <= 16 && pixel[1] <= 16 &&
    pixel[2] <= 16 && opaque(pixel);
  assert.ok(cyan(pixels.leftOnly),
    `Browser per-draw mask lost the cyan source: ${diagnostics}`);
  assert.ok(perDrawOverlap(pixels.overlap),
    `Browser mask was not applied independently before overlap blending: ${diagnostics}`);
  assert.ok(magenta(pixels.rightOnly),
    `Browser per-draw mask lost the magenta source: ${diagnostics}`);
  assert.ok(clear(pixels.outside),
    `Browser per-draw mask escaped its transformed bounds: ${diagnostics}`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU native browser contract ${contract.status}: ` +
    `${contract.semanticCommands} semantic commands, ` +
    `${contract.semanticDraws} GPU draw, exact per-draw vector masks, ` +
    `retained rounded masks, and coverage masks verified.\n`);
} finally {
  await browser.close();
}
