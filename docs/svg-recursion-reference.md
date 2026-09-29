# Recursive SVG.NET failure reference

The System.Drawing corpus currently reports `ArgumentNullException` for three
recursive fixtures whose inventory expects an isolated-worker failure:
`resvg|tests/structure/image/recursive-2`, `w3c|struct-image-12-b`, and
`w3c|struct-use-08-b`. This is not evidence that null clips should be accepted,
that recursive images render correctly, or that exception baselines can change.

`eng/probes/SvgRecursion` runs those exact fixtures in separate processes using
the same expected PNG dimensions and pinned SVG.NET/corpus revision as the full
quality lane. Microsoft Windows Desktop System.Drawing is selected independently
on Windows x64 and ARM64; Linux uses ProGPU. The preparation overlay changes only
project targeting/references. No SVG.NET renderer source is patched or copied.
The native identity gate requires the actual Microsoft Windows Desktop assembly
path, .NET 10 identity and public-key token `cc7b13ffcd2ddd51`; the portable gate
requires the ProGPU provider key. That native identity is independently recorded
by the successful Windows drawing-clip reference (Build `36511937631`), not
inferred from the similarly named framework `System.Drawing` assembly.

Each worker records loaded assembly hashes, fixture/reference hashes, dimensions,
process architecture and runtime before rendering. Linux registers the same
21/22 pinned font faces as the full corpus and records their inventory hash;
Windows retains its actual Microsoft font resolver. The first exception, first
drawing-library exception and final exception are recorded separately because
recursive cleanup can mask an earlier error. Raw worker stdout/stderr, exit status and the unchanged 30-second timeout
remain distinct from managed exceptions; nothing normalizes or substitutes their
types. Evidence paths must be fresh, and a worker that fails before publishing its
identity fails the collection job.

A successful collection job means only that all three actual outcomes were
captured. The original whole-corpus image/exception inventories, thresholds,
performance budget and worker deadlines remain authoritative and unchanged.
Native reference outcomes must be inspected before deciding whether a product
fix or a precisely justified exception-inventory correction is appropriate.

The diagnostic was written specifically for ProGPU. Source inspection of the
pinned SVG.NET `SvgElement.Drawing.cs` identified instance-field clip restoration
as a possible secondary error path; no third-party implementation was incorporated.

## First hosted comparison

Run `36519569856` captured all three fixtures on Windows x64, Windows ARM64 and
Linux. Both Microsoft runs reject `Graphics.ScaleTransform` with
`System.ArgumentException`, without a timeout or process crash. Linux first
rejects a non-finite mapping in `Region.Transform`; SVG.NET cleanup then replaces
it with `ArgumentNullException(region)`. Therefore the observed portable exception
is not justified by the Windows reference, and the inventory remains unchanged.

The clip mapper previously materialized a float inverse before composing the
capture/current frames. Its determinant reciprocal can overflow even when their
relative scale is exactly two. The implementation now retains the ordinary float
path for finite arithmetic, but directly computes the relative affine mapping in
double when the determinant, inverse or composition overflows. Only the final
mapping is narrowed. Singular and unrepresentable results still fail; world-matrix
admission and original clip ownership are unchanged. Thirteen authored source
cases cover both determinant/reciprocal overflow, shared translation, snapshot
ownership, unchanged command state and a genuinely unrepresentable result.

This repair still requires hosted execution. No exception-inventory entry or
quality threshold is changed. The Windows checkouts have different raw SVG hashes
from Linux; receipts now additionally hash LF-normalized decoded source text to
check for checkout line-ending differences without changing the rendered files.
