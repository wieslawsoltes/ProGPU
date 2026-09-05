# Suntrail remaining work

User-requested scope, updated 2026-09-05. PR: https://github.com/wieslawsoltes/ProGPU/pull/158.
Items remain open until implementation and representative validation are complete.

1. Finish responsive iPhone touch controls: floating/fixed thumbsticks, arrow buttons,
   configurable sprint and button sizes, simultaneous movement/jump, equal jump physics.
2. Measure real iPhone Release performance, identify GPU/CPU bottlenecks, optimize without
   reducing artwork quality, and verify sustained frame pacing on the installed game.
3. Replace repetitive campaign geometry with distinct authored encounters per world:
   branching routes, vertical rooms, dungeons, pipe travel, and varied moving hazards.
4. Support loading NES Super Mario Bros. `.nes` level data, TMX/JSON tile maps, and
   SMBX `.lvl`/`.lvlx` levels. Implement independent format adapters, explicit unsupported
   feature diagnostics, and fixture-based compatibility tests. Support user-supplied
   Mario artwork/character data; do not bundle extracted commercial game assets.
5. Add an in-game drag-and-drop level editor with selection, placement, movement,
   deletion, undo/redo, save/load, and play testing. Integrate the supported Mario
   level formats and preserve format-specific data where round trips are supported.
6. Validate Desktop, Browser AOT, and iOS; reinstall on the iPhone, update PR and evidence,
   and remove unneeded raw performance traces after exporting useful measurements.
7. **Last:** add a switch between 2.5D and full 3D gameplay. This means a genuine depth
   axis, 3D camera/rendering and collision/input behavior, not a perspective filter.
   Validate controls, level interpretation, and performance in both modes on all hosts.

The iPhone black screen was fixed and the user confirmed gameplay. Smooth iPhone FPS
is not yet verified. The controls pass targeted input tests and native visual checks. Eight optional vaults,
two-way pipe travel, and three timed hazard families are implemented and tested.
The main campaign still needs a broader encounter redesign; new rooms do not by
themselves complete that request. Current sample validation: 97 Release tests. The reported unresponsive joystick was
reproduced through platform pointer injection and fixed in the shared WinUI panel
hit-test path; two-axis thumb feedback now repaints during dragging.
Full Mario format compatibility, editor completeness and full 3D remain open.

Next rendering investigation: retain expensive static procedural materials in a bounded
GPU texture cache, while keeping animated foliage, lights, and other changing effects
dynamic. Any such change needs physical-resolution and device-generation keys, bounded
residency, correct invalidation, image comparisons, and matched iPhone Release traces.
The six-pipeline specialization improved the measured fragment cost but is insufficient.

An opt-in full-precision sky cache and fixed-input GPU latency benchmark are now
implemented. Exact pixels pass across worlds, vaults and Retina scales. The cache
remains disabled by default because its memory cost and missing device validation
do not yet justify enabling it on iPhone. Device work stopped after installing
the joystick fix (`29366d4f`), as requested; further device validation awaits reconnection.

Exact-zero coverage rejection now skips invisible sphere/canopy/mountain lighting
without changing compared pixels or resource cost. The fixed-input benchmark supports
all eight worlds and explicit baseline/coverage options. This is an incremental
shader improvement; it does not close the smooth-iPhone-FPS requirement.

The pause-action hover defect is fixed by explicitly applying the existing Fluent
button style. Routed mouse/touch tests and the native reproduction pass.

All eight main routes now have authored section widths, elevations, gaps and
encounter sequences, including main-path tunnels, brambles and timed mechanisms.
Ordinary-input completion passes. Further distinct mechanics, branching-room travel,
format compatibility and editor support remain open; this is not a completion claim
for the expanded world-diversity request.

The user reconnected and requested installation. The authored-campaign/menu revision
with early coverage is installed and launched on iPhone (2026-09-05 11:48 local).
Device work is authorized again. Smooth FPS and real-device control feedback remain
open; this launch alone does not close those requirements.

The first workshop now supports mouse/touch palette placement, selection and drag,
one-step drag undo, redo, deletion, width edits, biome changes, bounded JSON save/load
and isolated playtests. Original finite Tiled object-map JSON/TMX adapters are tested;
At that stage tile layers, NES and SMBX data, connected custom pipes, asset import
and robust format round trips remained open. Desktop drag and playtest were visually
checked. Browser AOT and the application-owned extension API integration now pass.

Public drawing-extension registration was split into PR #159 and merged into main.
This branch merged main and uses the typed API across the game, GPU fixtures and
measurement tools. A procedural-pixel regression covers mobile surface recreation.
This completes the API extraction/integration request, independently of the open
gameplay, compatibility, editor and full-3D scope above.

Finite Tiled tile layers now compile embedded gameplay classes from JSON arrays,
TMX CSV/XML and raw/gzip/zlib base64. Matching solid runs coalesce into bounded
rectangles; independently authored and randomized fixtures verify occupancy,
format equivalence, corruption limits and ordinary-input completion. External
tileset/asset bundles, infinite maps, zstd, NES and SMBX remain open.
