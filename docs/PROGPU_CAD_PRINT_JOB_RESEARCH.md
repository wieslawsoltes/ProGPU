# ProGPU.CAD retained print-job research

## Scope

This checkpoint defines the backend-neutral document boundary between existing
physical `CadPrintPlan` pages and later printer, PDF, SVG, and raster adapters.
It covers caller-controlled sheet order, repeated pages, reverse publishing,
collated and uncollated copies, mixed media, bounded ownership, cancellation,
and managed/native retained-page parity. It does not add a spooler, document
encoder, DSD reader/writer, page-range UI, duplex/N-up policy, or an implicit
page-setup override.

Only original ProGPU sources are implementation provenance:
`CadPrintPlan.CreatePagePicture`, `GpuPicture.Clone`, and the existing managed
and native picture compilers. The sources below define observable behavior and
architecture only; no third-party source text or structure was copied.

## Authoritative output contracts

- Autodesk's [Publish dialog](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT/files/GUID-7BCCDFDC-562F-43A4-83F2-CEAE10C0DA64.htm)
  defines an explicitly ordered sheet list, copied sheet entries, per-sheet page
  setup selection, reverse-order output, physical copy count, and single- or
  multi-sheet file output. It also says file output ignores the physical-copy
  count and produces one plot file. ProGPU therefore keeps ordered/repeated
  retained pages separate from any later adapter decision about whether copies
  belong in a device job or are invalid for a particular file format.
- Autodesk documents that [copied publish sheets](https://help.autodesk.com/cloudhelp/2019/ENU/AutoCAD-Core/files/GUID-311039EA-7660-4625-935E-CF0706B3C91A.htm)
  can use different page setups. ProGPU consequently treats every caller entry
  as a distinct source page even when two entries originated from the same CAD
  layout.
- `PlotEngine.BeginDocument` exposes an explicit
  [copy count](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_PlottingServices_PlotEngine_BeginDocument_PlotInfo_string_object_int_modoptIsLong__MarshalAsUnmanagedType_U1__bool_string.html),
  with one copy required for plot-to-file, while
  [`BeginPage`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_PlottingServices_PlotEngine_BeginPage_PlotPageInfo_PlotInfo__MarshalAsUnmanagedType_U1__bool_object.html)
  supplies a validated per-page description and explicit final-page marker.
  ProGPU adopts explicit source-page metadata and deterministic final page
  count, without importing Autodesk database/transaction/runtime types.
- Skia's [`SkDocument`](https://api.skia.org/classSkDocument.html) owns a strict
  begin-page/end-page sequence and supports different dimensions per page. Its
  [PDF canvas documentation](https://skia.org/docs/user/api/skcanvas_creation/)
  distinguishes a multi-page document backend from a single raster surface.
- Direct2D
  [`ID2D1PrintControl::AddPage`](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/nf-d2d1_1-id2d1printcontrol-addpage)
  consumes one retained command list plus that page's physical size and print
  ticket. Win2D's
  [`CanvasPrintDocument.Print`](https://microsoft.github.io/Win2D/WinUI3/html/E_Microsoft_Graphics_Canvas_Printing_CanvasPrintDocument_Print.htm)
  similarly asks the app to draw each declared page into its own drawing
  session, while Windows exposes copies and collation as independent standard
  print options.

## Cross-engine applicability audit

Skia, Direct2D/DirectWrite, and Win2D all keep document sequencing outside the
retained page drawing commands. WebRender has retained display lists and spatial
trees but no physical multi-page document contract; only its separation of
retained content from presentation applies. Vello renders an immutable scene to
one selected texture target, so a document owner must sequence page scenes
outside the renderer. ProGPU follows that same boundary: a job owns page
pictures, while an adapter selects the output target and submits one page at a
time.

SkParagraph, DirectWrite, Parley, and HarfBuzz remain applicable only to the
already-compiled text inside each page. Copies, collation, and reverse ordering
must not reshape text, rebuild glyph arrays, change fallback, or alter DPI and
subpixel policy. The job therefore clones the existing immutable page picture
and its resource leases instead of traversing CAD or text state.

No shader, GPU algorithm, managed/native ABI, cache generation, atlas, upload,
or device-loss contract changes. Native applicability is satisfied by returning
the same `GpuPicture` accepted by `GpuPictureNativeSceneCompiler`; matched tests
compile every ordered DXF/DWG-derived output page through that path.

## Adopted ProGPU contract

- The caller supplies a bounded ordered span of named `CadPrintPlan` sources.
  Names and retained setup names are copied under per-string and total UTF-16
  budgets; mutable CAD objects are never retained.
- Source plans may come from different documents, generations, page setups,
  DPIs, rotations, and media sizes. Every source plan is already an immutable,
  generation-consistent physical page. The job preserves that metadata rather
  than falsely assigning one document generation to a cross-drawing publish
  set.
- Compilation clones each physical source page exactly once. All later copies
  share immutable command storage and acquire independent resource leases.
  Disposal of the source plans, the job, and returned pictures is independent.
- Collated order is `A,B,C,A,B,C`; uncollated order is `A,A,B,B,C,C`.
  Reverse order first reverses the source sequence, then applies the selected
  copy ordering. Source and copy indices resolve with O(1) arithmetic and no
  output-page mapping array.
- Defaults cap source pages at 4,096, resolved output pages at 65,536, each name
  at 4,096 UTF-16 code units, and all owned names at 1,048,576 code units.
  Validation is complete before picture acquisition; cancellation or acquisition
  failure disposes every clone already acquired and leaves caller plans owned by
  the caller.
- Compilation is O(P + S) time/storage for P source pages and S owned string
  code units. Output index resolution is O(1). Page creation is O(R) only for R
  retained resource leases and shares the immutable command buffers. A 10,000-
  copy regression resolves 20,000 output pages from two retained source-page
  command stores.

Rejected were eager page-picture duplication per copy, an O(output pages)
sequence array, implicit sorting by layout name/tab order, forced same-generation
jobs that would prevent cross-drawing publication, inferred application of named
page setups, and output-backend behavior inside the retained job owner.

## Validation evidence

Focused tests cover collated and uncollated order, reverse output, mixed media
and DPI, 10,000 copies without duplicated command storage, ownership across
source-plan/job/page disposal, invalid options and budgets, cancellation, DXF
and DWG round trips from different source generations, and native compilation
of every resolved page. Full CAD and core renderer suite results are recorded in
the corresponding PR checkpoint.
