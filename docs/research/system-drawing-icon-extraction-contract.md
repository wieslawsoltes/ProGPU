# System.Drawing managed icon-extraction contract

Date: 2026-08-27

## Scope

This checkpoint restores the three official `Icon.ExtractAssociatedIcon` and
`Icon.ExtractIcon` members without routing portable drawing through the Windows
shell or manufacturing an `HICON`. The supported portable sources are ICO
containers and PE executable/library resources. `ExtractAssociatedIcon` also
uses the existing managed bitmap decoder for ordinary image files.

The public contract follows the .NET 10 reference assembly and official API
documentation:

- positive `ExtractIcon` identifiers select a zero-based icon-group index;
- negative identifiers select a numeric PE group-icon resource identifier;
- the integer overload selects the closest available frame and resamples it to
  the requested square size;
- the Boolean overload uses portable small/large defaults of 16 and 32 pixels;
- `ExtractIcon` returns `null` for an absent icon or invalid existing source,
  while an invalid `ExtractAssociatedIcon` source is an argument failure;
- null, empty, missing-path, and invalid-size failures retain their documented
  argument and I/O boundaries; and
- the returned icon owns decoded pixels and does not retain the source file.

The official contracts were taken from the .NET 10 reference assembly and
Microsoft Learn pages for
[`Icon.ExtractIcon`](https://learn.microsoft.com/dotnet/api/system.drawing.icon.extracticon?view=windowsdesktop-10.0)
and
[`Icon.ExtractAssociatedIcon`](https://learn.microsoft.com/dotnet/api/system.drawing.icon.extractassociatedicon?view=windowsdesktop-10.0).
Canonical runtime tests were used only as observable-behavior evidence. The
implementation is original ProGPU code.

## Typed managed pipeline

`PortableIconExtractor` performs bounded little-endian parsing over an owned
file snapshot. ICO directory entry offsets and sizes are checked before any
slice. PE parsing validates the DOS and PE signatures, optional-header and
section extents, resource RVA mapping, directory depth, entry counts, and each
data extent. It walks only numeric `RT_GROUP_ICON` and `RT_ICON` resources,
selects one group/frame, and assembles a single-image ICO in owned memory. The
existing ProGPU bitmap decoder then decodes and, when required, resamples that
image.

No unmanaged address is accepted or retained. The parser does not load an
assembly, execute a PE image, call the shell, enumerate processes, inspect
private state, or use reflection. File associations and OS-generated shell
thumbnails remain a separate local-OS service concern; this managed checkpoint
does not claim those shell-specific behaviors.

## Quality gates

Focused tests create ICO and PE fixtures entirely in managed memory. They cover
multi-frame size selection, closest-frame resampling, index and negative
resource-ID lookup, associated-icon selection, owned lifetime after source
deletion, null/empty/missing/size failures, absent IDs, invalid existing files,
and out-of-range resource RVAs. The complete Debug and Release drawing suites,
ApiCompat verifier, and package verifier remain required before delivery.

Removing the three exact suppressions reduces reviewed debt from 0 missing
types, 14 missing members, 15 other diagnostics, and 29 total to 0 missing
types, 11 missing members, 15 other diagnostics, and 26 total. Native HICON
import/export and system file-association thumbnails remain explicit typed
platform work.
