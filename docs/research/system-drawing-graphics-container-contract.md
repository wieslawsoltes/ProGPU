# System.Drawing Graphics-Container Contract

Date: 2026-08-26

## Scope and sources

This slice implements the official [`GraphicsContainer`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicscontainer?view=windowsdesktop-10.0) identity and all four [`Graphics.BeginContainer`](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.begincontainer?view=windowsdesktop-10.0) / [`Graphics.EndContainer`](https://learn.microsoft.com/dotnet/api/system.drawing.graphics.endcontainer?view=windowsdesktop-10.0) members from the pinned .NET 10.0.11 contract.

The observable contract follows the official nested-container model: entering a container retains the parent graphics state, exposes a fresh public transform, page, clip, and rendering-quality state, and composes subsequent drawing with the parent's effective transform. Ending a container restores its captured state and invalidates scopes created above it. The rectangle overload additionally maps a source rectangle, expressed in the declared supported unit, into a destination rectangle. `World`, `Display`, invalid units, non-finite rectangles, and zero source extents fail at the public boundary.

The public signatures and state defaults are checked against the reference assembly, Microsoft documentation, and the repository's canonical WinForms compatibility tests. The retained implementation is original ProGPU code and does not call or copy GDI+ internals.

## Typed implementation

The immutable host/device transform remains separate from a compact container transform. Retained drawing now composes:

`world transform × page transform × container transform × typed host transform`.

An unscaled container captures the parent's effective world/page/container transform. A rectangle container prepends a source-to-destination scale/translation before that parent transform. Public `Transform` resets to identity inside the container, while `TransformPoints` includes the hidden container transform for truthful world/page/device conversions.

Parent clips remain as enclosing `PushGeometryClip` scopes in the typed `DrawingContext`; the new container's public `Clip` is infinite and any child clip is nested inside the retained parent clip. Ending, restoring across, or disposing active containers emits matching pops before reconstructing the restored logical state. Source-copy blend scopes are reset on entry and restored through the same typed state path.

`Save` and containers share one ordered context stack but retain their distinct official token types. Tokens are owned by one `Graphics`, are single use, and cannot be substituted across save/container operations. No HDC, native GDI+ state object, runtime reflection, private-field scan, or fake compatibility object is introduced.

## Gates and evidence

Twelve focused tests cover:

- exact public type shape;
- official container defaults and complete parent-state restoration;
- nested parent/local transform composition;
- rectangle mapping through an explicit LibreWinForms-style host transform;
- inherited clip pixels while the public clip is infinite;
- null, cross-instance, single-use, and nested-token invalidation;
- `Save` restoration across a live container;
- invalid unit validation;
- balanced geometry-clip commands on restore and disposal; and
- a 256-byte-per-round-trip upper allocation bound across 1,024 warmed container transitions.

The complete drawing suite passes 200/200. ApiCompat removes one missing-type and four missing-member suppressions, reducing measured debt from 47 missing types, 288 missing members, 47 other diagnostics, and 382 total to 46 missing types, 284 missing members, 47 other diagnostics, and 377 total. The gate reports no breaking changes or stale suppressions. LibreWinForms downstream validation rebuilds the ProGPU adapter with 0 warnings and 0 errors, passes 10/10 backend tests, rebuilds canonical `System.Windows.Forms` with 613 known compatibility warnings and 0 errors, and passes 24/24 lifecycle tests.
