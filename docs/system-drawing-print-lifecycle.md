# Managed print-controller lifecycle

`PrintDocument.Print()` keeps one selected controller for an invocation and
reports its actual preview, file, or printer action. This is managed callback
coordination; it does not add a printer driver or platform printing adapter.
`StandardPrintController` continues to throw `PlatformNotSupportedException`.

The lifecycle contract distinguishes cancellation before page processing from
cancellation or exceptions during it:

| Boundary | Observable completion |
| --- | --- |
| `BeginPrint` cancels | Application `EndPrint` only; no controller startup. |
| Controller startup cancels | Application `EndPrint`, then controller completion; no page query. |
| Page query or page processing cancels | Application `EndPrint`, then controller completion with cancellation set. |
| Page processing throws | The exception propagates after application and controller completion. |
| Application `EndPrint` throws after page processing starts | Controller completion still runs; its input is not rewritten after the failed handler. |
| Application `EndPrint` throws during early cancellation | Controller completion is not synthesized. |

The application may also cancel from `EndPrint`; successful pages must not erase
that decision. If completion callbacks themselves throw, normal exception
replacement follows callback order. A controller assigned during a callback is
retained for the next invocation, not substituted into the active one.
One queried settings snapshot also survives the entire page sequence: edits
made by the first query remain available to later pages without mutating the
document's default settings.

## Implementation and conformance

The implementation extends the existing ProGPU-owned `PrintDocument` page loop.
The [official .NET print-controller contract](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.printing.printcontroller?view=windowsdesktop-10.0)
and [upstream callback behavior](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/src/System/Drawing/Printing/PrintController.cs)
were consulted as conformance references. No third-party source was copied or
ported. Existing managed page-graphics acquisition and disposal remain intact.
Coordination uses constant per-invocation state and constant work per callback;
it adds no reflection, native crossings, GPU initialization, or page-sized state.

`PrintDocumentLifecycleTests` exercises 28 cases through real `PrintDocument`
instances and public controller overrides: exact callback sequences, all
cancellation points, preview/file action precedence, exception identity and
replacement, repeated multi-page printing, retained query/settings identity,
controller identity, and explicit native-printer rejection. Each test is bounded
to two 8×8 pages at most.

There is no corresponding native C++ print-document/controller API to update.
The shared renderers receive unchanged drawing commands; native rendering, page
origin/DPI behavior, printer discovery, and physical printer submission are not
qualified by these lifecycle tests. LibreWinForms consumers need an aligned
source/package dependency update before this behavior can be claimed there.
