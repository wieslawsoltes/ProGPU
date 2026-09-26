# ProGPU for JavaScript

An ES-module library using ProGPU's retained C++ vector renderer, compiled to
WebAssembly and running on WebGPU. No .NET installation or global Module object
is required. A browser with WebGPU and a secure origin (HTTPS or localhost) is
required; there is no Canvas2D, WebGL, or software-renderer fallback.

```js
import { createRenderer, SceneBuilder, Path } from 'progpu';

const canvas = document.querySelector('canvas');
const renderer = await createRenderer({ canvas });
renderer.resize({ width: 640, height: 360, pixelRatio: devicePixelRatio });

const curve = new Path()
  .moveTo(80, 160)
  .cubicTo(80, 20, 280, 20, 280, 160)
  .lineTo(80, 160)
  .close();
const scene = new SceneBuilder({ sceneId: 1n, generation: 1n })
  .fillRect(20, 20, 40, 40, [1, 0.2, 0.1, 1])
  .fillPath(curve, [0.1, 0.5, 1, 1])
  .build();

renderer.updateScene(scene); // Only when the immutable scene changes.
renderer.render({ clearColor: [0.06, 0.06, 0.08, 1] });
// Keep the renderer for subsequent frames. It does not install an animation loop.
// renderer.dispose(); // When the view is permanently removed.
```

Use your application's requestAnimationFrame scheduling for animated views.
Call `resize` when the logical size or device-pixel ratio changes. Geometry
coordinates are logical pixels; colors are RGBA values between zero and one.
Paths preserve line, quadratic and cubic segments through the native renderer.

## Packaging and assets

The archive includes its JavaScript modules, TypeScript declarations, generated
`progpu-native.mjs`, and `progpu-native.wasm`. Serve the Wasm file alongside the
generated module. Bundlers must copy/preserve that asset URL; check the emitted
network request rather than treating a successful JavaScript build as proof that
Wasm was deployed. `progpu/progpu-native.wasm` is an exported asset subpath.
For unbundled use, map the bare `progpu` specifier to the installed `index.js`
with an import map. Node.js can inspect/pack the library, but is not a supported
WebGPU rendering host for this browser build.

This source tree is packaged by `eng/progpu-pack-npm.mjs`; it is not itself a
complete publishable archive until the native target has been built and staged.
PR builds never receive a publishing token. The separate manual release workflow
uses the repository's `NPM_TOKEN` only for the final publish step after verifying
the successful Build, merged source commit and exact archive digest. Prereleases
use the `next` distribution tag. There is no install or publish lifecycle script.

## TypeScript

The installed-package declaration gate uses TypeScript 6.0.3 with `strict`,
`noEmit`, `target: "ES2022"`, `module: "NodeNext"` and `lib: ["ES2022", "DOM"]`.
The browser DOM declarations supply the real `GPUDevice` type; the package does
not substitute a reduced device interface or add a runtime typing dependency.
The same installed consumer was also checked with TypeScript 7.0.2.

TypeScript 5 users need the supplemental `@webgpu/types` package and must opt it
into their compiler's `types` list (verified with TypeScript 5.9.3 and
`@webgpu/types` 0.1.74). Do not unconditionally include that supplement with newer
DOM libraries: duplicate WebGPU declarations fail strict compilation. Follow the
[GPUWeb type package's compatibility guidance](https://github.com/gpuweb/types)
when selecting a compiler and DOM declaration version.

## Ownership and supported authoring

`Path` and `SceneBuilder` are mutable authoring objects. Recording a path/brush
copies its input; `build()` returns an immutable scene snapshot. Use a new
generation for changed content with an existing scene ID. Stable frames reuse
the accepted native scene instead of crossing into Wasm per drawing command.

The typed authoring surface includes solid/linear-gradient fills, nonzero and
even-odd curved paths, connected polyline strokes, transforms, rectangular clips,
opacity state and opacity layers. Text layout, SVG parsing and custom shaders
are not typed JavaScript authoring APIs in this preview. They are not silently
approximated.

Advanced integrations may supply an existing complete native semantic stream as
a Uint8Array to `updateScene`. It goes through the same native validation and
compiler as other ProGPU hosts. `getSceneStream()` returns an owned copy of the
currently accepted stream; modifying that copy cannot mutate the renderer.
The exported native module is a low-level, versioned ABI, not a promise that raw
native pointers or wire layouts are stable across package versions.

Each factory creates an isolated Wasm module and renderer. Pass `{ canvas, device }`
to borrow an existing WebGPU device; `dispose()` never destroys a supplied device.
The device must remain live while the renderer is used. Device errors remain
explicit; disposal is not automatic recovery or permission to reconfigure a
borrowed device. Runtime-owned handles are released through the native engine.

See `build-info.json`, LICENSE and THIRD-PARTY-NOTICES.md for exact source,
toolchain and runtime notices. CI's installed-package browser checks are bounded
rendering/ownership checks, not a claim of complete third-party engine integration.
