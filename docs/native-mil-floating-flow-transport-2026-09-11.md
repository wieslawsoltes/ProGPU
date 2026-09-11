# Batched floating paragraph transport

## Acceptance dependency

The unchanged LibreWPF document application needs original Figure/Floater source
positions to drive the native floating-row fitter. This batch connects that fitter
to the existing styled/inline C shaping pipeline and managed context spans. It
does not yet connect the retained paragraph snapshot, neutral provider or WPF
source admission; those remain the next integration work.

The new requirements/layout entry points take measured outer float sizes and
ordered scalar-array boundaries, including the terminal boundary. Equal indices
preserve sibling order. After shaping, one forward scan maps those events to
original logical glyph-cluster boundaries. A boundary inside a shaped cluster is
rejected, never rounded to the next glyph or made into an invisible font glyph.
Source UTF-16/hidden-edge mapping and any required shaping partition at an anchor
must be implemented by the retained source contract before its admission.

Existing style metrics, physical face annotations, inline objects, tab options,
bidi, wrapping, initial exclusions and native fragment frames stay in the shared
pipeline. Both provider libraries compile the same implementation. The C++ fitter
is unchanged by this transport; no separate managed or C-layer line composer is
introduced.

## Wire and ownership contract

The generated records are:

| Record | Bytes | Purpose |
| --- | ---: | --- |
| Floating item | 16 | Scalar boundary, measured width/height, checked alignment |
| Floating options | 32 | Retry budget, paragraph origin, empty-row metrics, reserved fields |
| Floating placement | 24 | Owning source row and actual outer box |
| Floating result | 32 | Consumed floats/rows, next glyph, attempts and combined extents |

Parent text extents remain in the ordinary paragraph result. Float-inclusive
width/height are separate; a float may extend below the final parent row. Empty
input with events requests zero glyph slots but one line/frame slot and uses
explicit source empty-row metrics. Ordinary empty input retains its no-row API.
The native empty-row fitter, not source-local geometry, handles existing obstacles.

All arrays are synchronously borrowed. Writable buffers must not overlap each
other or inputs; checked range sizes also cover 32-bit address arithmetic. Invalid
sizes, alignments/reserved fields, metrics, source boundaries, output capacity,
scratch capacity and native retry failures do not publish a successful prefix.
Wire alignment is checked before narrowing the uint32 enum. Source indices outside
the signed native cluster range are rejected before conversion.

Requirements include one scratch arena for shaping, native event mapping,
collision/interval storage, fragments and float placements. There is no per-anchor
or per-row P/Invoke. NativeTextShapingContext pins typed spans under its existing
owner lease and checks metric/style lengths before pinning. Generated C# records
come from the C header; they are not hand-maintained ABI duplicates.

The ordered event scan is O(G+A) and uses no new allocations. Shared coordinate
validation retains intrinsic lanes; shape/row/interval dependencies retain their
existing native policies. No performance or fastest-path qualification is claimed.
Provenance is the same-repository styled/inline/excluded pipeline and floating
fitter at 529b031b; no foreign implementation was introduced.

## Evidence

- Both full native providers rebuild; both exported-symbol allowlists pass.
- Native C interop CTest passes. It covers real styled text plus an inline object,
  source origin, same-row siblings, scalar indices distinct from input indices,
  terminal events, explicit empty rows, short/overlapping buffers, enum/reserved
  rejection and failure without public partial output. Record sizes are asserted.
- The same compiled C interop test is separately linked to libprogpu_native_dawn
  and passes against that provider. This is CPU text transport execution, not a
  Dawn GPU presentation or package qualification.
- The generated text-flow C# contract verifies against its header. Release managed
  consumer build passes with zero warnings/errors. Its floating span/ABI and empty
  source-row checks pass together with the existing MIL-only consumer checks.
  This run uses project references and local native artifacts, not final packages.
- System.Drawing allocation CI passes on the preceding 529b031b head, job
  103109299602 in run 34549521363. The earlier 9beaaf2b failure is retained in its
  report; no allocation threshold was changed. The next head still needs CI.

Next: retain placements and combined extents in NativeTextParagraphSnapshot;
map original source UTF-16 anchor boundaries without splitting clusters; expose
the optional typed provider; connect original WPF child ownership/drawing/input.
Hard-segment affinity and source empty paragraphs remain explicit requirements.
Normal source rejection, final application/package/Windows/macOS/Linux gates and
broader deferred DirectX/Direct2D/Win2D scope remain unchanged.
