# Source-controlled portable dialog lifetime

The LibreWPF MVP About dialog uses the same portable host in native C++ MIL and
managed rendering modes. Application run lifetime and modal dialog lifetime are
different: the application may remain alive with hidden windows; a dialog's
source can end its synchronous invocation without destroying the native window.

`PortableWindowActivationCallbacks.RunDialog` is an optional, typed callback
separate from `Run`. It receives the existing activation and a borrowed
`Func<bool>` continuation. The source captures capability admission before Show,
owns dialog/result/visibility state and the modal enter/leave scope, and rejects a
host that returns while the dialog is still open. Missing capability must not
fall through to application Run or a WPF HWND dispatcher loop.

The host pumps native events, dispatcher work and rendering while its normal
native lifetime remains valid and the continuation permits another iteration.
It checks the continuation before polling and after callbacks, outside existing
close/dispose exception filters. Hiding must not close/dispose or recreate the
window. Continuation exceptions propagate; the predicate is borrowed on the host
thread and never stored beyond this synchronous call. A per-dialog delegate is
bounded control flow, not a SIMD-eligible compute kernel or per-frame allocation.

The WPF source consumer and host integration live in LibreWPF. This neutral
ProGPU contract is original API design using object identity and a boolean
continuation, not copied WPF implementation. Public contract references consulted:

- [Window.Hide](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.hide):
  hiding preserves the instance and does not raise Closing/Closed.
- [Window.ShowDialog](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showdialog):
  synchronous result, application modality and owner/activation semantics.

Scope: this connects loop lifetime, not full modality. Native owner-window
configuration, disabling other application windows (including newly created
windows), nested-modal input restriction and previous activation restoration
remain required integration work. Do not infer them from ComponentDispatcher's
modal notification or the presence of this callback. Windows SDK admission remains
guarded; no native menu, popup or rendering algorithm is replaced here.

Authored regressions cover optional capability separation, continuation identity
and exception propagation; LibreWPF covers Hide/reuse, canceled result closes,
missing callbacks, premature host return and modal-scope cleanup. The MVP existing
dialog gate additionally hides a live dialog, retains it in Application.Windows,
then reuses and closes it. Tests and native application runs are deferred until
the final implementation-freeze qualification phase. Both renderers must pass the
same actual application actions on macOS/Linux and Windows Parallels; compilation
or a callback-only fixture is not runtime/modal parity evidence.

Compile-only checkpoint: ProGPU.Tests builds with 0 warnings/0 errors; LibreWPF
source PresentationFramework fixtures build with 2 warnings/0 errors (6/0 on the
initial dependency rebuild), bridge fixtures with 116/0 and source-built
application harness with 0/0. No fixture, verifier, GPU/native application, VM,
benchmark or CI gate was executed. Latest fetched ProGPU main is included in the
feature branch. The C++ scene/compiler/render algorithms are unchanged: native
and managed modes both consume this same host/source lifetime contract.
