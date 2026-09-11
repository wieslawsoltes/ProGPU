# Source-owned application lifetime and native host handoff

## Contract and core dependency

LibreWPF's package SDK and Toolkit/AvalonDock application must not terminate
merely because the first native polling window closes. The Showcase explicitly uses
OnMainWindowClose; the Toolkit uses the default OnLastWindowClose. Explicit
application lifetime additionally permits closing all windows and creating a new
one later. These are application/host contracts, not new renderer API breadth.

`PortableApplicationRunLoop` borrows a typed struct source on one application
thread. That source owns shutdown policy, live host discovery, each native pump,
and a blocking dispatcher wait when no host exists. The coordinator never closes
windows, replaces application MainWindow, invents source identities, or chooses
another renderer. It rechecks shutdown after selection/wait, rejects a retired
selection, and rejects callbacks that return without retirement or shutdown.
Application callback exceptions propagate unchanged, without implicit shutdown
or a busy retry. Callers must not catch these as device-loss recovery.

Source WPF selects an existing live activation on the application's dispatcher,
preferring its actual MainWindow when available, then its actual window list.
Existing source Window close logic continues to decide when each ShutdownMode
requests application shutdown. Without native windows, the source dispatcher
waits for posted/timer work; its OperationCompleted hook exits the wait when a
live host appears or shutdown is requested. The hook is removed in finally.
Only actual application/dispatcher shutdown ends Application.Run. Reentrant Run
is rejected before changing the application's window list.

The host distinguishes deferred first Show from pumping an existing window.
Handoff must preserve visibility and activation intent, including hidden sources;
it must not show a hidden window, reactivate a visible one, or use the dialog pump
and thereby add native modal hints. An intervening Hide cancels deferred Show.
Both managed portable and native MIL use this same application/window host layer.
The C++ renderer has no source application lifetime policy to duplicate; its
scene, cache, device and surface contracts are unchanged.

## Cost and ownership

The coordinator performs O(H) host transitions, plus caller selection and event
dispatch costs, with O(1) storage and no retained host references. Source window
selection is O(W) worst case per handoff, with direct indexed traversal, not
per-frame snapshots. A hostless interval owns one dispatcher frame and bounded
hook closure, with no timer polling or manufactured native window. These are
ordered lifecycle operations with user callbacks, not independent-lane compute;
SIMD and GPU-stage execution policies are not applicable. No performance gain
or runtime parity is claimed before final measurement and qualification.

## Authored coverage and release gate

`PortableApplicationRunLoopTests` covers host handoff, windowless wait/reopen,
shutdown from wait and from a still-live host, premature returns and exception
identity. LibreWPF's existing RealApplicationRunHarness adds three separate
process scenarios using real source Application/Window/PortablePresentationSource:
OnLastWindowClose handoff, OnMainWindowClose termination with another window,
and explicit windowless lifetime followed by dispatcher-posted creation and an
explicit nonzero exit. They assert source identity, close/dispose/Exit counts and
reentrant Run rejection. They do not instantiate a native GPU host or replace
the real Showcase/Toolkit/package gates. The SDK gate retains its normal application
smoke and adds these scenarios after it; build-packages-only never executes them.

The harness shares the neutral interop assembly across its isolated source load
context and registers typed callbacks, avoiding positional binding to a growing
internal Register signature. Its original callback now explicitly requests
Shutdown after its existing assertions rather than depending on loop return.
Public source API reflection in the diagnostic-only isolated loader is temporary;
the exit path is direct source references when its assembly binding permits them.
No product reflection is introduced.

Fixtures are authored for the final validation phase; compilation alone is not
execution evidence. Required visible multi-window interaction, rendering after
handoff, native/managed comparison, hidden-window behavior and all platform CI
remain mandatory. This connection does not admit Windows native SDK packages or
qualify Cocoa modal popups, Linux modality or cross-dispatcher applications.

Build-only checkpoint (2026-09-09): SDK 10.0.201 compiled
`src/ProGPU.Tests/ProGPU.Tests.csproj` with `--no-restore -m:1 -v:quiet
'-clp:ErrorsOnly;Summary'`: 0 warnings, 0 errors, 11.28 seconds. No tests,
verifiers, renderer workloads, benchmarks or CI polling were executed.

## Primary contracts and provenance

- Microsoft's [Application.ShutdownMode contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.shutdownmode?view=windowsdesktop-10.0)
  defines the default and the distinction between last-window, main-window and
  explicit shutdown. Adopted as behavior; no foreign implementation was copied.
- Microsoft's [DispatcherHooks.OperationCompleted contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatcherhooks.operationcompleted?view=windowsdesktop-10.0)
  places completion notifications on the dispatcher thread. Adopted for a bounded
  source-owned wait rather than observing posting from arbitrary threads.
- ProGPU baseline `82cbc114` and LibreWPF baseline `19e0303ad` supplied the existing
  typed registrars, source close policy, native pump and startup Show deferral.
  The coordinator is original ProGPU code derived from those public contracts;
  source-specific adaptation remains in LibreWPF. This changes application
  lifetime, not rendering/text/cache/startup pipeline architecture or GPU work.
