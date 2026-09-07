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

## Execution order and finish gate

Spend the first part of the few-day window on rendering and host blockers, then
basic editing gaps, with the final part reserved for regression fixes and CI.
This is a target, not an evidence-backed completion date: adjust the estimate
when representative file or runtime validation exposes new blockers.

Done means the bounded workflows above work on the agreed representative
fixtures, known unsupported content is documented, and required checks are green
on the final commit. Compilation, unit-test totals, or a blank running canvas
alone do not satisfy that gate. The broad CAD roadmap remains unfinished.
