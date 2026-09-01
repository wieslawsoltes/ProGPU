# CAD generation-safe incremental plan chunks

## Scope and clean-room boundary

The shared CAD sample now records semantic-root and eligible static-block-instance
plan chunks as immutable nested `GpuPicture` values and retains a least-recently-used,
bounded 8,192-entry cache across document generations. Canonical identity storage
is independently capped at 64 MiB total, conservatively charging each entry's
distinct retained TrueType font bytes, SHX glyph-path dependencies, and prepared
GPU texture residency, and 8 MiB
per encoded key. Reuse is
admitted only for continuous-style POINT, LINE, CIRCLE, ARC, ELLIPSE, SOLID,
3DFACE, SPLINE, LWPOLYLINE, 2D/3D POLYLINE, vector-outline TEXT/MTEXT,
SHX TEXT/MTEXT, SHAPE, MLINE fills/strokes, LEADER/MULTILEADER paths, TOLERANCE frames,
paper VIEWPORT frames, modeler display wires/payloads, solid or patterned HATCH,
WIPEOUT, and prepared raster IMAGE
roots whose complete canonical rendering inputs match byte-for-byte. Top-level
INSERT and MINSERT cells additionally share one definition-local fragment across
translation, rotation, reflection, and nonuniform affine scale when every expanded
child is an eligible analytic family, including flat SOLID/3DFACE,
vector-outline TrueType TEXT/MTEXT, retained analytic SHX TEXT/MTEXT/SHAPE,
HATCH boundary/pattern streams, continuous MLINE fills/strokes,
LEADER/MULTILEADER spline and arrow geometry,
TOLERANCE frame strokes, and WIPEOUT masks/frames.
Instance-owned ATTRIBs are
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
| HarfBuzz | [shape plans](https://harfbuzz.github.io/shaping-and-shape-plans.html), [buffer contract](https://harfbuzz.github.io/harfbuzz-hb-buffer.html), and [glyph rendering boundary](https://harfbuzz.github.io/glyphs-and-rendering.html) | Shaping remains immutable CPU state. Text chunks reuse only after exact font/glyph dependency identity is encoded; no reshaping or font-table work was added. |

## Identity, ownership, and complexity

Each eligible semantic-root key contains rebase origin, physical lineweight policy,
resolved layer visibility/plot/freeze/exclusion state, complete resolved color,
alpha, lineweight and continuous-linetype style, entity kind/visibility/bounds,
and exact primitive data. Variable SPLINE/polyline ranges are normalized away
from snapshot-global offsets and append their addressed values. Any unsupported
kind, PDMODE marker, unsupported linetype, or unencoded dependency fails
closed to ordinary recording.

The snapshot also retains non-overlapping top-level block-definition entity ranges
with exact double WCS affine placement and definition identity. Definition keys
normalize projected points and basis vectors through a double-precision inverse,
then compare the same retained float values consumed by plan recording. Cached
fragments compose through the existing nested-picture transform; fixed-device CAD
lineweights remain device-space while local wide-polyline geometry follows the
instance affine. Singular/projectively collapsed placements and extruded face/surface or
resource-bearing definitions fail closed.

TrueType identity includes exact glyph indices/positions, normalized entity basis,
run ranges and paint, rectangles/strokes/decorations, face index, ordered variation
settings, and a cached SHA-256 font-data discriminator. A cache hit additionally
performs reference-fast, byte-exact font-data and variation comparison, so the hash
is never the correctness boundary. Bitmap/color fonts fail closed until palette and
bitmap-resource identity is part of the contract.

SHX identity includes the normalized entity basis, exact glyph placements,
MTEXT run paint/transforms and rectangle/stroke/decorations, shape number,
orientation, advance, bounds, and segment count. Cache hits additionally require
the same immutable `CadShxGlyph` objects, so equal public metadata cannot alias a
different analytic path. Distinct dependencies are conservatively charged by
segment count. Double-inverse cancellation residue within 64 binary64 ulps of
unit-scale zero is canonicalized before the retained-float key boundary; all
other normalized components remain byte-exact.

HATCH identity normalizes its OCS origin and axes and encodes every contributing
or ignored loop, analytic segment, pattern family, and dash value independently
of snapshot-global offsets. Replayed chunks restore the exact complex-pattern
auxiliary-record charge and are admitted only when the remaining document budget
can reproduce the original result.

WIPEOUT identity normalizes the image-plane origin and independent pixel axes and
encodes its dimensions, active clip grammar, mask/frame/alignment flags, inversion,
and exact mask color. Clip points remain in their authored image-plane coordinate
system. This makes the retained mask and frame safe to share across translation,
rotation, reflection, and nonuniform affine block instances without allocating a
raster mask or introducing a device resource.

LEADER and MULTILEADER identity encodes each canonical spline without retaining
snapshot-global offsets, its exact knot/weight streams, default-arrow triangle,
fit/dogleg state, and branch ordinals. TOLERANCE identity likewise encodes every
frame stroke and its row/cell topology. Their projected geometry is safe for the
same affine definition normalization as other analytic paths; any accompanying
text or custom-block entity remains an independently encoded member of the same
semantic root. Paper VIEWPORT identity is root-local only and includes the exact
camera, clipping/status, frozen-layer-name, and frame inputs; viewport entities
inside a block fail closed because their paper-space dimensions are not an
ordinary model-geometry affine contract.

MLINE identity encodes every fill triangle, authored cut interval, path domain,
element style, and element-local linetype definition without retaining snapshot-
global offsets. Continuous elements participate in definition-affine sharing.
Patterned elements reuse exact semantic roots only, because their dash lengths are
already measured in final entity space; their figure, placement, pattern-step, and
source-segment charges and substitution diagnostics replay through the same bounded
global contract as ordinary complex linetypes. Any unresolved or budget-truncated
element prevents the whole root from being interned.

BODY, REGION, and 3DSOLID modeler identity is semantic-root-only and contains the
byte-exact ACIS payload, format/kind metadata, display-wire topology, wire metadata,
and all 3D points without snapshot-global offsets. Hits replay deferred-surface and
wireframe statistics and the same per-entity informational diagnostic. Empty-wire
payload roots are valid cached empty pictures; they still retain their observable
statistics and diagnostic. Definition-affine sharing fails closed because the
retained `DrawAcisSolid` command carries full 3D lines while the plan chunk outer
transform is intentionally a 2D affine composition contract.

Semantic-root simple and complex linetypes encode the complete A-aligned pattern,
element transforms, shaped TrueType or SHX text, SHX shape paths, substitution
state, and the per-entity arc-map option. Hits restore figure, placement, pattern-
step, and source-segment counters and repeat one-per-pattern substitution
diagnostics. A failed or budget-truncated lowering is never interned. Affine block
sharing remains disabled for non-continuous linetypes because CAD dash lengths are
resolved in final entity space and must not be rescaled by a shared outer transform.

Prepared raster IMAGE identity includes normalized origin/pixel axes, dimensions,
clip grammar, frame/display/sampling/effect state, IMAGEDEF metadata, exact texture
object, format, alpha mode, dimensions, layers, and mip count. `DrawingContext`
shares an already-retained texture lease into the chunk recorder with an independent
reference, so cache eviction, scene disposal, or prepared-resource disposal cannot
invalidate another published picture. Texture residency is conservatively charged
as 16 bytes per texel per layer, sample, and mip level; this intentionally
overestimates common RGBA8 storage. Resolver-on-record images fail closed because
their exact texture identity is not known at key construction time.

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
picture. Currently eligible analytic/vector-text chunks retain no disposable device
resource; cached font and SHX glyph objects remain strongly owned by their retained
commands and exact dependency table. Color/bitmap text and mesh roots remain on
the ordinary path until their complete resource and global
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

Definition-local extruded surface, color/bitmap text, affine-block linetype,
mesh and other
resource- or global-budget-dependent families remain the next chunk-coverage work.
