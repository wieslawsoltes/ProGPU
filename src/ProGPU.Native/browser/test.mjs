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
    semanticGeometry:
      document.body.dataset.progpuNativeSemanticGeometry,
    deviceRecovery: document.body.dataset.progpuNativeDeviceRecovery,
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.deepEqual(contract, {
    status: "passed",
    semanticCommands: "3",
    semanticResources: "2",
    semanticDraws: "2",
    rendererSubmissions: "1",
    evidenceTarget: "offscreen-texture-readback",
    backendAbi: "3",
    explicitTimeline: "0",
    coverageMasks: "passed",
    roundedMasks: "passed",
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
      leftStem: sample(230, 113),
      counter: sample(330, 120),
      bridge: sample(340, 180),
      outside: sample(100, 180)
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
  const cyan = (pixel) => near(pixel[0], 0) &&
    near(pixel[1], 217, 24) && near(pixel[2], 255) && opaque(pixel);
  const clear = (pixel) => pixel[0] <= 16 && pixel[1] <= 16 &&
    pixel[2] <= 16 && opaque(pixel);
  assert.ok(cyan(pixels.leftStem),
    `Browser coverage mask lost the left H stem: ${diagnostics}`);
  assert.ok(clear(pixels.counter),
    `Browser coverage mask did not remove the H counter: ${diagnostics}`);
  assert.ok(cyan(pixels.bridge),
    `Browser coverage mask lost the H bridge: ${diagnostics}`);
  assert.ok(clear(pixels.outside),
    `Browser coverage mask escaped its transformed bounds: ${diagnostics}`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU native browser contract ${contract.status}: ` +
    `${contract.semanticCommands} semantic commands, ` +
    `${contract.semanticDraws} GPU draws, retained analytic rounded and ` +
    `coverage masks verified.\n`);
} finally {
  await browser.close();
}
