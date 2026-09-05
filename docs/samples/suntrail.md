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
| Move | Arrows or A/D | Left/right buttons |
| Jump | Space, W, or up; hold for height | Hold JUMP |
| Sprint | Shift | Hold RUN |
| Pause/resume | Escape or P | Pause / resume button |
| Continue / retry | Enter | Primary menu button |
| Return to checkpoint | R | Retry after falling |

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
