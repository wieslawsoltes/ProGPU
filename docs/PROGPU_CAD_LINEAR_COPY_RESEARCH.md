# ProGPU.CAD Linear COPY Array Research

Date: 2026-08-30

This record defines the clean-room behavior, ownership, complexity, and parity
contract for bounded selection-set linear copies in ProGPU.CAD. It describes
the `CadLinearCopyModelSpaceEntitiesCommand` checkpoint; it is not a claim that
the independent associative `ARRAY` command family, clipboard exchange, UCS
interaction, or arbitrary-camera point input is complete.

## Authoritative behavior source

Autodesk's public
[`COPY` command contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-1CF9287F-06E8-4D03-8377-2E130862FE02.htm)
defines the observable behavior used here:

- COPY operates on a completed object selection and a displacement established
  by two points or relative coordinates.
- Multiple mode repeats COPY for the duration of an interaction.
- Array produces a linear array whose item count includes the original
  selection set.
- With the ordinary second-point option, the first copy is placed at the
  supplied displacement and later copies use that same incremental spacing.
- With Fit, the supplied displacement locates the final copy and intermediate
  items are distributed between the original and final selection sets.

No Autodesk or other third-party source implementation was inspected, copied,
ported, translated, or used as an implementation template.

## ProGPU-owned source provenance

This implementation directly extends these original in-repository ProGPU
contracts, which are approved sources under the repository clean-room policy:

- `src/ProGPU.CAD/CadEditing.cs`, specifically
  `CadDuplicateModelSpaceEntitiesCommand`, supplies the established semantic
  root resolution, detached ACadSharp clone ownership, structurally complete
  batch publication, retained identity on Redo, and exact batch removal on
  Undo.
- `src/ProGPU.CAD.Sample/CadSampleCanvas.cs`, specifically
  `DuplicateSelection`, supplies the source-selection-preserving shared-shell
  interaction and one transactional retained-scene rebuild per edit.
- `src/ProGPU.CAD/CadEditCommand` and `CadDocumentHistory` remain the authority
  for one generation per Apply, Undo, or Redo and for history divergence.

The new command is a ProGPU-to-ProGPU extension of those ownership and history
contracts. It does not derive its type names, control flow, storage layout, or
helper structure from a third-party implementation.

## Adopted contract

`ItemCount` includes the original selection. An item count of two therefore
creates one duplicate placement and remains behaviorally equivalent to the
existing single-displacement selection copy.

For source displacement `D`, item count `C`, and zero-based duplicate placement
`p` in `[0, C - 2]`:

- Incremental mode uses `T(p) = D * (p + 1)`.
- Fit mode uses `T(p) = D * (p + 1) / (C - 1)` and assigns the final placement
  directly to `D` so the public final-point contract is exact.

Each placement contains the complete deduplicated source selection in caller
order. Retained duplicate storage and current handles are placement-major and
source-order within each placement. This gives deterministic draw-order and
save-order behavior without an intermediate displacement array.

Zero displacement is legal because it is finite and remains a meaningful CAD
operation even though the resulting copies overlap. Non-finite input and an
incremental final displacement that would overflow are rejected before source
resolution or cloning.

## Bounds, ownership, and failure semantics

The default command bounds are 65,536 unique source semantic roots and 65,536
duplicate entity graphs. The checked product `S * (C - 1)` is validated during
construction. An untrusted source enumerable cannot grow retained handle state
past the source bound.

Apply first resolves every semantic source root, including the existing
locked-layer authorization, before cloning. It then creates and transforms all
detached graphs before one ACadSharp `AddRange`. A missing source, unsupported
modeler transform, invalid detached clone, count violation, or numerical
failure therefore publishes no partial document mutation or history
generation. Undo preflights and removes the same retained graph batch. Redo
reattaches those exact graphs rather than re-reading or recloning sources that
may since have changed.

For `S` unique source roots and `C` total items:

- construction is `O(S)` time and storage;
- first Apply is `O(S(C - 1))` clone/transform time and retained storage;
- Undo and Redo are `O(S(C - 1))` collection work;
- `GetPlacementDisplacement` is allocation-free `O(1)`;
- no `O(C)` displacement array is retained.

The shared desktop/browser shell uses the existing finite positive WCS step as
the X/Y displacement magnitude, exposes item count and Step/Fit mode, preserves
the original semantic selection, and triggers one snapshot/picture replacement
per committed command. Its explicit Multiple point mode retains one exact WCS
base and accepts up to 65,536 independently committed second-point placements.
Enter or Escape ends the prompt after any accepted copies; Escape before the
first placement cancels without an edit. Each placement is its own exact
Undo/Redo history action, so finishing the prompt never creates a synthetic
aggregate mutation or rewrites prior history.

## Managed/native applicability audit

The change modifies no shader, canonical shader resource, C ABI, generated C#
wire declaration, native C++ module, cache key, atlas, upload path, device-loss
rule, text shaping, or raster-quality policy. Both renderers consume the same
generation-tagged immutable snapshot rebuilt from the ACadSharp document after
the atomic edit. The matched regression compiles the resulting retained
`GpuPicture` through `GpuPictureNativeSceneCompiler`; managed command order and
native source-command count remain identical.

## Adopted, adapted, and rejected concepts

- Adopted: item count includes the original; incremental second-point spacing;
  Fit places the final item at the requested vector; the complete selection is
  one copy unit.
- Adapted at this checkpoint: the array consumes a typed WCS vector from the
  finite-step controls. The later shared two-point interaction now supplies an
  exact base-to-second WCS vector for single COPY while this bounded array
  remains one explicit history action.
- Rejected for this checkpoint: creating an associative ARRAY object, silently
  exceeding the bounded edit budget, cloning sources again on Redo, retaining
  one displacement object per placement, rewriting source selection to newly
  created handles, clipboard transport, and tessellated or renderer-specific
  copies.
- Implemented subsequently: shared WCS-XY base/second-point acquisition for
  single MOVE/COPY and bounded repeated Multiple COPY, documented in
  [`PROGPU_CAD_POINT_TRANSFORM_RESEARCH.md`](PROGPU_CAD_POINT_TRANSFORM_RESEARCH.md).
  UCS and arbitrary-camera interaction, clipboard exchange, and rectangular,
  polar, or path associative ARRAY entities remain.

## Verification evidence

`CadLinearCopyTests` and the shared-shell selection regressions cover:

- exact incremental and Fit placement mathematics, including exact final Fit
  displacement;
- deterministic placement-major/source-order duplicate identity;
- source deduplication, finite/enum/item/product/source bounds, and missing
  source preflight with no generation or partial mutation;
- exact retained clone identity and handle clearing/restoration through
  Apply/Undo/Redo;
- a 10,000-copy bounded batch;
- shared desktop/browser controls, invalid item rejection, source-selection
  preservation, and one snapshot rebuild per history action;
- exact retained-base Multiple prompt transitions, caller placement bounds,
  Enter/Escape termination, and independent placement Undo/Redo;
- matched managed/native retained-picture compilation; and
- DXF and DWG round trips of Fit results.
