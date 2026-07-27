# Avalonia clean-room remediation

## Scope

The Avalonia renderer, its Avalonia 11 wrapper, the Silk.NET integration tests,
the ProGPU renderer tests, and the Skia comparison host contain files introduced
by commits that explicitly describe ported or adapted source. This conflicts
with ProGPU's clean-room implementation rule even when the imported material is
permissively licensed.

The remediation gate is:

```bash
tools/verify-avalonia-clean-room.sh --enforce
```

During remediation, `--report` prints the remaining tree and history inventory
without failing.

## Permitted design evidence

Replacement code may be derived only from:

- Avalonia's published interface and assembly contracts for the pinned version.
- Public WebGPU, Unicode, OpenType, and platform specifications.
- Primary architecture documents for Skia/SkParagraph, DirectWrite/Direct2D,
  Win2D, WebRender, Vello/Parley, HarfBuzz, CoreText, and Chromium.
- Independently observed output, lifecycle, allocation, and performance
  behavior.
- New conformance and property tests written from those contracts and
  observations.

The imported implementation and imported tests are deletion inventory, not
design references. Replacement files must use ProGPU-owned naming, structure,
typed ownership, and algorithms.

## Replacement order

1. Remove unused placeholders and compatibility remnants.
2. Replace geometry and region adaptation with one ProGPU-owned path contract.
3. Replace bitmap, image, and render-target ownership around `GpuTexture`.
4. Replace drawing recording with typed retained commands and bounded caches.
5. Replace font management, glyph-run transport, and shaping adapters with
   `ProGPU.Text.Shaping`.
6. Rewrite tests as independent contract, property, differential, lifetime,
   pixel, and performance tests.
7. Remove porting notices, imported assets, and legacy project layout.
8. Create a history-clean integration branch before committing the final
   replacement.

Every completed cluster must build against the exact pinned Avalonia assemblies,
pass the no-reflection and package ABI gates, and preserve the matched
ControlCatalog pixel/performance evidence.

## Current progress

The initial audit found 80 imported paths in the scoped tree. The current
remediation pass reduced that final-tree inventory to zero by:

- deleting unused cache, paint, text-builder, image, path, and pen placeholders;
- deleting the unused two-level cache and its self-only imported tests;
- replacing the dirty-region implementation with a bounded ProGPU-owned region;
- replacing primitive, grouped, boolean, and transformed geometry subclasses
  with one ProGPU-native geometry factory; and
- replacing the imported base/stream geometry implementation with a typed,
  CPU-only path adapter and stream writer;
- adding independent region, geometry, measurement, hit-test, and stream-writer
  contract tests;
- replacing the imported color-glyph bitmap cache with a bounded,
  metadata-only cache that owns no decoded pixels;
- replacing imported immutable, writable, render-target, drawing-layer, and
  framebuffer bitmap owners with typed one-texture ProGPU implementations;
- making render-target CPU storage and readback genuinely lazy, so ordinary
  GPU rendering does not retain a duplicate native framebuffer;
- replacing image-brush resolution, tile mapping, and offscreen resource
  ownership with bounded ProGPU-owned contracts; and
- adding an allocation-free ICO/common-image header reader and independent
  image-container tests;
- replacing the imported telemetry file and Skia comparison host with
  ProGPU-owned contracts at new paths; and
- removing all six provenance notices plus an unused imported noise asset;
  and
- moving the independently authored renderer/shaper contracts into a new
  signed test project, consuming Inter from the pinned Avalonia dependency
  instead of retaining seven imported font binaries; and
- replacing the imported render-test project with a new Avalonia 12 headless
  pixel contract. It captures two deterministic 640x360 Skia reference frames
  around a targeted text invalidation and verifies that both PNGs are valid and
  distinct. Native ProGPU-vs-Skia pixels remain qualified by the separate
  source-built ControlCatalog matrix;
- replacing both imported package project files and the bootstrap/options/API
  lease surface with newly authored ProGPU package definitions and typed
  service registration while preserving the `Avalonia.ProGpu` assembly
  contract for Avalonia 11 and 12; and
- replacing the imported typeface, glyph-run, and font-manager files with a
  bounded adapter over `ProGPU.Text.FontManager`. Typeface wrappers are capped
  at 256 entries, font streams and Avalonia 12 table reads are zero-copy, and a
  retained glyph run now owns only glyph IDs plus one packed `Vector2` position
  array. Rare decoration intersections recompute conservative font bounds
  instead of retaining a second per-glyph bounds array; and
- replacing the imported platform renderer and backend-context files with
  independently authored typed factories and surface selection. Backend GPU
  initialization is lazy, and nested/offscreen drawing layers receive the
  selected `WgpuContext` explicitly instead of consulting thread-global state
  and potentially initializing a second native device; and
- replacing the imported drawing-effect partial with a lazy effect-scope
  recorder. Ordinary contexts allocate no effect stack, blur and shadow scopes
  retain typed ProGPU visual subtrees, non-finite inputs are bounded, and reset
  transactionally discards unbalanced scopes.

The replacement region retains at most 64 independent rectangles before
collapsing to a conservative union, so dirty-rectangle storage remains bounded.
Boolean paths are represented lazily and do not initialize WebGPU during
Avalonia layout. Contour measurements and render-command geometry identities are
created lazily and retained by the owning geometry. The exact-source
retained/flattened pixel matrix passes for all nine ControlCatalog pages after
these changes.

The clean-room contract suite passes 57 tests, and the new headless pixel suite
passes its independent capture/invalidation test. The rebuilt
source ControlCatalog Composition page completed three fresh 300-frame Release
runs with a median 120.57 FPS, about 4.80 KiB managed allocation per frame, one
739-node retained scene, zero fallback nodes, 29,605,888 explicit Metal bytes,
and 92,928 tracked intermediate-texture bytes. Matched three-run Xcode
Allocations evidence reports a 301,021,088-byte median persistent native heap
plus anonymous VM, 1,622,656 bytes below the preceding matched baseline.
Because the remaining IOAccelerator working set is startup-created and the
delta is small, it is recorded as a bounded reduction rather than a leak fix.
The complete retained-versus-flattened pixel matrix also passes after the
effect rewrite: nine zero-fallback pages plus blur, drop-shadow, opacity-mask,
geometry-clip, text-option, and BitmapCache fixtures.
The clean integration branch has been reconstructed from a parent before the
three prohibited imported commits. The current enforced audit reports zero
imported paths, zero provenance notices, and zero reachable import commits.

The final preview-27 qualification also passes after the clean-room rewrite:
2,500/2,500 ProGPU tests, the exact Avalonia ABI check, the complete
retained/flattened pixel contract, package reflection inspection,
single-window and shared-device package-only runtime smokes, and a trimmed
NativeAOT package-consumer smoke. The final four-lane ControlCatalog Buttons
matrix completed 12/12 fresh processes with zero harness failures. The
clean-room tree and history gates therefore both pass.
