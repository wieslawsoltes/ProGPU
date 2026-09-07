# Suntrail

An original scrolling 2.5D platform adventure: eight distinct worlds, a fox
courier, sunsparks, hidden relics, patrolling beetles, moving platforms, checkpoints,
and a final ending. All artwork is deterministic original WGSL; no downloaded game
assets, image generators, copied levels, or third-party game implementation are used.

![Lumen Caverns](images/suntrail-world-3.png)

![Frostbound Peaks](images/suntrail-world-6.png)

## Run

Use .NET 10 and the repository's normal native WebGPU prerequisites. Run commands
from the repository root. Initialize `external/microsoft-ui-xaml` for Fluent resources.

```sh
git submodule update --init external/microsoft-ui-xaml
dotnet run --project src/ProGPU.Samples.Suntrail.Desktop -c Release
```

Desktop uses the existing GLFW host. macOS is the exercised desktop platform;
Windows and Linux use the same project and their existing ProGPU runtime packages.

```sh
dotnet publish src/ProGPU.Samples.Suntrail.Browser -c Release
python3 eng/serve-suntrail.py
```

Open `http://127.0.0.1:5187` in a WebGPU-enabled browser. Release publishes with
WebAssembly AOT. Deploy the published `wwwroot` under HTTPS with the COOP/COEP
headers shown in the development server. The browser bootstrap is shared with
ProGPU.Browser. Progress uses localStorage; private/blocked storage stays playable.

```sh
bash eng/build-wgpu-native-ios.sh
dotnet build src/ProGPU.Samples.Suntrail.iOS -c Release -r iossimulator-arm64
```

Install the resulting `.app` with Xcode or `xcrun simctl install booted <app-path>`.
Device builds use `-r ios-arm64` and require your signing/provisioning identity.
The simulator uses the interpreter; device Release retains AOT. Landscape is the
intended phone orientation. The host explicitly exports the static WebGPU C API
for Silk's startup symbol resolver. `NoSymbolStrip` preserves these runtime-resolved
exports through device Release post-processing (managed trimming and AOT remain enabled).
Verify the final packaged executable before installing it:

```sh
python3 eng/verify-suntrail-ios.py src/ProGPU.Samples.Suntrail.iOS/bin/Release/net10.0-ios/ios-arm64/ProGPU.Samples.Suntrail.iOS.app
```

## Play

| Action | Keyboard | Touch |
| --- | --- | --- |
| Move | Left/right or A/D | Floating/fixed thumbstick or arrow buttons |
| Jump | Space, W, or up; hold for height | Hold JUMP |
| Sprint | Shift | Outer thumbstick edge, automatic arrows, or separate RUN |
| Enter/leave a vault | Down or S while standing on a pipe | ↓ while standing on a pipe |
| Pause/resume | Escape or P | Pause / resume button |
| Continue / retry | Enter | Primary menu button |
| Return to checkpoint | R | Retry after falling |

Open Settings from the title or pause menu to choose a touch layout, sprint behavior,
and button size. Settings are saved on each device. Hold JUMP for the same full-height
arc as the keyboard; two independent fingers can move and jump simultaneously.

Land on beetles to bounce. Side contact and thorns cost one of three hearts.
Lanterns save a checkpoint; falling allows unlimited retries and retains collected
sunsparks. Reach the glowing arch to unlock the next island. Relics are optional
exploration goals. The island menu replays unlocked stages. Losing focus pauses the
game and clears held input. Unlocks are saved locally; no account or network service
is required.

## Worlds

| World | Artwork and route character |
| --- | --- |
| Verdant Isles | Rough orchard canopies, moss, roots, ferns, gentle meadow leaps |
| Sandstone Reach | Layered sandstone, palms, a repeated aqueduct, broad elevated shelves |
| Lumen Caverns | A dark rock ceiling, faceted crystals, ascending shelves and vertical lifts |
| Tidal Kingdom | Broken coastal causeways, distant horizontal sea, reeds, longer low terraces |
| Autumn Highlands | Copper canopies, falling leaves, climbs and descents through tall terraces |
| Frostbound Peaks | Snow boughs, ice fractures, snowfall, alternating heights and vertical lifts |
| Obsidian Forge | Basalt columns, warm fissures, ember sea, denser thorn challenges |
| Celestial Gardens | Marble pillars, pale ledges, cloud colors, the longest sequence of upper routes |

Each world now has an enterable pipe to an optional underground vault. Both vault
pipes return to the surface entry; collected coins persist across visits, and retry
returns to the surface checkpoint. Vault geometry and materials vary by world.
Selected upper galleries and relic routes add oscillating saws, flame jets with
advance warning, and crushers with a slow retraction and rapid drop.

Each world has its own authored elevation score, section lengths, obstacle rhythm,
three optional relic routes, and two checkpoints. The campaign uses 10–13 ground
sections per world. Horizontal ferries and vertical lifts use the same collision and
carry rules as the player. Windmills and vertical waterfall strips are removed.
Materials use original bounded noise, fractured height shading, bark/fur detail,
soft dust, atmospheric shafts, and three local lantern/portal lights. The artwork
remains a procedural stylized interpretation, not photorealistic scanned assets.

## Structure and checks

- `ProGPU.Samples.Suntrail`: shared WinUI interface, fixed 120 Hz simulation,
  deterministic level grammar, bounded artwork batch, drawing-context extension,
  and one canonical shader.
- `.Desktop`, `.iOS`, `.Browser`: platform startup and packaging.
- `.Tests`: input-only completion of all eight stages, simulation regressions,
  warm simulation/batch allocation checks, native GPU validation, eight-world
  captures, UI captures, and unchanged-frame upload checks.

```sh
dotnet test src/ProGPU.Samples.Suntrail.Tests -c Release
dotnet run --project src/ProGPU.Samples.Suntrail.Desktop -c Release -- \
  --benchmark artifacts/suntrail/release-run 1200
```

`--world 1` through `--world 8` selects a starting world for focused play or measurement.
`--autoplay` enables the input-only route pilot without the benchmark recorder.
`--no-occlusion` disables conservative background culling for same-binary comparisons.
The benchmark writes raw CSV plus startup, host-frame/compositor percentiles, allocations,
uploads, and resource counters. Instrumented runs must be reported separately from
normal frame rates. See [design/research](suntrail-design.md) and
[validation evidence](suntrail-validation.md).

The extension is a full-window game surface. It owns its projection and one
compositor/device's buffers; it is not a general masked/nested drawing primitive.
The independent C++ retained renderer is outside this WinUI sample's host contract.

Expanded campaign work, Mario format import/edit support, and full 3D remain in
[the work list](suntrail-work-list.md). These features are not all implemented yet.


For deterministic offscreen GPU measurements, run the Desktop executable with
`--render-benchmark OUTPUT_PREFIX off|coverage|on 600 [WORLD]`. World is 1–8
(default 1): `off` disables the two experimental switches, `coverage` enables the
normal early-coverage optimization, and `on` tests only the optional sky cache.
Each run warms 120 frames and records 600 frames of identical simulation input;
CSV timings measure serialized completion latency, not displayed FPS. Ordinary
play enables early coverage and keeps the sky cache off unless `--sky-cache` is set.


The main campaign now alternates short crossings with authored chambers and terraces.
Low tunnels have a walking route beneath the roof and optional steps onto it; some
relics sit above those passages. Later worlds put saws, flame gates and crushers on
the main route. Lantern checkpoints recognize upper-route crossings and always
respawn you on their safe floor.


## Level workshop

Choose **Level workshop** from the title or pause menu. Drag a palette item onto
the map, or tap a tool and then tap the map. **Select / drag** moves existing
objects; **Undo**, **Redo**, **Delete**, and width controls edit the draft. **World**
cycles the eight procedural environments. **Play test** starts a separate playable
snapshot. Pause and return to the workshop to resume editing; playtest completion
does not unlock campaign levels.

**Save** writes a `.suntrail` version 1 JSON document. **Open** accepts these files
and finite orthogonal Tiled `.json` / `.tmx` maps. The object-map adapter
uses rectangle and point objects with a gameplay class: `ground`, `ledge`,
`moving`, `crate`, `pipe`, `stone`, `coin`, `relic`, `enemy`, `hazard`, `checkpoint`,
`spawn`, `exit`, `saw`, `flame`, or `crusher`. Map properties `suntrail.name` and
`suntrail.biome` select the title and environment (0–7). Object properties
`travel`, `phase`, and `verticalTravel` configure supported movement. Nested
object groups and pixel offsets are supported. Coordinates use the game's
logical units; spawn uses the player's top edge, checkpoint/exit use floor
height, and coin/relic use their center. Enemy collision size is 42 × 34.

Import is a conversion into Suntrail gameplay geometry and procedural artwork.
Tiled editor colors, visibility, opacity and drawing order do not select gameplay
rules. Saving creates a Suntrail copy; it does not round-trip Tiled metadata.
Image layers, rotated/nonrectangular objects, templates, external assets,
NES cartridges and SMBX files are not implemented yet. Custom pipes currently
provide solid pipe geometry; authoring connected room destinations remains open.

Documents are capped at 1 MiB and 256 objects, with bounded coordinates, motion
and procedural artwork cost. Exactly one spawn and exit are required. Validation
checks structural limits; authors must still playtest reachability. Save drafts
before leaving the application; automatic draft recovery is not implemented.

The independent readers follow the official [Tiled JSON specification](https://doc.mapeditor.org/en/stable/reference/json-map-format/)
and [TMX specification](https://doc.mapeditor.org/en/stable/reference/tmx-map-format/).
Only their public field contracts informed this original implementation; no Tiled
source or commercial game assets are included. Paired authored JSON/TMX fixtures
verify equivalent geometry and ordinary-input playtest completion.


## Application-owned GPU registration

Suntrail uses the public typed registration API from merged [PR #159](https://github.com/wieslawsoltes/ProGPU/pull/159).
Its shared `DrawingExtension<ProceduralBatch>` definition creates a separate
`ProceduralPipeline` for each compositor. `App` registers it on the window before
activation, and the drawing-context helper records it with local bounds. Mobile
surface recreation automatically receives a fresh pipeline; numeric extension IDs
and manual registration in the activation handler are removed.

The application uses public APIs only. Its test assembly's existing friend access
is limited to installing/restoring the application's resource scope in fixtures.
See [package-consumer usage and ownership](../drawing-extensions.md). The API is
merged into main; it still needs a package release before consumers can use it from
an ordinary published NuGet version.


### Tiled tile layers

Finite tile layers support JSON integer arrays and TMX CSV or individual `<tile>`
elements. Both formats also accept base64 data containing little-endian 32-bit tile
IDs, optionally compressed with gzip or zlib. Embedded tileset `type` / `class`
values use the same gameplay names as object layers. Multiple tilesets, sparse
local IDs and nested pixel offsets are resolved before compiling gameplay objects.

Contiguous static `ground`, `ledge` and `stone` cells with matching properties merge
into collision rectangles. Individual actors, moving platforms and mechanisms keep
their identity. Coins use cell centers; spawn/enemy markers align their feet with
the bottom of the cell; checkpoints and exits use bottom-center coordinates.
Horizontal/vertical/diagonal tile flags preserve symmetric whole-cell solids;
transformed actors and mechanisms report an unsupported-transform error. The stale
hexagonal rotation bit is cleared for orthogonal maps as required by Tiled's
[GID contract](https://doc.mapeditor.org/en/stable/reference/global-tile-ids/).

The importer caps all layers together at 65,536 cells and 4,096 tilesets/gameplay
definitions, then enforces the usual 256-object/artwork limits. Decompression reads
only the declared cell bytes and checks for extra or missing data. Invalid IDs,
overlapping tileset ranges and unsupported encodings fail transactionally. External
TSX/TSJ dependencies, zstd compression, infinite chunks and custom per-tile collision
object groups remain open. Embed tilesets and use whole-cell classes for this lane.
Tileset images are not imported: the map becomes Suntrail procedural gameplay art.

The independently authored [tile-crossing fixture](levels/tile-crossing.tmx) has
three stretches of ground separated by two gaps. It imports, plays to completion
and remains editable. Its 504 solid cells compile to three ground rectangles.
This extends Tiled compatibility; it does not establish NES or SMBX compatibility.
