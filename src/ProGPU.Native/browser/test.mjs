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

const useSwiftShader =
  process.env.PROGPU_NATIVE_BROWSER_USE_SWIFTSHADER !== "0";
const deviceScaleFactor = Number(
  process.env.PROGPU_NATIVE_BROWSER_DEVICE_SCALE_FACTOR ?? "1");
assert.ok(Number.isFinite(deviceScaleFactor) &&
  deviceScaleFactor >= 1 && deviceScaleFactor <= 4,
  `Invalid browser device scale factor ${deviceScaleFactor}.`);
const browserArgs = ["--enable-unsafe-webgpu"];
if (useSwiftShader) {
  browserArgs.push("--use-angle=swiftshader");
}
const browser = await chromium.launch({
  channel: "chromium",
  headless: true,
  args: browserArgs
});
const page = await browser.newPage({
  viewport: { width: 900, height: 680 },
  deviceScaleFactor
});
const errors = [];
page.on("console", (message) => {
  if (message.type() === "error") {
    errors.push(message.text());
  }
});
page.on("pageerror", (error) => errors.push(error.message));

try {
  const testUrl = new URL(url);
  if (useSwiftShader) {
    testUrl.searchParams.set("progpuNativeGpuHitTesting", "0");
  }
  await page.goto(testUrl.toString(), { waitUntil: "domcontentloaded" });
  try {
    await page.waitForFunction(
      () => document.body.dataset.progpuNative !== "loading",
      undefined,
      { timeout: 120_000 });
  } catch (error) {
    const stage = await page.evaluate(() =>
      document.body.dataset.progpuNativeStage ?? "uninitialized");
    const diagnostics = errors.length === 0 ? "no browser errors" :
      errors.join(" | ");
    throw new Error(
      `Browser smoke timed out at native stage '${stage}': ${diagnostics}.`, {
      cause: error
    });
  }
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
    vectorClipMasks:
      document.body.dataset.progpuNativeVectorClipMasks,
    compositeGeometryMasks:
      document.body.dataset.progpuNativeCompositeGeometryMasks,
    directImageSampling:
      document.body.dataset.progpuNativeDirectImageSampling,
    semanticGeometry:
      document.body.dataset.progpuNativeSemanticGeometry,
    nativeSceneBuilder:
      document.body.dataset.progpuNativeSceneBuilder,
    nativePngDecode:
      document.body.dataset.progpuNativePngDecode,
    incrementalUpdate:
      document.body.dataset.progpuNativeIncrementalUpdate,
    deviceRecovery: document.body.dataset.progpuNativeDeviceRecovery,
    gpuHitTesting: document.body.dataset.progpuNativeGpuHitTesting,
    backingWidth: Number(document.body.dataset.progpuNativeBackingWidth),
    backingHeight: Number(document.body.dataset.progpuNativeBackingHeight),
    dpiScale: Number(document.body.dataset.progpuNativeDpiScale),
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
    vectorClipMasks: "passed",
    compositeGeometryMasks: "passed",
    directImageSampling: "passed",
    semanticGeometry: "passed",
    nativeSceneBuilder: "passed",
    nativePngDecode: "passed",
    incrementalUpdate: "passed",
    deviceRecovery: "passed",
    gpuHitTesting: useSwiftShader
      ? "deferred-software-adapter"
      : "passed",
    backingWidth: Math.round(640 * contract.dpiScale),
    backingHeight: Math.round(360 * contract.dpiScale),
    dpiScale: contract.dpiScale,
    error: ""
  }, errors.length === 0 ? "no browser errors" : errors.join(" | "));
  assert.ok(contract.dpiScale >= 1 && contract.dpiScale <= 4,
    `Unexpected browser render scale ${contract.dpiScale}.`);
  assert.equal(
    await page.locator("#progpu-native-evidence").evaluate(
      (canvas) => canvas.width),
    contract.backingWidth,
    "The browser evidence canvas lost its physical-pixel backing width.");
  const screenshotPath = path.join(
    evidenceDirectory,
    "progpu-native-browser-webgpu.png");
  await page.waitForFunction(
    () => document.body.dataset.progpuNativeEvidence === "ready",
    undefined,
    { timeout: 120_000 });
  const screenshot = await page.locator("#progpu-native-evidence").screenshot({
    path: screenshotPath
  });
  assert.ok(screenshot.length > 0, "The browser evidence screenshot is empty.");
  const pixels = await page.locator("#progpu-native-evidence").evaluate(
    (canvas) => {
    const context = canvas.getContext("2d", { willReadFrequently: true });
    const physicalScale = canvas.width / 640;
    const sample = (x, y) =>
      Array.from(context.getImageData(
        Math.round(x * physicalScale),
        Math.round(y * physicalScale),
        1,
        1).data);
    return {
      image: sample(250, 250),
      glyph: sample(100, 150),
      maskInside: sample(310, 180),
      maskOutside: sample(330, 180),
      right: sample(480, 180)
    };
  });
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
  const glyphMagenta = (pixel) => near(pixel[0], 255) &&
    near(pixel[1], 32) && near(pixel[2], 192) && opaque(pixel);
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
    `masks, retained rounded/vector, picture, and composite brush/stroked-geometry ` +
    `masks, coverage masks, and direct image sampling verified.\n`);

  const galleryUrl = new URL(
    "progpu_native_browser_gallery.html",
    testUrl);
  await page.goto(galleryUrl.toString(), { waitUntil: "domcontentloaded" });
  try {
    await page.waitForFunction(
      () => document.body.dataset.progpuNative === "running" &&
        Number(document.body.dataset.progpuNativeFrames) >= 3,
      undefined,
      { timeout: 120_000 });
  } catch (error) {
    const diagnostics = await page.evaluate(() => ({
      status: document.body.dataset.progpuNative,
      error: document.body.dataset.progpuNativeError ?? ""
    }));
    throw new Error(
      `Native gallery did not present its MotionMark scene: ` +
      `${JSON.stringify(diagnostics)}; ${errors.join(" | ")}`, {
      cause: error
    });
  }
  const galleryContract = await page.evaluate(() => {
    const canvas = document.querySelector("#progpu-canvas");
    const rect = canvas.getBoundingClientRect();
    return {
      status: document.body.dataset.progpuNative,
      renderer: document.body.dataset.progpuNativeRenderer,
      presentation: document.body.dataset.progpuNativePresentation,
      aot: document.body.dataset.progpuNativeAot,
      elements: Number(document.body.dataset.progpuNativeElements),
      groups: Number(document.body.dataset.progpuNativeGroups),
      draws: Number(document.body.dataset.progpuNativeDraws),
      streamBytes: Number(document.body.dataset.progpuNativeStreamBytes),
      width: Number(document.body.dataset.progpuNativeBackingWidth),
      height: Number(document.body.dataset.progpuNativeBackingHeight),
      dpiScale: Number(document.body.dataset.progpuNativeDpiScale),
      frames: Number(document.body.dataset.progpuNativeFrames),
      canvasWidth: canvas.width,
      canvasHeight: canvas.height,
      cssWidth: rect.width,
      cssHeight: rect.height,
      error: document.body.dataset.progpuNativeError ?? ""
    };
  });
  assert.equal(galleryContract.status, "running");
  assert.equal(galleryContract.renderer, "pure-cpp-webgpu");
  assert.equal(galleryContract.presentation, "canvas-swapchain");
  assert.equal(galleryContract.aot, "emscripten-wasm");
  assert.equal(galleryContract.elements, 1000);
  assert.ok(galleryContract.groups > 0 &&
    galleryContract.groups <= galleryContract.elements);
  assert.equal(galleryContract.draws, 1);
  assert.ok(galleryContract.streamBytes > 0);
  assert.equal(galleryContract.width, galleryContract.canvasWidth);
  assert.equal(galleryContract.height, galleryContract.canvasHeight);
  assert.equal(
    galleryContract.width,
    Math.round(galleryContract.cssWidth * galleryContract.dpiScale));
  assert.equal(
    galleryContract.height,
    Math.round(galleryContract.cssHeight * galleryContract.dpiScale));
  assert.ok(galleryContract.frames >= 3);
  assert.equal(galleryContract.error, "");

  const framesBeforeControlChange = galleryContract.frames;
  await page.locator("#complexity").selectOption("250");
  await page.waitForFunction(
    previousFrames =>
      document.body.dataset.progpuNativeElements === "250" &&
      Number(document.body.dataset.progpuNativeFrames) > previousFrames,
    framesBeforeControlChange,
    { timeout: 30_000 });
  galleryContract.controlledElements = Number(
    await page.locator("body").getAttribute("data-progpu-native-elements"));
  galleryContract.controlledFrames = Number(
    await page.locator("body").getAttribute("data-progpu-native-frames"));
  assert.equal(galleryContract.controlledElements, 250);
  assert.ok(galleryContract.controlledFrames > framesBeforeControlChange,
    "The native gallery did not present after its scene changed.");
  assert.equal(
    await page.locator("body").getAttribute("data-progpu-native-error"),
    null,
    "The native gallery reported an error after its scene changed.");
  const galleryScreenshot = await page.locator(".stage").screenshot({
    path: path.join(
      evidenceDirectory,
      "progpu-native-browser-gallery.png")
  });
  assert.ok(galleryScreenshot.length > 1000,
    "The native gallery WebGPU screenshot is empty.");

  await page.getByRole("button", { name: "Aa Text shaping" }).click();
  try {
    await page.waitForFunction(
      () => document.body.dataset.progpuNativeSample === "text-shaping" &&
        Number(document.body.dataset.progpuNativeFontBytes) > 0 &&
        Number(document.body.dataset.progpuNativeWasmBytes) > 0 &&
        Number(document.body.dataset.progpuNativeGlyphs) > 0 &&
        Number(document.body.dataset.progpuNativeOutlines) > 0,
      undefined,
      { timeout: 120_000 });
  } catch (error) {
    const readiness = await page.evaluate(() => ({
      sample: document.body.dataset.progpuNativeSample ?? "unset",
      fontBytes: document.body.dataset.progpuNativeFontBytes ?? "unset",
      wasmBytes: document.body.dataset.progpuNativeWasmBytes ?? "unset",
      glyphs: document.body.dataset.progpuNativeGlyphs ?? "unset",
      outlines: document.body.dataset.progpuNativeOutlines ?? "unset",
      stage: document.body.dataset.progpuNativeStage ?? "unset",
      error: document.body.dataset.progpuNativeError ?? ""
    }));
    const diagnostics = errors.length === 0 ? "no browser errors" :
      errors.join(" | ");
    throw new Error(
      `Text showcase readiness timed out: ${JSON.stringify(readiness)}; ` +
        diagnostics,
      { cause: error });
  }
  const textContract = await page.evaluate(() => ({
    sample: document.body.dataset.progpuNativeSample,
    fontBytes: Number(document.body.dataset.progpuNativeFontBytes),
    wasmBytes: Number(document.body.dataset.progpuNativeWasmBytes),
    glyphs: Number(document.body.dataset.progpuNativeGlyphs),
    outlines: Number(document.body.dataset.progpuNativeOutlines),
    draws: Number(document.body.dataset.progpuNativeDraws),
    streamBytes: Number(document.body.dataset.progpuNativeStreamBytes),
    preset: Number(document.body.dataset.progpuNativeTextPreset),
    updateMilliseconds:
      Number(document.body.dataset.progpuNativeUpdateMilliseconds),
    width: Number(document.body.dataset.progpuNativeBackingWidth),
    height: Number(document.body.dataset.progpuNativeBackingHeight),
    dpiScale: Number(document.body.dataset.progpuNativeDpiScale),
    error: document.body.dataset.progpuNativeError ?? ""
  }));
  assert.equal(textContract.sample, "text-shaping");
  assert.ok(textContract.fontBytes > 300_000);
  assert.ok(textContract.wasmBytes > 1_000_000);
  assert.ok(textContract.glyphs > 100);
  assert.ok(textContract.outlines > 16);
  assert.ok(textContract.draws > 1);
  assert.ok(textContract.streamBytes > 0);
  assert.equal(textContract.preset, 0);
  assert.ok(Number.isFinite(textContract.updateMilliseconds) &&
    textContract.updateMilliseconds >= 0);
  assert.equal(textContract.width, galleryContract.width);
  assert.equal(textContract.height, galleryContract.height);
  assert.equal(textContract.dpiScale, galleryContract.dpiScale);
  assert.equal(textContract.error, "");

  await page.locator('[data-preset="1"]').click();
  await page.waitForFunction(
    () => document.body.dataset.progpuNativeTextPreset === "1",
    undefined,
    { timeout: 30_000 });
  textContract.changedPreset = Number(
    await page.locator("body").getAttribute(
      "data-progpu-native-text-preset"));
  assert.equal(textContract.changedPreset, 1);
  await page.locator("#benchmark-text").click();
  await page.waitForFunction(
    () => Number(document.body.dataset.progpuNativeBenchmarkSamples) === 32,
    undefined,
    { timeout: 30_000 });
  Object.assign(textContract, await page.evaluate(() => ({
    benchmarkSamples:
      Number(document.body.dataset.progpuNativeBenchmarkSamples),
    benchmarkP50: Number(document.body.dataset.progpuNativeBenchmarkP50),
    benchmarkP95: Number(document.body.dataset.progpuNativeBenchmarkP95),
    benchmarkMax: Number(document.body.dataset.progpuNativeBenchmarkMax)
  })));
  assert.equal(textContract.benchmarkSamples, 32);
  assert.ok(Number.isFinite(textContract.benchmarkP50) &&
    textContract.benchmarkP50 >= 0);
  assert.ok(textContract.benchmarkP95 >= textContract.benchmarkP50);
  assert.ok(textContract.benchmarkMax >= textContract.benchmarkP95);
  const textScreenshot = await page.locator(".stage").screenshot({
    path: path.join(
      evidenceDirectory,
      "progpu-native-browser-text-shaping.png")
  });
  assert.ok(textScreenshot.length > 1000,
    "The native text-shaping WebGPU screenshot is empty.");
  galleryContract.textShaping = textContract;
  await fs.writeFile(
    path.join(evidenceDirectory, "progpu-native-browser-gallery.json"),
    `${JSON.stringify(galleryContract, null, 2)}\n`);
  assert.deepEqual(errors, []);
  process.stdout.write(
    `ProGPU pure C++ browser gallery presented MotionMark as one GPU draw ` +
    `and ${textContract.glyphs} shaped glyph records with ` +
    `${textContract.outlines} retained outlines at ` +
    `${galleryContract.width}x${galleryContract.height} physical pixels.\n`);
} finally {
  await browser.close();
}
