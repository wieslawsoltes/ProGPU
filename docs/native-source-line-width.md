# Native source line widths

The explicit `SourceIdealUnits` paragraph lane now retains an integer line
measure in 1/300-DIP units, separately from its original per-glyph draw advances.
The native fitter, intrinsic measurement, shared measured writer and flat-resource
import validator use the same typed width policy. Other advance policies keep
their original ordered double arithmetic. A private mixed-policy or mixed-DPI
style inventory does not select the integer lane.

The defect is observable with six authored glyph advances of 8, 8, 8, 8, 8 and
10 physical pixels at DPI 1.5. Adding the projected doubles reports
`33.33333333333333`; projecting the retained 10000-unit line measure once reports
`33.333333333333336`. This is a width and fitting correction in the producer,
not a reader adjustment or a tolerance in a comparator.

## Original observations

The original Microsoft observer at commit
`7688c0051b509474c9966fbc3a9291c8971682ab` passed on Windows x64 and ARM64 in
[run 38625837266](https://github.com/wieslawsoltes/ProGPU/actions/runs/38625837266).
Its original 960 corpus cases are field-for-field identical to the preceding
numeric observer. Four additional public `FormatLine` requests per architecture
observe Inter, `Hello`, Display, em 13 and DPI 1.5:

| Requested width | Original integer limit | First line length | Original first width |
| --- | ---: | ---: | --- |
| `24.666666666666664` | 7399 | 3 | `21.333333333333332` |
| `24.666666666666668` | 7400 | 4 | `24.666666666666668` |
| `24.66666666666667` | 7400 | 4 | `24.666666666666668` |
| `25` | 7500 | 4 | `24.666666666666668` |

The original four-character line retains width 7400 beside draw advances
10, 8, 10/3 and 10/3. Read-only diagnostic numeric fields explain the public
outputs; product code contains no reflection or original formatter dependency.
The separate requests retain the original sixty-second process limit.
Artifact SHA-256 values are
`ae3b4b14c0b3e5cd594f0f58a816eb9bb345c944bc6a03c24630697507dda38a` (x64) and
`7ad04dafdd2ae3369d4ebc56ccab242127d09c6152fae09197bba6111cd75690` (ARM64).

Each selected native logical advance contributes its nearest-even integer units
before fitting. The original maximum width is independently floored to units;
zero retains the existing unbounded contract. Width publication converts the
integer extent through the original DPI once, preserving the positive minimum.
Every numeric failure leaves the accumulator unchanged. Integer overflow rejects
the candidate rather than clamping it or publishing a partial line.

## Ownership and implementation applicability

The fitting generation retains the complete policy, and validation rejects a
different policy or even a one-ULP change to its published width. Initial layout
and reflow pass the same policy to the existing measured writer. The importer
derives it from the transported original styles, regenerates the logical
metrics, and verifies the complete fitted partitions before publication. No wire
record, enum value, source option default or raw glyph advance changes.

Managed `NativeHintedSourceParagraph` calls and snapshots consume this native
writer; they have no separate source-line accumulator to patch. Both native
render providers share the producer/import validator. Ordinary managed text and
raw native paragraphs do not opt into this source policy. No renderer-specific
correction, GPU readback, font lookup or extra source callback is introduced.

Each appended width is O(1), allocation-free and checked before publication.
Candidate traversal, budgets and retained storage remain unchanged. The prefix
measure depends on the previous extent and its signed overflow admission;
independent glyph projections retain their existing SIMD path. This correctness
change makes no performance improvement claim.

## Validation and remaining admission

The original six-glyph regression fails before the change. Native ASan/UBSan
paragraph, source policy, transport and import suites pass after it. Controls
include exact and immediately lower reflow widths, original owner/advance
retention, intrinsic widths, imported six-glyph resources, negative advances,
small positive extents, policy identity and atomic invalid/overflow rejection.
The existing mark import control keeps its original requested double width:
its 6799-unit constraint now correctly splits the 6800-unit tail into an extra
line. Other policy controls keep their previous partitions.

Whole successful producer CI, exact package execution and comparison with the
complete original corpus remain required. This change does not enable Display
or qualify glyph pixels, trailing-whitespace/bidi behavior, source interaction
or downstream package pins. LibreWPF #184 remains open until its full contract
qualifies.

## Clean-room references

The owned implementation extends ProGPU's source policy, fitting generation and
measured writer. No foreign implementation text or helper structure was copied.
The independent Windows observations above establish the numeric boundary.
[DirectWrite placement](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextanalyzer-getgdicompatibleglyphplacements)
and [TextLine width](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformatting.textline.widthincludingtrailingwhitespace?view=windowsdesktop-10.0)
define the separate placement and line surfaces. Architecture research considered
[Win2D layouts](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[Skia shaping](https://skia.org/docs/dev/design/text_shaper/),
[WebRender font instances](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html),
[Parley layouts](https://docs.rs/parley/latest/parley/layout/index.html),
[Vello's rendering architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md)
and [HarfBuzz buffers](https://harfbuzz.github.io/harfbuzz-hb-buffer.html).
Those separations support keeping the source measure in the owned formatter
generation; none supplies a substitute WPF numeric oracle or rendering policy.
