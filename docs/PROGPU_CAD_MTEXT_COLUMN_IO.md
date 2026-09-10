# MTEXT column input preservation

## Confirmed defects

The ACadSharp DWG reader consumed the R2018 column count without assigning it
to the object model. Valid static, dynamic/manual-height, and dynamic/auto-height
documents therefore reached ProGPU's column layout with a zero capacity and
were rejected. Preserve the existing decoded count; do not manufacture a count
in the renderer or reinterpret the authored column mode.

`MText.TextColumnData.Clone` also shared its mutable height list. A copied
MTEXT could consequently change the original's layout. The clone now owns its
height array while retaining the scalar column settings.

These changes are in the reviewed ACadSharp dependency, not imported
third-party implementation code in ProGPU. Count publication is O(1), without
additional reads or allocations. Column cloning uses O(C) owned memory and
time for C heights, only at the explicit object-copy boundary.

## Sources and applicability

- The [Open Design DWG specification, section 20.4.46, pages 154–155](https://www.opendesign.com/files/guestdownloads/OpenDesign_Specification_for_.dwg_files.pdf)
  establishes the R2018 count, column width, gutter, flags, and conditional
  dynamic-height sequence. Adopt the persisted field contract; retain the
  dependency's existing bitstream decoder.
- Autodesk's [MTEXT DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-5E5DB93B-F8D3-4433-ADF7-E92E250D2BAB.htm)
  distinguishes text geometry from column count, auto-height, and flow state.
- The ezdxf maintainer's [observed MTEXT file records](https://ezdxf.readthedocs.io/en/stable/dxfinternals/entities/mtext.html)
  describe the newer embedded DXF object and show a zero last-column height.
  Its interpretation is explicitly tentative; it is not sufficient to define
  ProGPU's layout semantics for negative DWG heights.

No shaping, layout algorithm, shader, cache, native ABI, GPU resource identity,
or submission path changes. The existing
[MTEXT layout architecture](PROGPU_CAD_MTEXT_PARAGRAPH_RESEARCH.md) remains intact.
Managed and native renderers consume the same corrected immutable positioned
glyphs. The integration tests compare positions before/after file round trips,
then verify managed retained glyph-run recording and native picture compilation.
There is no independent C++ CAD file reader to update.

## Representative-file follow-up (resolved 2026-09-08)

The audited DXF sample has one dynamic/manual column with height zero for
MTEXT `3EC`, `3F5`, `3F6`, and `779`. The paired DWG contains a negative final
height (for example approximately -2.9442928603910867 for `3EC`). The DXF
embedded rectangle height is also zero, so retaining that redundant field
alone would not restore the text. Do not replace those values with arbitrary
positive heights or drop column boundaries. The count fix alone did not resolve
these entities; the separate final-column placement correction below now restores
`3EC`, `3F6`, and `779`. Vertical TrueType `3F5` remains explicitly unsupported.

The focused tests use explicit valid heights and verify static, manual-dynamic,
and automatic-dynamic round trips without changing their layout contracts.
These are IO/positioned-command regressions, not independent AutoCAD pixel
conformance or a rendering performance claim.

Validation: four dependency tests and seven ProGPU integration tests pass in
Release. The complete 1,552-test CAD suite passes with CI's existing
`DOTNET_TieredCompilation=0` setting. An initial default-runtime suite run
reported 5,712 bytes in the unrelated rectangle preview allocation check; that
unchanged check then passed in isolation with zero bytes. No allocation threshold
or runtime setting was changed by this patch, and the observation is not proof
of its cause.

## Dynamic/manual final-column placement

The maintainer's [public dynamic/manual column API contract](https://ezdxf.readthedocs.io/en/stable/layouts/layouts.html#ezdxf.layouts.BaseLayout.add_mtext_dynamic_manual_height_columns)
is more definitive than the earlier tentative internal-file notes: only earlier
columns use persisted height limits; the final column receives remaining content
and its height value is ignored. Adopt that behavioral contract, not the external
implementation. Extending the ignored finite value rule to negative DWG sentinels
is an inference supported by the paired fixtures, not licensed AutoCAD pixel
certification. Non-finite metadata is still rejected.

`CadSnapshotCompiler.MText.cs` now owns one original shared height-limit resolver,
also used by `CadSnapshotCompiler.ShxMText.cs`. Only dynamic/manual final columns
receive an internal unlimited placement bound. All published bounds come from
measured lines. Static and earlier manual-column limits remain finite and
positive; overflow on conversion to float is rejected. Width, gutter, explicit
column breaks, reversed flow, automatic layout, and raw object-model data remain
unchanged. No arbitrary replacement height is persisted or exported.

The [cross-engine paragraph research](PROGPU_CAD_MTEXT_PARAGRAPH_RESEARCH.md)
continues to apply: SkParagraph, DirectWrite/Win2D, HarfBuzz, WebRender, and
Vello/Parley separate reusable CPU text layout from retained GPU replay. This
correction adapts the CAD column contract only after existing shaping. Column
placement remains O(L + C) for L lines and C columns, with O(C) limit storage.
Startup, discovery/fallback, variable fonts, worker preparation, culling, cache
keys/eviction, demand upload, batching, DPI/subpixel coverage, and device-loss
invalidation are unchanged. No renderer performance improvement is claimed.

Managed/native applicability: both TrueType and SHX snapshot paths use the same
resolver; both backends consume their positioned retained commands. There is no
independent C++ CAD column layout to patch, and no shader or wire-contract change.
Six real-file cases compare native serialized scene and print commands against
equivalent ample-height inputs, preserving glyph positions and source ownership.
Synthetic tests cover two-column flow in both directions for TrueType and SHX,
zero/negative/tiny/ample final heights, malformed active limits, and DXF/DWG
save/reopen preservation. Six headless regressions compare exact pixel buffers
at two zoom levels and independently require visible text in both columns.

The representative audit now has zero invalid entities in both files (previously
three), with unsupported entities unchanged at 12. DXF records 560 entities /
589 commands; DWG records 561 / 591. Unsupported line styles (4), unresolved
images (1), and deferred modeler surfaces (3) remain reported. This does not
certify complete file fidelity or eliminate the separate CI allocation blocker.

Final local Release validation: 1,621 CAD tests (including 35 new cases), 3,862
core renderer tests, and 280 headless tests (including six new pixel cases) pass.
The CAD suite uses CI's unchanged `DOTNET_TieredCompilation=0` setting. A
separate diagnostic called the unchanged mesh-allocation test 100 times while
another thread forced generation-0 collections; all passed with zero bytes on
macOS arm64 / .NET 10.0.5. That experiment does not reproduce or explain the
Linux CI failure and is not evidence that CI is fixed.

The final `0081177f` browser Release AOT publish and local SwiftShader smoke
also pass: 81 frames, 103 dispatches, a 2880x1800 physical framebuffer, visible
columned text, pan/zoom/resize, and 16 entity types through save/reopen/resave.
The presentation preflight confirms 4,096/4,096 direct and 16,384/16,384 page
pixels for its independent red-clear probe. These counters identify the run;
they are not performance measurements. PR CI qualification remains pending.

## Separate Linux allocation investigation

An isolated self-contained .NET 10.0.5 ARM64 diagnostic invokes the unchanged
`CadMesh3DSelectionTests.WarmModernMeshSubobjectQueryAllocatesNothing` directly
100 times, with `DOTNET_TieredCompilation=0`, outside the xUnit runner. On Ubuntu
24.04 in the existing Parallels VM, the ordinary run fails 8/100 times, reporting
5,680 or exactly 7,312 bytes (the CI value). With a separate thread forcing
generation-0 collections every millisecond it fails 3/100 times, reporting
5,400–7,296 bytes. The macOS forced-collection run passes 100/100.

Two independent controls seed a small object outside measurement, then measure
only `Thread.Sleep(20)` or `Thread.SpinWait(1_000_000)` under the same forced
collection loop. Both Linux controls pass 100/100 with zero measured bytes.
Consequently neither a generic garbage-collection accounting explanation nor
an xUnit-runner-only explanation is established. The reproducer narrows further
investigation to the query workload and runtime interaction; it does not yet
identify an allocating source line. Keep the zero-byte assertion and runtime
settings unchanged. Diagnostic programs and logs remain ignored under
`artifacts/progpu-cad/`; the VM uses staged self-contained binaries and no
configuration or system-package changes.

Follow-up: [allocation measurement isolation](PROGPU_CAD_ALLOCATION_VALIDATION.md)
records the expanded controls and the revised test's 1,000-run Linux validation.
This is not a production mesh algorithm change or proof of the runtime cause.
