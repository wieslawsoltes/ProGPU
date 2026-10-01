# Drawing native package closure

`ProGPU.System.Drawing.Common` references `ProGPU.Backend.Native`; the native
backend in turn references `ProGPU.Backend.Dawn` and `ProGPU.Backend`. The focused
`drawing-runtime` package registry includes all three, in dependency order,
alongside the existing drawing dependencies. All packages use the same requested
source version.

The missing Native/Dawn entries caused LibreWPF Build 36928515448's canonical
Forms lane and LibreWinForms Build 36928979951's package lane to fail the existing
isolated-closure verifier after successful source compilation. The complete
portable producer group already contained these packages; its success did not
prove that the smaller drawing group was complete.

`eng/test-drawing-package-closure.py` checks the real registry against the source
project references, with missing Native, missing Dawn, wrong-order, wrong-identity
and duplicate controls. The package-manifest verifier runs these checks before
packing. These are source metadata checks, not package or runtime qualification.

Native packaging retains its original runtime validation and package verifier.
Consumers must stage the real multi-RID payload from a successful whole Build for
the exact selected ProGPU commit before packing. Do not disable native validation,
fabricate payload files, accept unrelated versions, or weaken the isolated
dependency guard. LibreWPF and LibreWinForms need matching source gitlinks and
their own successful whole Builds before this integration is qualified.
