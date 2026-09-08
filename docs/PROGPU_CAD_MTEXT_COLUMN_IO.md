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

## Representative-file follow-up

The audited DXF sample has one dynamic/manual column with height zero for
MTEXT `3EC`, `3F5`, `3F6`, and `779`. The paired DWG contains a negative final
height (for example approximately -2.9442928603910867 for `3EC`). The DXF
embedded rectangle height is also zero, so retaining that redundant field
alone would not restore the text. Do not replace those values with arbitrary
positive heights, drop column boundaries, or claim this count fix resolves
those sample entities. Their final-column semantics remain a rendering blocker.

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
