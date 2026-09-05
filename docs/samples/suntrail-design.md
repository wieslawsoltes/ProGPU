# Suntrail design and clean-room record

The expanded scope and remaining implementation work are tracked in
[the work list](suntrail-work-list.md), including import/edit compatibility and full 3D.

## Touch and iPhone performance investigation (2026-09-05)

[Apple game-control guidance](https://developer.apple.com/design/human-interface-guidelines/game-controls)
informs floating/fixed thumbstick options, separate button controls, thumb-sized jump,
optional outer-edge sprint, safe areas, and immediate visual/haptic feedback.
The touch jump fix publishes both pressed and held state before the next fixed step.
Regression tests compare every touch/keyboard jump frame at 120, 30 and 10 Hz and cover
dead zones, sprint thresholds, independent fingers, cancellation and setting changes.

[Apple shader performance guidance](https://developer.apple.com/videos/play/tech-talks/111373/)
and [GPU renderer optimization](https://developer.apple.com/videos/play/wwdc2023/10127/)
identify material specialization as a way to remove unreachable branches and reduce
register pressure. Suntrail now experiments with six cached entry points in the same
canonical shader: general artwork, sky, cliff, mountains, trees and shafts. They call
the identical shading function with a constant material kind; AA, noise, physical
resolution, lighting and painter order remain unchanged. Contiguous instance runs
replace the single draw, with O(V) scanning, fixed pipeline storage, and no additional
uploads or per-frame managed allocations. This is sample-extension-only; the existing
managed/native applicability finding below still applies. A runtime switch provides
same-binary comparison. Performance acceptance requires real device measurements;
specialization alone is not evidence of a speedup.

The first device Release run reported median/p95/p99 intervals 50.011/62.434/72.873 ms,
simulation 0.053/0.127/0.161 ms and compositor CPU 1.506/1.866/2.380 ms. These separate
CPU work from frame intervals; they do not prove GPU duration by subtraction. An
iPhone Metal System Trace was captured for correlation. A zero native Metal resource
counter on this host is unavailable instrumentation, not zero GPU memory use.

Compatibility research starts with the [TMX specification](https://docs.mapeditor.org/en/latest/reference/tmx-map-format/)
and [iNES container specification](https://www.nesdev.org/wiki/INES). An iNES header
describes cartridge storage, not a universal level format. Each game revision and each
SMBX format version needs its own independently implemented, tested adapter. No source
code or commercial level/art data is copied into Suntrail by this research.

Suntrail is an original eight-stage 2.5D platform adventure. Its visual thesis is a
warm miniature world: sculpted foliage, layered atmospheric islands, amber light,
and a small orange courier with a teal scarf. The opening is a full-canvas poster;
gameplay removes the poster and keeps a quiet HUD. Camera lag, three parallax planes,
gentle foliage motion, character stride, coin turns, and bounded particles provide motion.

## Research gate (2026-09-04)

Only public architectural contracts were consulted. No third-party implementation
text, helper layout, tables, assets, level designs, names, or characters were ported.

| Primary source | Concepts considered and disposition |
| --- | --- |
| [Skia text architecture](https://skia.org/docs/dev/design/text_overview/) and [raster quality](https://docs.skia.org/docs/dev/design/raster_tragedy/) | Preserve CPU shaping and retained glyph results. Use the existing WinUI/ProGPU text stack; do not put text into the game shader or invent glyph rasterization. |
| [Direct2D resources and domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains), [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/programming-guide), [Win2D](https://github.com/microsoft/Win2D) | Adopt device-owned persistent resources and separate CPU game data from GPU state. The Win2D/XAML integration model informs a game surface with normal UI layered over it. No DirectX interop is introduced. |
| [WebRender](https://github.com/servo/webrender) and its [documented stages](https://github.com/servo/webrender/blob/main/webrender/src/profiler.rs) | Adopt visibility before preparation/batching, retained command identity, and separate frame/build/upload metrics. Avoid a texture cache for analytic artwork. |
| [Vello](https://github.com/linebender/vello) and [Parley](https://github.com/linebender/parley) | Keep CPU input/layout preparation separate from parallel GPU coverage. A general compute vector engine is unnecessary for a bounded library of analytic sprites; use instancing. Font enumeration/fallback stays with the existing host. |
| [HarfBuzz shaping contract](https://harfbuzz.github.io/what-does-harfbuzz-do.html) | Shaping maps text to positioned glyphs, distinct from rasterization. HUD values update only when their displayed value changes; fonts/layout remain reusable CPU objects. |

Startup loads only the two Inter faces actually used and lazily creates the game
pipeline. Static level records live for the level; simulation mutates only small
entity arrays. Visibility culls artwork before a fixed-capacity sprite batch is
written. The batch is frozen between UI update and submission. Each generation
uploads once; paused replay retains both sprite and uniform buffers. There is no
per-sprite P/Invoke, texture upload, path atlas entry, worker dispatch, or readback.
The small fixed simulation workload stays on the UI thread; workers would add
coordination without demonstrated benefit. This decision must be revisited if the
workload expands beyond the bounded campaign.

Physical framebuffer projection and derivative coverage preserve DPI. Artwork has
no glyph cache, variable font state, font fallback, texture cache eviction, or atlas
generation of its own. Those contracts remain entirely in the existing ProGPU UI
renderer. Pipelines are compositor/device-owned and disposed with that compositor;
a recreated compositor must register a fresh extension. Shader source is one
embedded canonical WGSL file used on every target. Fixed loops and storage costs
are documented at its top.

## Managed/native applicability audit

The new extension is an application-owned `ICompositorExtension`, registered by the
Suntrail WinUI application. Desktop, iOS, and browser use that same C# compositor
extension with their existing native/WebGPU dispatch implementation. No renderer,
scene compiler, C ABI, shader in ProGPU.Scene, glyph/path cache, or native resource
algorithm is changed. The only WinUI assembly change grants the sample test
assembly access to install and restore Application.Current, matching the real
application resource scope in its render fixtures. `ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.cs` has no
application extension registration or WinUI application host contract; it only
recognizes its enumerated built-in commands. Accordingly this sample does **not**
advertise the independent C++ retained renderer as a supported host. Adding that
host would require a paired typed native extension contract and matched tests,
not an approximate C++ artwork fork. Current cross-platform parity is one shared
game implementation and one shader source, exercised through the managed compositor.

## In-repository provenance

The resource/pipeline API contracts were read from `ProGPU.Scene/ICompositorExtension.cs`,
`Extensions/CustomGridExtensionPipeline.cs`, `Extensions/ShaderToyExtensionPipeline.cs`,
and `ProGPU.Backend/GpuBuffer.cs`. New instancing, artwork, simulation, and layout are
original. Host wiring follows the existing `ProGPU.Samples.Desktop`,
`ProGPU.Samples.iOS`, and `ProGPU.Samples.Browser` projects. The browser bootstrap is
a link to `ProGPU.Browser/BrowserAssets/progpu-browser.js`, not a fork.

## Contracts to validate

All eight stages must complete using input-only traversal, without teleporting or
disabling damage. Tests cover deterministic replay, fixed-step partitioning, jump
height, checkpoints/death, pause, capacity, and shader embedding. GPU captures must
show every biome, actor silhouettes, readable controls, moving scenery, and physical
DPI output. Report Release p50/p95/p99 CPU work and frame intervals separately,
startup/first-use, uploads, allocations, and bounded residency. Instruments traces
are required for macOS performance claims; a new sample has no predecessor benchmark
and must not claim a measured speedup. Browser AOT and iOS simulator/device evidence
must be distinguished from successful compilation alone.

## WGSL validator compatibility

The [WGSL derivative uniformity rules](https://www.w3.org/TR/WGSL/#derivative-uniformity)
conservatively treat flat fragment inputs as non-uniform. Sprite material is constant
across each primitive, so every interior derivative quad selects the same artwork;
edge helper invocations run that same primitive. The canonical shader explicitly
filters this diagnostic and performs clipping only after derivative calculations.
The pinned 2024 native Naga cannot parse diagnostic controls. The startup loader
removes exactly that directive on native hosts; executable WGSL, derivatives,
coverage, complexity, buffers, and artwork remain identical. This is syntax
compatibility, not an alternate shader algorithm or reduced-AA path.

The test project links the repository-owned `ProGPU.Tests.Headless/PngEncoder.cs`
for lossless review captures. No PNG encoder implementation was copied from outside
ProGPU. The full-window surface extends behind host system insets; normal WinUI
menus, HUD, and two-thumb controls use the host's safe-area values.

## Complexity and ownership details

For the fixed campaign, each 120 Hz step visits P platforms, E enemies, C pickups,
and at most 128 pooled particles: O(P + E + C), with no managed allocations. Frame
preparation scans those bounded records, culls them, and adds O(W) decorative records
for viewport width W in world units. A fixed 2,048-record arena makes overflow an
explicit error. Each visible record is 48 bytes. The GPU executes one instanced
artwork draw; UI uses the normal retained ProGPU paths. Active animation uploads the
visible batch once, while paused replay uploads nothing. Per-fragment work is bounded
by the selected analytic sprite, independent of campaign size.

Keyboard state and per-pointer touch capture are separate. Cancellation, lost capture,
window deactivation, and menu transitions release held state. Touch controls occupy
opposite sides of the safe area. A scene generation freezes before compilation and
changes only in the host animation update; `OnRender` records one command and does
not mutate gameplay or schedule another frame.

## World and material revision

The campaign now has eight separate biome IDs and authored elevation scores. World
geometry, landmark silhouettes, terrain materials, airborne particles, platform
arrangements, and optional routes vary. Static arch ruins replace windmills; no
vertical waterfall shader remains. The cave suppresses outdoor clouds; snow omits
summer flowers and mushrooms. Local art is deterministic from integer-coordinate
hashes, continuous cubic noise, fixed three-octave detail, and analytic coverage.
No third-party source or bitmap assets were introduced.

The frame uniform is 288 bytes: transform (64), scene (16), clip (16), exactly
three positional lights (48), occlusion count/padding (16), and eight conservative
opaque rectangles (128). Checkpoints and the exit populate these emitters on
the CPU in screen coordinates; fragments evaluate bounded quadratic falloff. This
adds no texture pass, readback, per-light resource, or native crossing. The same
canonical shader and matching layout run on Desktop, iOS, and Browser. There is no
applicable standalone native C++ host, as recorded in the applicability audit above.
Noise, derivative-based material normals, and fern layering increase fragment cost;
performance results must identify this revision separately from the initial artwork.

Opaque ground interiors are inset by at least eight world units and four logical
pixels, preserving noisy/antialiased edges. The fragment shader skips only background
art behind those interiors, not foreground actors or particles. More than eight
visible ground spans disables this optimization explicitly. A toggle in the sample
batch and Desktop `--no-occlusion` permit matched measurements of the exact same
binary. GPU tests compare exact enabled/disabled pixels in all eight worlds before
and after scrolling, alongside unchanged replay/upload checks. Golden captures from
before the optimization are also byte-identical. No material detail or AA setting
was reduced to obtain this culling.


## iOS device packaging

Silk's iOS startup loader resolves the statically linked WebGPU functions through
`NativeLibrary.GetMainProgramHandle`. A native linker export option alone is
insufficient: the .NET device post-processing stripper retains only its managed
P/Invoke symbol list and removes these runtime-resolved exports. The sample keeps
native symbols with the documented [`NoSymbolStrip` build property](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties#nosymbolstrip)
while retaining full managed trimming and AOT. This increases packaged native
symbol metadata; it does not add per-frame work or change the rendering algorithm.
`eng/verify-suntrail-ios.py` checks the final Mach-O export table, after stripping
and signing, for essential adapter/device/pipeline/submission/presentation functions.
Both device and simulator packages must pass. This is a host packaging correction;
the standalone C++ renderer, Desktop, and Browser symbol resolution are unaffected.


## Vaults and timed obstacles

Optional vaults are preconstructed with each surface level. Pipe entry changes the
active level reference and camera/spawn state without recreating collectibles. Return
restores the surface entry position; death/retry restores the surface checkpoint.
The fixed simulation clock continues through room changes, and each transition clears
standing-platform, jump-buffer and particle state. Down/S and a touch arrow are explicit
entry actions; accidental contact never changes rooms. Closed ceilings use a solid
stone platform kind; moving/one-way ledges retain their original collision contract.

Saw motion is analytic sinusoidal translation. Flame jets have a warning interval
before their active collision interval; crushers retract over 40% of a 3.2-second
cycle, hold, drop over 10%, and rest. Their immutable definitions are shared by visual
preparation and collision. Per-step cost adds O(H) for H mechanisms, with no allocation;
render preparation adds only visible fixed-size instance records. Procedural conduit,
saw, jet and crusher art is original and uses bounded analytic shapes. The uniform's
previously reserved occlusion Y component identifies an underground room, retaining
its world material while suppressing the outdoor sun and clouds.
# Routed joystick input correction (2026-09-05)

The iPhone joystick failure came from WinUI CPU hit testing: `HasBackground`
recognized Control and Border but omitted Panel, so even the joystick Grid's
assigned transparent background passed touches through to GameSurface. The shared
input path now recognizes Panel.Background. Null backgrounds still pass through;
transparent assigned brushes accept hits. The public contract was checked against
[Microsoft’s hit-testing contract](https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/events-and-routed-events-overview#hit-testing-and-input-events).
The implementation is original and adds one constant-time type/property check to
the existing traversal, with no native crossing or rendering algorithm change.
The native C++ renderer has no WinUI routed-input implementation to update.

Thumb feedback uses bounded two-axis displacement inside a 44-point radius while
the gameplay axis retains its horizontal dead zone and sprint threshold. It updates
existing child margins and opacity through ordinary layout/visual invalidation.
No animation timer, input smoothing delay, shader, or per-frame polling is added.
Regression tests inject platform-style pointers into the actual arranged phone root,
check captured off-control movement and simultaneous jump, and compare rendered
centered/dragged frames without advancing simulation.


## Retained full-precision sky experiment

`ProceduralPipeline.SkyCache.cs` caches only the static sky entry point from the
original canonical `Shaders/Suntrail.wgsl`; it does not cache moving clouds, foliage,
terrain, dynamic lights, or the camera. The existing research on retained immutable
resources (Skia, WebRender, Vello, Direct2D/Win2D) informs the ownership and invalidation
choice; no external implementation was copied. The same original shader is baked by
the same WebGPU device and replayed by Desktop, Browser and iOS managed extensions.
The native C++ renderer has no corresponding WinUI sample extension. No C ABI or
core renderer algorithm changes are required.

The cache is opt-in pending representative performance validation. One RGBA32Float
image preserves the shader's float output before the normal target-format conversion.
Replay uses `textureLoad` at the identical physical pixel, with no reduced resolution,
filtering, compression, or lower precision. Current opaque-ground masks are checked
at replay, so scrolling never reuses obsolete visibility. The key includes logical
and physical dimensions, world, room, and tint; only identity-transformed full-viewport
skies with integral physical dimensions are eligible. Unsupported extents and
transforms use live shading. A device owns its texture and bind groups; disposal
queues their release through the existing submission lifetime.

Preparation runs through `TryPrepareDrawCall` before the compositor render pass. A
miss adds one bounded GPU bake submission and 336 uploaded bytes; a hit adds no
upload or submission. The current image is capped at 96 MiB and 4096 pixels per axis,
with normal deferred release potentially retaining the preceding image until its
in-flight submission finishes. At 932×430 logical pixels and 3× DPI it occupies
57,709,440 bytes. The image is reused across animation time, scrolling, and lighting
changes; it never contains those changing effects. GPU state remains confined to
the sample's single procedural batch per compositor.

The `--render-benchmark OUTPUT on|off FRAMES` Desktop harness advances exactly two
120 Hz simulation steps per frame, resets the route after 120 warmup frames, and
measures submission CPU and serialized GPU completion separately. It is deliberately
labelled latency, not displayed FPS. Both settings use one final Release binary,
identical input, framebuffer, MSAA, allocations and upload workload. Device FPS still
requires a later iPhone run after reconnection.


## Exact transparent-coverage rejection

The original ProGPU-owned sphere, canopy and mountain implementations in
`src/ProGPU.Samples.Suntrail/Shaders/Suntrail.wgsl` (parent `ce5c49fa`) are the source
provenance. Coverage is evaluated once before an optional exactly-zero-alpha return;
visible lanes reuse that value, keeping derivatives ahead of nonuniform control flow.
This skips only invisible lighting work. Worst-case time and private storage remain
O(1) per fragment with the existing fixed loop bounds; instances, uploads, buffers,
shader precision and submissions do not increase. `EnableEarlyCoverage` changes one
existing uniform component and marks it dirty through the existing version contract.

The previously consulted [Apple shader optimization guidance](https://developer.apple.com/videos/play/tech-talks/111373/)
and [GPU performance guidance](https://developer.apple.com/videos/play/wwdc2023/10127/)
inform reducing unnecessary fragment work and checking compiler/branch tradeoffs.
The [WGSL derivative and uniformity contracts](https://www.w3.org/TR/WGSL/)
inform evaluating coverage before the return, rather than recomputing derivatives
inside divergent lighting control flow. No external shader implementation was copied.
The earlier cross-engine research table still governs ownership, reuse and culling;
this change introduces no new renderer, scene, text, or pipeline architecture.

Applicability: all three managed WinUI sample hosts consume this same canonical
shader. The C++ renderer has no corresponding Suntrail application extension, so
there is no native algorithm fork or C ABI change. Matched on/off pixel tests
exercise the shared sample implementation. Device performance remains unverified
until the user reconnects the iPhone.

Adding eight more dedicated background entry points was tested and rejected.
Additional branch specialization increased pipeline startup and painter-order draw
switches without a consistent benefit. The six existing pipelines are retained.
