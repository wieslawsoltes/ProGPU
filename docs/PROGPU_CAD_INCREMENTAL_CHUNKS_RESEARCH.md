# CAD generation-safe incremental plan chunks

## Scope and clean-room boundary

The shared CAD sample now records semantic-root and eligible static-block-instance
plan chunks as immutable nested `GpuPicture` values and retains a least-recently-used,
bounded 8,192-entry cache across document generations. Canonical identity storage is independently capped at 64 MiB total
and 8 MiB per root. Reuse is admitted only for continuous-style POINT, LINE, CIRCLE,
ARC, ELLIPSE, SOLID, 3DFACE, SPLINE, LWPOLYLINE, 2D POLYLINE, and 3D POLYLINE
roots whose complete canonical rendering inputs match byte-for-byte. Top-level
INSERT and MINSERT cells additionally share one definition-local fragment across
translation, rotation, reflection, and nonuniform affine scale when every expanded
child is one of the eligible non-face analytic families. Instance-owned ATTRIBs are
excluded from the definition range. Unsupported
families take the ordinary full-recording path and are never guessed reusable.

The implementation is original ProGPU code. No third-party source text,
control flow, helper layout, or cache encoding was copied.

## Primary-source comparison

| Engine | Primary sources | Adopted, adapted, or rejected |
|---|---|---|
| Skia / SkParagraph | [Skia picture API](https://skia.org/docs/user/api/), [Skia shaped-text design](https://docs.skia.org/docs/dev/design/text_shaper/), and [Skia tracing](https://docs.skia.org/docs/dev/tools/tracing/) | Adopted immutable nested pictures and reusable text/layout separation. Adapted identity to exact CAD semantic-root inputs. Rejected mutable command data and treating object handles as content versions. |
| Direct2D / DirectWrite / Win2D | [Direct2D command lists](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist), [Direct2D/DirectWrite integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite), and [Win2D `CanvasCommandList`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_CanvasCommandList.htm) | Adopted immutable command-list composition and device-independent analytic input. Rejected COM/platform objects in CAD snapshots and per-entity native crossings. |
| WebRender | [rendering overview](https://searchfox.org/mozilla-central/source/gfx/docs/RenderingOverview.rst), [retained display-list overview](https://github.com/servo/servo/wiki/Webrender-Overview), and [current profiler source](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs) | Adopted transaction-generation separation, retained subtrees, and explicit hit/miss counters. Adapted transactions to one complete CAD picture publication. Rejected partial-frame publication and handle-only reuse. |
| Vello / Parley | [Vello retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md), [Vello scene source](https://github.com/linebender/vello/blob/main/vello/src/scene.rs), and [Parley layout model](https://github.com/linebender/parley/blob/main/doc/concept.md) | Adopted retained fragments and reusable layout ownership. Adapted fragments to canonical analytic CAD inputs and existing ProGPU pictures. Rejected viewport-baked fragments and a second path/text renderer. |
| HarfBuzz | [shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html), [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html), and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | Shaping remains immutable CPU state. Text chunks stay conservatively uncached until font/fallback/glyph-resource identity is encoded completely; no reshaping or font-table work was added. |

## Identity, ownership, and complexity

Each eligible semantic-root key contains rebase origin, physical lineweight policy,
resolved layer visibility/plot/freeze/exclusion state, complete resolved color,
alpha, lineweight and continuous-linetype style, entity kind/visibility/bounds,
and exact primitive data. Variable SPLINE/polyline ranges are normalized away
from snapshot-global offsets and append their addressed values. Any unsupported
kind, PDMODE marker, non-continuous linetype, or unencoded dependency fails
closed to ordinary recording.

The snapshot also retains non-overlapping top-level block-definition entity ranges
with exact double WCS affine placement and definition identity. Definition keys
normalize projected points and basis vectors through a double-precision inverse,
then compare the same retained float values consumed by plan recording. Cached
fragments compose through the existing nested-picture transform; fixed-device CAD
lineweights remain device-space while local wide-polyline geometry follows the
instance affine. Singular/projectively collapsed placements and face/surface or
resource-bearing definitions fail closed.

Key construction is O(P) time and storage for P primitive/range values in the
root, polls cancellation at a fixed entity cadence, and fails closed at the
per-root byte limit. Lookup is O(P) for exact byte comparison. A hit skips plan command
generation for that root, appends its immutable child picture in O(1), restores
its recorded-entity/command statistics, and preserves child identity so the
existing compositor retained-picture pages can reuse compiled geometry and
incremental uploads. Cache storage is bounded by entry count and canonical-key
budgets; eligible picture payload size is correspondingly bounded by the encoded
analytic inputs and the snapshot expansion limits. Stable replay remains allocation-free; cache work occurs
only during changed-generation scene preparation.

The aggregate picture independently retains every child resource lease. LRU
eviction, replacement, or cache clearing therefore cannot invalidate an already published
picture. Currently eligible analytic chunks retain no device resource. Raster,
text, hatch, complex-linetype, viewport, modeler, leader, tolerance, MLINE, and
mesh roots remain on the ordinary path until their complete resource and global
budget dependencies are encoded.

## Managed/native applicability and validation

Managed composition traverses the same nested `GpuPicture`; the native picture
compiler flattens that identical retained hierarchy into the existing packed
scene. No shader, C ABI, C++ frontend, primitive quality, camera, or upload
contract changes. Focused tests prove exact unchanged reuse, one-root
invalidation with an unrelated root retained, cache bounds/ownership, and
successful native lowering with the same flattened source-command and draw-batch
semantics. MINSERT and distinct affine INSERT regressions prove one shared child
identity, exact composed managed endpoints within retained-float tolerance, and
matched native primitive/draw counts.

Definition-local face/surface, text/SHX, hatch, complex-linetype, raster, and other
resource- or global-budget-dependent families remain the next chunk-coverage work.
