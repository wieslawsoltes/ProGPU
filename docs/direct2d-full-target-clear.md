# Direct2D full-target Clear

An unscoped `ID2D1RenderTarget::Clear` replaces all earlier commands and retained
resources in the current recording. Compatible targets also discard their retained
history. The portable recorder and Windows command-list translator use the shared
semantic scene reset, retire recorder-owned resource indices, and retain transform,
antialias, text, brush and tag state for subsequent drawing. Previously exported
scene bytes keep their independent ownership.

The last full clear becomes the frame's clear color; only the surviving commands
and resources are serialized. Windows `TranslatedDrawCount` still counts all
successfully translated draw callbacks, including discarded draws. Original
callback indices and the first failure remain authoritative.

Microsoft specifies [straight-alpha clear colors and transparent black for a null
color](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-clear%28constd2d1_color_f%29).
Public clear metadata retains that representation. IGNORE targets set alpha to one;
ordinary scene submission premultiplies RGB once before the engine attachment
clear. Compatible picture submission retains its existing conversion. Recordings
without Clear preserve the target attachment.

Clear inside an all-aliased axis-aligned clip stack appends a bounded replacement
through the shared semantic builder's ordinary SRC layer. The already-intersected
clip belongs to the target frame captured at each push. A later source transform,
including a singular finite transform, cannot move that clear. Pixels outside the
clip, preceding commands/resources, leading-clear metadata and source drawing
state survive. A null color replaces the clipped pixels with transparent black;
IGNORE targets instead retain alpha one. An empty intersection appends no draw.

This is retained drawing, not a history reset: the portable retained draw count
includes its primitive and preserves the existing mixed-DPI-history rejection.
Windows translated draw counts still describe original draw callbacks, not the
new internal clear primitive. The layer record layout is unchanged; the explicit
`ALIASED_COMPOSITE_BOUNDS` flag adds final binary replacement coverage. No readback, CPU compositor
or transfer of resource ownership. Both native providers use their existing shared
layer execution. Ordinary uniform-DPI picture capture uses that same execution;
the final bound uses that capture's actual per-axis presentation when mapped.
Other mapped layer/cache/effect/mask restrictions remain unchanged.

Antialiased axis clips (including aliased children beneath them) use the
[retained target-storage Clear contract](native-target-storage-clear.md):
background-preserving clip groups, exact binary SAVE clips and replacement of
the actual current attachment before AA coverage resolves once at pop. Source
layers use the independently documented
[captured extent and demand-isolation contract](direct2d-layer-initialization.md),
including legacy and OPTIONS1_NONE layers. Targetless unbounded layers still
require actual target metrics. Invalid colors and earlier recording failures
cannot be revived by a later clear; failed recordings publish no scene bytes.
This change applies to the native portable COM recorder and Windows command-list
translator. It does not change the separate managed CanvasDrawingSession or
CanvasCommandList Clear contracts, nor admit automatic source-host routing.

Focused portable and Windows fixtures cover replacement, repeated/null clear,
resource reuse, retained state, first-failure identity and immutable exports.
Windows command-list counts use an independent native stream summary. The existing
Direct2D GPU fixture exercises actual scene submission/readback, checking every
pixel of translucent and null clears followed by transformed drawing. Existing
provider gates and deadlines are unchanged. These cases are authored; no local
GPU or VM execution was performed. Additional aliased clipped-clear cases cover
the captured nested clip, singular later transform, retained prefix/suffix,
immutable export, empty intersection, null/straight/IGNORE alpha and mixed-DPI
history. The same eight integral/fractional physical reference variants run cold
and warm on both native providers, with exactly three semantic draws, nine commands and one
submission. Windows additionally compares every pixel against the original
Microsoft WIC render target and exercises original command-list streaming plus
direct sink callbacks. These are authored acceptance gates, not successful
execution evidence; hosted CI must qualify the change before merge.

Fractional aliased clips preserve their original float bounds while checking
the independently expected physical sample coverage. Exterior/history pixels,
binary colors and binary alpha are exact; only nonbinary UNORM8 color conversion
allows one byte of rounding difference against the original WIC reference.

## Provenance and checkpoint

This is original ProGPU recording logic over the unchanged owned builder and
compositor at `a1060bc59e601c53619725bf6db2fa590623dc02`:
`Scene/Builder/progpu_native_scene_builder_layer.cpp`,
`Scene/Builder/progpu_native_scene_builder_geometry.cpp` and
`Backend/progpu_native_layer_composite_execution.cpp`, under
`src/ProGPU.Native/src`. No foreign implementation was imported. The shared
helper appends O(1) records and uses existing bounded GPU layer composition;
it adds no CPU pixel path, eager pipeline, per-pixel crossing or cache family.

Microsoft's [Clear contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-clear%28constd2d1_color_f%29)
defines active-clip restriction and straight/IGNORE alpha. Its
[clip contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-pushaxisalignedclip%28constd2d1_rect_f__d2d1_antialias_mode%29)
defines capture under the push-time transform and separate group-edge coverage.
The existing [cross-engine clip research](direct2d-command-stream-antialiasing.md#primary-research-and-design-decisions)
still applies: retain scoped resources and demand-driven GPU execution; do not
add text/font work or change renderer architecture for this ingress operation.
Native portable and Windows producers share the implementation. Both native
renderers consume the same existing SRC layer contract; the independent managed
recording API is neither routed through this helper nor newly advertised.

After the implementation commits, strict Apple Clang C++20 syntax checks passed
for the portable target, complete portable COM fixture and instantiated shared
GPU fixture. Source diff checks passed. This does not execute the authored
controls or qualify Windows compilation, actual GPU pixels, packages, application
routing or performance; those remain exact-head hosted gates before merge.

## Retained scene-owner dependency

This feature depends on [PR #257](https://github.com/wieslawsoltes/ProGPU/pull/257),
merged here at exact `1bcd53735e0ea31f9aa5f293e8566295ac793674` with its history
preserved. The dependency is its original scene-owner-qualified retained family
identity, not a new clipped-Clear or nonzero-origin SRC algorithm.

The independent base initially reached the shared GPU fixture but failed.
[Build 37005919682, Linux ARM64 job 110834045185](https://github.com/wieslawsoltes/ProGPU/actions/runs/37005919682/job/110834045185)
at exact `dde31d312e64ed337685a792f4a8e0a77cfef745` reported integral null-clear
variant 1 at `(12,14)`: red was `96`, expected `0`; cold and warm agreed. `96`
is the preceding distinct scene's translucent-clear red. The old family hash
omitted scene ownership, so equal resource ordinals/generations could reuse that
paint. PR #257's `11e119995cddd9da42b42a21737cd90a969e0476` includes the original
scene owner in the shared family identity and retains independent ownership
controls for both providers. Fixture isolation or changed resource ordinals would
hide this product failure and are not used.

All eight original Clear variants, strict exterior/binary pixels, one-byte
nonbinary conversion allowance, cold/warm equality, structural counters and
deadlines remain unchanged. Hosted execution of this merged head must still
establish fractional clipping, both native providers and the original Windows
differential; the diagnosed first failure does not prove later variants pass.

After the dependency merge, strict syntax checks again passed the portable target,
complete COM fixture and instantiated shared pixel fixture. The full native
contract verifier passed, including all generated schemas and its two inline-array
controls; whitespace checks passed. No native linking, GPU or VM execution was
performed locally, and no runtime was staged from either failed Build.

## Fractional replacement bounds

After the owner dependency, exact `e7fd0fa3c3732dbd2c99da8de7451ee814bbc071`
[Build 37006840556](https://github.com/wieslawsoltes/ProGPU/actions/runs/37006840556)
passed the four integral variants and reached fractional variant 4. Linux x64
job `110837003833`, MSVC `110837003805` and Windows x64 `110837003949` reported
red `0`, expected `255`, at `(12,14)`. Cold and warm agreed. The captured clip
was `[12.75,23.75) × [14.75,25.75)` but its required texture allocation was
`[12,24) × [14,26)`. SRC correctly replaces transparent source pixels, so
compositing the entire allocation erased preceding red outside the source clip.

The native producer now explicitly declares aliased final composition bounds.
The shared validator admits this flag only with BOUNDS, SRC, opacity one, no
effect/mask/cache/backdrop/composite state, and zero revisions/reserved fields.
The managed raw scene builder has the same admission, using a generated flag
constant from the C header. Original float bounds and the 64-byte layer record
remain unchanged, as do all existing flag values and ordinary layer behavior.

The shared native renderer maps the original four float edges through the actual
per-axis DPI and viewport, using the existing intrinsic four-lane projection.
For physical edge `e`, the half-open integer pixel-center boundary is
`ceil(double(e) - 0.5)`: `left <= i + 0.5 < right`. It intersects that interval
with the actual presentation and parent target, then subtracts the parent target
origin. The double subtraction preserves adjacent-float midpoint distinctions;
there is no epsilon, identity inverse, alpha-mask inference or source rounding.
The outward allocation, texture UVs, source primitive and GPU blend operation
stay unchanged. Only the final scissor changes. This is O(1) work and storage per
materialized layer, no new pipeline/pass/submission/readback or pixel loop.

Both native providers execute this shared pop-layer path. The separate managed
renderer does not deserialize this native layer wire contract: its public scene
and Canvas recording paths remain unchanged; the managed *native-wire producer*
is paired above. The cross-engine design references earlier in this document
still apply, with no text/font/cache lifecycle changes or performance claims.

Authored native controls retain original serialized bounds, reject unsupported
flag/resource/state forms without changing prior scene bytes, distinguish final
coverage from allocation, and cover exact/adjacent midpoints, empty/subpixel and
negative clips, nonzero viewport, per-axis DPI and nested target origins.
Managed raw-builder controls match valid wire identity and atomic rejections.
The original eight cold/warm GPU variants and original Windows differential,
all binary/exterior pixels, nonbinary one-byte allowance and counters remain
unchanged. This implementation still requires successful hosted execution.

Postcommit checks on `ebc9e1042` passed strict Apple Clang C++20
`-Wall -Wextra -Wpedantic -Werror` syntax for the shared state/validator,
scene-builder fixture, portable target, full portable COM fixture and
instantiated shared GPU fixture. The full native contract verifier passed,
including the newly generated constant and both literal-inline-array controls.
Four changed C# files parsed without syntax errors; this is not type compilation
or test execution. The subsequent fixture correction `5113bb108` checks rejected
managed writes *before* one-shot finalization, verifies unchanged storage and
then successfully consumes the same command ID. Its syntax check passed too.
Whitespace checks passed. No native library/renderer build, native execution,
GPU/VM execution or runtime staging was performed locally.

## WebScene fixture link dependency

At exact `d9f2be242e04a50af94e332e3f5f593d86e04350`,
[Build 37009525872, macOS ARM64 job 110845654484](https://github.com/wieslawsoltes/ProGPU/actions/runs/37009525872/job/110845654484)
compiled the WebScene provider fixture but failed its final executable link in
`Verify exact WebScene provider on Metal`. The failed command's library section
contained:

```text
libprogpu_native_dawn.dylib  libprogpu_native_direct2d_core.a  -lprogpu_native_direct2d_compat  libprogpu_native_scene_builder.a  -framework IOSurface
```

The linker reported `library 'progpu_native_direct2d_compat' not found` before
the provider test executed. There is no CMake target by that name: the actual
`progpu_native_direct2d_core` target already compiles
`src/Direct2D/progpu_native_direct2d_compat.cpp`, and both the WebScene fixture and
the standalone compatibility fixture link that core target. Removing only the
nonexistent extra library preserves the real compatibility implementation,
all provider fixture sources, pixel assertions and the original 60-second test
deadline. This is a link correction, not provider execution or pixel qualification.
