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
    "--enable-features=Vulkan",
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
    totalSubmissions: document.body.dataset.progpuNativeTotalSubmissions,
    backendAbi: document.body.dataset.progpuNativeBackendAbi,
    explicitTimeline:
      document.body.dataset.progpuNativeExplicitTimeline,
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.deepEqual(contract, {
    status: "passed",
    semanticCommands: "4",
    semanticResources: "2",
    semanticDraws: "3",
    totalSubmissions: "1",
    backendAbi: "3",
    explicitTimeline: "0",
    error: ""
  });
  const screenshotPath = path.join(
    evidenceDirectory,
    "progpu-native-browser-webgpu.png");
  const screenshot = await page.locator("canvas").screenshot({
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
      clear: sample(10, 10),
      destination: sample(80, 60),
      composited: sample(200, 150)
    };
  }, screenshot.toString("base64"));
  const near = (actual, expected, tolerance = 20) =>
    Math.abs(actual - expected) <= tolerance;
  const diagnostics = errors.length === 0
    ? "no WebGPU console errors"
    : errors.join(" | ");
  assert.ok(
    near(pixels.clear[0], 3) && near(pixels.clear[1], 4) &&
      near(pixels.clear[2], 8) && pixels.clear[3] >= 240,
    `Unexpected browser clear pixel: ${pixels.clear}; ${diagnostics}`);
  assert.ok(
    near(pixels.destination[0], 51) &&
      near(pixels.destination[1], 204) &&
      near(pixels.destination[2], 102) &&
      pixels.destination[3] >= 240,
    `Browser semantic destination pixel was lost: ${pixels.destination}; ` +
      diagnostics);
  assert.ok(
    near(pixels.composited[0], 128) &&
      near(pixels.composited[1], 128) &&
      near(pixels.composited[2], 128) &&
      pixels.composited[3] >= 240,
    `Browser semantic isolated layer was not composited: ${pixels.composited}; ` +
      diagnostics);
  contract.pixels = pixels;
  await fs.writeFile(
    path.join(evidenceDirectory, "progpu-native-browser-contract.json"),
    `${JSON.stringify(contract, null, 2)}\n`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU native browser contract ${contract.status}: ` +
    `${contract.semanticCommands} semantic commands, ` +
    `${contract.semanticDraws} GPU draws, isolated layer verified.\n`);
} finally {
  await browser.close();
}
