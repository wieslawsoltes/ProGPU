# Native browser npm package

`@wieslawsoltes/progpu` is the proposed scoped identity for the ES-module consumer
of the existing retained C++ WebGPU renderer,
not a packaged gallery executable. The first version is `0.1.0-preview.1` under
the `next` tag. Source files under `src/ProGPU.Native/browser/npm` are not a
publishable package until the pinned native build and staging checks finish.

The scoped-name proposal awaits the maintainer's naming choice. The initial
unpublished `progpu` name was rejected by the registry as too similar to `prompt`;
preparing this change does not authorize merging or publication. Version
`0.1.0-preview.1`, public access and the `next` tag are unchanged.

Consumers import `@wieslawsoltes/progpu`; its Wasm asset subpath is
`@wieslawsoltes/progpu/progpu-native.wasm`. The installed-package browser and
TypeScript gates use that exact scoped identity, without an unscoped alias.

## Architecture and ownership

`createRenderer({ canvas, device? })` creates one isolated Emscripten module,
native engine and canvas surface. Importing the JavaScript performs no DOM/GPU
initialization, installs no animation loop and creates no global `Module`.
Applications retain the renderer and own frame scheduling and CSS/resize policy.
The optional device is borrowed: native C handle references are balanced without
calling `GPUDevice.destroy()`. A factory-created device is destroyed on disposal.
The detachable loss callback cannot retain a disposed renderer through a borrowed
device's unresolved promise. Device loss is explicit, with no automatic fallback.

Mutable `Path`/`SceneBuilder` authoring copies caller input into owned records;
`build()` produces an immutable generation. One changed scene uses one batched
native update, and a frame uses one native render entrypoint. Stable immutable
objects avoid another update. Raw native streams are always validated by content,
not cached by the identity of mutable JavaScript arrays. Scene ID/generation and
complete accepted bytes remain unchanged after a rejected replacement.

The compact typed authoring transport has checked little-endian fields, bounded
lengths and atomic publication. Its decoder calls the original
`semantic_scene_builder`: it does not duplicate scene compilation, GPU culling,
connected stroke widening, cap/join/dash walking, rasterization or compositing.
The CMake target shares `PROGPU_NATIVE_RENDERER_SOURCES` and the existing
`browser_window_host`. Native engine/device limits and source ownership remain
authoritative. No per-command Wasm crossing or JSON parsing is added to rendering.

Typed authoring covers solid/linear-gradient fills, line/quadratic/cubic paths,
nonzero/even-odd fill rules, connected/dashed polylines, transforms, rectangular
clips, opacity state and isolated opacity layers. Complete native semantic streams
can also be supplied explicitly. There is no typed text-layout, font-loading,
SVG-parser or custom-shader API in this preview; raw ABI exposure does not qualify
every native resource type as a JavaScript integration. Existing text shaping,
font fallback, variable fonts, glyph caches and atlas-generation policies are
unchanged rather than replaced with browser text approximations.

## Qualification and release

The existing browser job keeps its full native smoke test and 30-minute deadline.
Additional checks cover:

- Original native-builder versus authoring-transport complete byte equality,
  every truncated input length, invalid flags/kinds/scopes, and unaligned input.
- JavaScript immutable ownership, validation and failed-factory lifetime contracts.
- A fresh consumer installed from the actual `.tgz`, with strict TypeScript
  declarations and actual Chromium WebGPU execution.
- Complete RGBA equality for cold/warm frames, independent module instances,
  raw-stream replay and the surviving borrowed-device renderer after peer disposal.
- Independent pixel checks for curves, even-odd holes, gradients, dash gaps,
  transforms, clips and once-composited opacity; actual browser presentation must
  match every canvas pixel, not merely produce a nonempty screenshot.
- Actual native raw-snapshot reuse, same-generation changed-byte rejection,
  visible generation replacement, logical/physical DPI resize, and separately
  owned versus borrowed device destruction.

The browser evidence retains native counters, archive/source identity, full images
and factory/cold/warm timings including explicit queue completion. These are
bounded correctness measurements on explicit Chromium SwiftShader, not a
hardware performance, full engine integration, memory-residency or scroll-latency
claim. `render()` returns after submission, not after GPU/display completion.

The Linux software test also selects Chromium's Vulkan compositor with
`--enable-features=Vulkan --use-vulkan=swiftshader` and disables GL fallback.
WebGPU adapter selection alone does not initialize that compositor. With the
previous ANGLE-only test flags, Chromium 151 rejected the first canvas shared
image and canceled queue completion. A standalone WebGPU clear without any
ProGPU/Wasm reproduced the failure, including both supported canvas formats;
changing device preference, optional features or animation-frame timing did not
repair it. The explicit compositor configuration passed every clear pixel and
the complete installed-package gate on Linux ARM64, using the same archive
previously checked on macOS. Browser version, launch flags and actual SwiftShader
adapter identity are retained, with phase/console/device-loss evidence on failure.
This changes only the test host, not application adapter defaults, renderer
selection, assertions or deadlines; exact-head Linux x64 CI remains required.
Chromium documents the compositor/initialization distinction in its
[GPU modes](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/content/browser/gpu/fallback.md)
and [Vulkan switches](https://chromium.googlesource.com/chromium/src/+/caa03c9c6b945b2f364f962cb8f75abe835f4b01/gpu/command_buffer/service/gpu_switches.cc).

`eng/progpu-pack-npm.mjs` stages fresh files from the pinned Emscripten 4.0.18
build. It requires the complete JS/Wasm/typing payload and original ProGPU,
Emscripten/runtime and Emdawnwebgpu notices, and rejects local port overrides.
It records the source commit, dirty state, Build run/attempt, toolchain identity,
license hashes, archive SHA256 and npm SHA512 integrity. Dirty local diagnostic
archives can be tested but cannot be released.

Staging records the filename returned by `npm pack --json`. An offline test
executes npm itself and verifies its scoped archive name
`wieslawsoltes-progpu-0.1.0-preview.1.tgz` through the unchanged ZIP/hash/content
pipeline. The release verifier rejects the old unscoped identity, foreign scopes,
scope-stripped/mismatched filenames and inner/outer identity mismatches. Archives
from the earlier unscoped Build cannot be renamed or republished as the scoped
package; a fresh successful complete Build must produce the new exact bytes.

The separate `npm release` workflow is manual, canonical-main-only and defaults
to verification without publication. Its `build_run_id` must identify an entire
completed successful **Build** with a unique unexpired `progpu-npm-package`
artifact. The exact source must be an ancestor of main; canceled/incomplete
producers, unmerged PR heads and stale run attempts fail closed. The verifier
checks the GitHub ZIP digest, bounded regular-file inventory, package hashes,
embedded metadata and source-authored asset bytes. It never runs package code or
extracts arbitrary tar members.

With `publish=true`, a separate job downloads and verifies the same artifact ID,
attempt and digest again, then runs `npm publish` on the unchanged `.tgz` with
scripts disabled and the `next` tag. `NPM_TOKEN` is exposed only to that final
step. The workflow does not repack, change versions, overwrite existing releases,
or distribute native system DLLs. The npm account must own the package name and
the supplied token must authorize noninteractive publication; secret presence
alone does not prove either. A registry rejection remains a release failure.

Current-workflow npm provenance is deliberately not requested: it would describe
the release dispatch rather than the historical Build which produced these exact
bytes. The explicit verification record binds that historical producer without
claiming an npm provenance attestation.

## Primary research and clean-room provenance

Only original ProGPU implementation is reused. Original third-party runtime
notices accompany the normal pinned Emscripten/Emdawn dependency; foreign engine
implementation code is not copied into the adapter.

| Primary contract examined | Adopted or preserved decision |
| --- | --- |
| [Emscripten modularized output](https://emscripten.org/docs/compiling/Modularized-Output.html) | Async isolated ES-module factories; no global-module or experimental singleton mode. |
| [Skia CanvasKit](https://skia.org/docs/user/modules/canvaskit/) and [SkParagraph public contract](https://skia.googlesource.com/skia/+/refs/heads/main/modules/skparagraph/include/Paragraph.h) | Separate initialization/retained drawing from reusable layout and interaction. No new text wrapper is inferred from vector rendering. |
| [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains) and [Win2D device loss](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost) | Explicit device ownership and invalidation; no cross-device handle reuse or hidden recovery. |
| [WebRender rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) and [Vello Scene](https://docs.rs/vello/latest/vello/struct.Scene.html) | Retained scene generation and GPU submission remain distinct; native visibility, batching and demand-driven uploads are retained. |
| [Parley Layout](https://docs.rs/parley/latest/parley/struct.Layout.html), [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout) and [HarfBuzz clusters](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html) | Shaping, font/cluster identity and layout reuse stay in the existing text services. No character-count layout, glyph-feature substitution or per-frame reshaping is introduced. |
| [npm CI tokens](https://docs.npmjs.com/using-private-packages-in-a-ci-cd-workflow) and [provenance](https://docs.npmjs.com/generating-provenance-statements/) | Restrict token exposure, preserve exact tested bytes and do not misattribute historical-build provenance. |
| [npm scopes](https://docs.npmjs.com/cli/v11/using-npm/scope/) and [npm pack](https://docs.npmjs.com/cli/v11/commands/npm-pack/) | Preserve the scoped import/install identity and record actual pack output; an offline npm fixture independently verifies the permitted tarball basename. |

Startup remains lazy until the explicit factory; renderer caches remain native
and device-owned. DPI is separately supplied from logical coordinates. Existing
glyph/path cache keys, eviction, subpixel/hinting, fallback/variable-font state
and worker preparation are not retuned by this packaging change. It makes no
claim that another application's bundler, worker model or asset deployment has
been integrated until that application's emitted package is exercised directly.
