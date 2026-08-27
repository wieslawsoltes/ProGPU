# System.Drawing cumulative graphics context contract

## Source contract

The .NET 10 `Graphics.GetContextInfo` overloads expose cumulative transform and
clip state across saved graphics contexts. The allocation-conscious overloads
return the cumulative translation in a `PointF` and optionally an independently
owned `Region`; an infinite clip is represented by null. The obsolete no-argument
form returns an object array containing a `Region` and complete `Matrix`.

Primary contract source:

- [Graphics.GetContextInfo](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.getcontextinfo?view=windowsdesktop-10.0)

Canonical WinForms uses the typed offset/clip overload in its drawing-event
state check to detect a transform or clip leaked by a renderer. The portable
implementation therefore needs real cumulative state rather than a bypass or
fabricated default.

## Portable implementation

ProGPU records the context transform active when each clip is applied. A query
walks the current and saved context stack from inner to outer, composes the full
managed transform, maps each finite clip into that cumulative coordinate space,
and intersects the independently owned results. Infinite contexts add no finite
constraint. Restore disposes only the snapshots owned by the graphics stack;
clips already returned to a caller remain valid.

This path uses the existing typed `Matrix`, `Region`, save/restore, and retained
clip model. It introduces no HDC, native context pointer, runtime reflection, or
platform-shaped compatibility object.

## Quality and performance gate

Six focused cases cover default/infinite state, clip-before-transform versus
transform-before-clip coordinates, saved-context transform and clip
accumulation, legacy object-array shape, returned-clip ownership across restore,
disposed state, and exactly zero managed allocation across 10,000 warmed
offset-only reads. Strict ApiCompat removes only the three `GetContextInfo`
missing-member suppressions, reducing measured debt from 17 to 14 missing
members and from 32 to 29 total diagnostics with no new incompatibilities or
stale suppressions.
