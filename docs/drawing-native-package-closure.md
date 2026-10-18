# Drawing native package closure

`ProGPU.System.Drawing.Common` references `ProGPU.Backend.Native`; the native
backend in turn references `ProGPU.Backend.Dawn`, `ProGPU.Backend` and the matching
`ProGPU.Native.Sdk` static-archive package. The focused
`drawing-runtime` package registry includes all four, in dependency order,
alongside the existing drawing dependencies. All packages use the same requested
source version.

The group also includes the real `ProGPU.Scene.Native` bridge after its managed
Scene and native backend dependencies. Drawing's owned font-outline integration
uses this producer to retain original glyph descriptors and positions without
copying its internal wire or ownership implementation into Drawing. Source-first
consumers must be able to resolve this package from the same isolated feed before
the Drawing adapter takes that dependency. This package-list addition does not
enable or qualify a source font-quality policy.

The missing Native/Dawn entries caused LibreWPF Build 36928515448's canonical
Forms lane and LibreWinForms Build 36928979951's package lane to fail the existing
isolated-closure verifier after successful source compilation. The complete
portable producer group already contained these packages; its success did not
prove that the smaller drawing group was complete.

`eng/test-drawing-package-closure.py` checks the real registry against the source
project references, with missing Native, missing Dawn, wrong-order, wrong-identity
and duplicate controls. The package-manifest verifier runs these checks before
packing. These are source metadata checks, not package or runtime qualification.
The native scene bridge has a real-project identity check and an independent
wrong-order control against its managed Scene dependency.

Native packaging retains its original runtime validation and package verifier.
Consumers must stage the real multi-RID payload from a successful whole Build for
the exact selected ProGPU commit before packing. Do not disable native validation,
fabricate payload files, accept unrelated versions, or weaken the isolated
dependency guard. LibreWPF and LibreWinForms need matching source gitlinks and
their own successful whole Builds before this integration is qualified.
