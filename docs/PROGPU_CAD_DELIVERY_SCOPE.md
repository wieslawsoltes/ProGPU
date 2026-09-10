# Focused CAD delivery scope

Scope reset: 2026-09-08, at the user's request. Target a usable rendering-first
milestone in a few days, not complete AutoCAD feature parity. Freeze feature
expansion; preserve existing work without treating every experimental tool as a
release requirement.

## Required for this milestone

1. Open representative DXF/DWG documents and render the supported common 2D
   entities, blocks, hatches, line styles/weights, and text using the existing
   ProGPU quality contracts. Report unsupported content explicitly; never claim
   whole-file fidelity when content is omitted.
2. Make fit, pan, zoom, resize, and high-DPI rendering reliable on desktop and
   browser. Keep the drawing area usable and resolve visible defects before
   extending entity/tool coverage. Preserve retained scene reuse.
3. Finish basic editing: selection, move/copy, delete, undo/redo, basic primitive
   creation, and saving/reopening supported edits. Existing advanced commands
   may remain but do not add new ones for this milestone. Writer certification
   is a separate claim; development output must remain identified as such.
4. Run focused checks while implementing, then a concentrated validation pass:
   representative drawings, render screenshots, interaction, edit/save/reopen,
   desktop/browser Release builds, real browser AOT startup, and green PR CI.
   Keep generated screenshots, traces, and benchmark outputs ignored under
   `artifacts/`; upload CI evidence as workflow artifacts, not source commits.

## Deferred

Advanced drafting and editing, further 3D/modeler expansion, new print/page-setup
features, exhaustive entity coverage, exhaustive SHX/font compatibility, and
AutoCAD-wide output certification. Existing implementations are not removed.
Any discovery requiring one of these expansions is recorded as a limitation
unless it blocks a required basic workflow.

## Rendering-first workspace

The shared desktop/browser sample reserves 104 logical pixels for two compact
command rows instead of 532. File/view actions and common edits stay immediately
available. "More tools" opens the existing advanced controls in a bounded,
two-axis scrollable area; "Fewer tools" restores the drawing space. The toggle
stays pinned at the right edge on narrow windows. Each compact row can scroll
horizontally without scrolling the drawing.

Controls are moved once during construction, not recreated or reparented on
toggle. Document generation, control identity, and in-progress control
values remain intact. The sample explicitly invalidates layout and pixels when
the reserved toolbar height changes. This reuses the existing ProGPU `Grid`,
`StackPanel`, `ScrollViewer`, and theme resources; it adds no scrolling engine,
renderer algorithm, shader, native ABI, or rendering performance claim. The same
application visual tree feeds managed/native rendering, so no C++ algorithm
change applies.

`CadSampleWorkspaceTests` verifies drawing height at 1280x800 and 800x600,
expansion/collapse invalidation, retained controls, and unchanged document state.
The browser smoke captures expanded controls as well as the drawing and continues
to exercise pan, zoom, resize, save, and reopen from the compact toolbar.

Validation for the compact workspace: 1,542 Release CAD tests pass, the browser
Release AOT publish completes native linking, and the Chrome hardware/SwiftShader smoke
passes with visible drawing pixels, pan/zoom, 15 retained model-space entity
types through save/reopen/resave, and a 2880x1800 physical framebuffer after
resize. Initial and expanded-panel screenshots were inspected. This is workflow
validation, not a claim of comprehensive CAD visual fidelity or performance.

## Execution order and finish gate

Planning target: **3–4 working days**, conditional on the final rendering and CI
gates below. This is a timebox for the reduced milestone, not an estimate for
the broad CAD goal or a guarantee of completion.

- Days 1–2: close visible rendering and desktop/browser host blockers on the
  representative fixtures. Prioritize missing common geometry/text, pan/zoom,
  DPI, and the browser CI blank-frame failure. Do not start general-purpose
  implementations for isolated unsupported entities.
- Day 3: close only basic selection, move/copy, delete, undo/redo, primitive
  creation, and save/reopen defects. Retain already working advanced tools
  without expanding or certifying their full feature sets.
- Final day: freeze features; validate final Release binaries, representative
  screenshots and interactions, supported edit round trips, and PR CI. If a
  required gate still fails, report that blocker and revise the target rather
  than weakening the check or declaring the milestone complete.

Admission rule: new work must fix a reproducible defect in one of these required
workflows or unblock its validation. Broader entity coverage, architectural
expansion, and extra editing commands go to the deferred roadmap. Run focused
regressions with each fix; reserve extensive validation for the feature freeze.

Spend the first part of the few-day window on rendering and host blockers, then
basic editing gaps, with the final part reserved for regression fixes and CI.
This is a target, not an evidence-backed completion date: adjust the estimate
when representative file or runtime validation exposes new blockers.

Done means the bounded workflows above work on the agreed representative
fixtures, known unsupported content is documented, and required checks are green
on the final commit. Compilation, unit-test totals, or a blank running canvas
alone do not satisfy that gate. The broad CAD roadmap remains unfinished.

Current blockers and evidence are tracked in the
[representative rendering audit](PROGPU_CAD_REPRESENTATIVE_RENDERING_AUDIT.md).
The basic-editing [gradient data-preservation fix](PROGPU_CAD_GRADIENT_DATA_PRESERVATION.md)
does not expand the milestone to gradient authoring or exhaustive entity support.
Earlier Linux CAD browser CI runs produced blank initial screenshots;
local browser success alone does not close that blocker. The smoke test rejects
both blank light and blank dark frames and retains console diagnostics. Input
settling no longer requires an arbitrary number of idle GPU submissions; actual
pixel changes and file round trips remain required. CI must pass these checks
before the milestone is ready.
The [Linux presentation investigation](PROGPU_CAD_BROWSER_VALIDATION.md#linux-presentation-isolation-2026-09-08)
now reproduces the problem independently of CAD and obtains a complete local
Linux smoke pass by retaining Vulkan surfaces. Final CI qualification is still
required; no feature scope or pixel threshold was reduced.
