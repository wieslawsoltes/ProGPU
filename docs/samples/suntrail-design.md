# Suntrail design and clean-room record

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
