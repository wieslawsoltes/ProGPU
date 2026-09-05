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
themselves complete that request. Current sample validation: 46 Release tests. The reported unresponsive joystick was
reproduced through platform pointer injection and fixed in the shared WinUI panel
hit-test path; two-axis thumb feedback now repaints during dragging.
No level-format compatibility, editor, or full 3D completion is claimed by this list.

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
