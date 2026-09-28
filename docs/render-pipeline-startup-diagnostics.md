# Pipeline creation diagnostics

`PROGPU_BACKEND_DIAGNOSTICS=1` (or `true`, case-insensitive) now records the
beginning and end of actual render/compute pipeline creation. Entries include
the cache key and shader entrypoints; the end records API wall time and whether
the API returned a non-null handle. Those are not shader-validation success,
GPU execution/completion, or application startup acceptance claims.

Both local cache hits and device-domain acquisitions return before logging.
Disabled diagnostics format no message and read no clock. Each creation captures
the opt-in once, retaining a matching end even if the environment changes during
the call. Exception unwinding also records an end without claiming API return.
Compiler/adapter defaults, shader code, pipeline descriptors, cache identity,
submission/wait behavior and all application deadlines are unchanged.

This applies to the managed `RenderPipelineCache`, shared by its backend APIs.
The C++ engine owns separate native pipeline caches and does not invoke this
class: these measurements do not qualify or measure native pipeline creation.
`PipelineCreationDiagnosticsTests` exercises real render/compute creation, both
cache-hit layers, disabled settings and both accepted opt-in spellings.

## Rejected brush-argument experiment

The implementation baseline is original ProGPU commit
`58eee08f75e5b85c465fada2cfe4a33b10eedb6d`. A local experiment narrowed ten nested
`Vector.wgsl` brush helper arguments to only their consumed fields. It preserved
every raw result across 4,480 GPU samples on Metal and Windows ARM64 D3D12, but
did **not** establish a compilation improvement and is not included here.

Separate-process Windows ARM64 observations using the normal Parallels D3D12
adapter (API acquisition milliseconds, original/revised/revised/original):

| Fragment entry | Original first | Revised first | Revised second | Original second |
| --- | ---: | ---: | ---: | ---: |
| `fs_main` | 2989.353 | 2645.274 | 2508.789 | 2342.294 |
| `fs_main_unmasked` | 3269.711 | 2527.103 | 2451.956 | 2447.717 |

These are neither cold driver-cache measurements nor a statistical application
benchmark. The original packaged Forms grid's 20-second startup failure remains
authoritative. Its native stack proves FXC fragment compilation was active,
but not which entrypoint or the cost of the full startup sequence. Per-pipeline
diagnostics resolve that missing attribution before another rendering change.

## Research boundaries retained

The comparison in [single-path raster pipelines](single-path-raster-pipeline.md)
remains applicable. Primary references were revisited: [SkParagraph's separate
layout/paint API](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h),
[Direct2D resource reuse and batching](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance),
[DirectWrite reusable layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
[Win2D deferred resource/device-loss ownership](https://microsoft.github.io/Win2D/WinUI3/html/LoadingResourcesOutsideCreateResources.htm),
[WebRender retained scenes, culling and GPU preparation](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Vello's GPU rendering approaches](https://github.com/linebender/vello),
[Parley's text responsibilities](https://github.com/linebender/parley), and
[HarfBuzz shaping responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html).
No implementation text was copied. Preserve lazy device-owned pipelines and
shaping/layout versus raster separation; do not change retained-scene reuse,
visibility, glyph/path/texture keys or eviction, demand-driven upload, worker
preparation, batching, DPI/subpixel/hinting, fallback/variable-font state or
device/atlas invalidation to work around an unattributed startup delay.

Local focused Release validation: 71 pipeline/cache/diagnostic source tests
passed with zero skips. Full application/package/platform CI remains separate;
diagnostic-only observations cannot release a package or close the grid issue.
