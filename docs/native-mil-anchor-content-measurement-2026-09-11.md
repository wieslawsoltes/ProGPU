# Native anchored content measurement

Acceptance dependency: LibreWPF Figure auto width needs the measured child extent
before its second native width-resolution/formatting pass. The ordinary document
result is at least the requested width, so it cannot be used as fit-content input.

`progpu_native_document_arrange_with_content_measurement` adds a required disjoint
double output to the existing complete row/positioned-paragraph arrangement. It
shares width resolution, validation, native placement and atomic publication.
The separate content right extent includes source insets/margins, line widths,
measured objects, explicit paragraph extents and fixed row tracks/spacing; it
excludes ordinary allocated block widths. It is not ink bounds or point-hit geometry.
Callers supplying explicit paragraph extents retain responsibility for their
actual width contract; a constrained extent is not implicitly shrinkable.

Provenance is ProGPU 9449ef80's original document `place` pass. This extends that
same O(B + L + C + R + E) changed-layout traversal with constant scalar maxima,
no new buffers, no extra native crossings and no managed layout scan. Existing
calls do not request content measurement. Ordered hierarchy/extent accumulation
is dependency-bound; existing intrinsic validation stays shared. Both native
providers compile the same source. No foreign layout implementation was imported.

Both providers built and passed export allowlists. Native document CTest passed
(1/1, 0.38 seconds): constrained width 100 versus measured width 43 with real
insets/margins; unchanged outputs after invalid line coverage; and fixed row
content width 104 including its spacing. The first new table expectation omitted
spacing and failed; corrected it from the existing track-width contract rather
than changing placement. Existing document tests remain in the same executable.

Managed/neutral transport and actual source anchor two-pass consumption are still
required. This native measurement is not automatic Figure/Floater admission,
unchanged application closure or package/platform/CI qualification.
