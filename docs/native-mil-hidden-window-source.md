# Hidden LibreWPF window sources

## Core application dependency

The package MVP and Toolkit/AvalonDock startup path must support interop source
initialization before showing a window. Source inspection found that
`WindowInteropHelper.EnsureHandle()` called `Window.CreateSourceWindow(false)`
directly, bypassing portable activation and entering the Windows HWND/MIL path.
This is a source-backed blocker, not a reproduced runtime failure.

## Contract and ownership

`PortableWindowActivationCallbacks.CreateHidden` is an optional, typed init-only
factory. Its addition preserves the existing callback constructor signature.
Existing hosts still support ordinary activation; hidden creation must explicitly
reject an absent capability rather than invoke the ordinary factory or switch
window backends. The returned activation must own a stable nonzero source handle,
remain hidden and inactive, and leave the WPF visual tree detached. Factory
failures own cleanup of anything not returned to the caller.

LibreWPF consumes this contract in `PortableWindowActivationService` and
`Window.CreateSourceWindow` for both renderer modes. It validates the handle,
publishes activation before `SourceInitialized`, and uses the existing object for
reentrant/repeated handle requests, later Show/Hide and close-before-show.
Rejected owned sources are closed/disposed; close failures still attempt disposal.
Portable media without host registration cannot create a legacy Windows source.

`WpfPortableWindowActivation` creates an empty portable presentation source,
initializes its existing platform host hidden, and retains the source-built WPF
root without attaching it. The first Show attaches that root through the typed
presentation-source bridge. Starting the application loop before that Show uses
the same host loop without showing the window or rendering a rootless frame;
a later Show can attach the tree while the loop is running.
Normal host show, renderer selection, device recovery,
input and scene compilation are reused. An explicit custom factory returning null
throws rather than selecting a default renderer. This avoids a visible Show/Hide
flash and does not raise an activation request merely to obtain a source handle.

The existing portable presentation-source handle is a source-registry identity,
not a native HWND. Consumers must use typed portable interop or platform adapters;
passing it to arbitrary user32 P/Invoke is unsupported. This work does not admit
the guarded Windows SDK lane: popup ownership and ordinary MIL geometry/media
utilities still need connection and the full package path needs qualification.

## Provenance and applicability

The added ProGPU code is an original callback contract and independent regression,
not copied WPF or third-party implementation. LibreWPF integration reuses its
existing `TryAttach`, `InitializeHidden`, presentation-source and host lifetime
implementations. Both managed and native renderer modes use that same host.
No C ABI, C++ drawing, shader, shaping or geometry algorithm changes apply; this
is window/source ownership control, with O(1) routing and bounded per-window
callback state rather than compute-heavy CPU work. GPU/SIMD policy is unchanged.

[Microsoft's EnsureHandle contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.windowinterophelper.ensurehandle?view=windowsdesktop-10.0)
informs hidden creation, event order, reuse and delayed visual attachment, not
implementation text. Native HWND interoperability is explicitly not generalized
to portable source identities. The prior
[cross-engine ownership and startup research](native-mil-startup-selection.md#design-references-and-applicability)
remains applicable: renderer/text caches, retained scene generations, visibility
culling, upload policy, worker preparation, batching, font fallback and DPI/text
quality are unchanged. Hidden host initialization uses the existing host/device
load path; no cold-start or performance improvement is claimed.

## Authored qualification, not executed

ProGPU callback fixtures cover explicit hidden capability versus legacy ordinary
activation. Source-built WPF fixtures cover stable and reentrant handle queries,
one initialization event, unchanged hidden visibility, show/hide/reuse,
close-before-show and missing/rejected/zero-handle failures. Source-contract
assertions cover routing before legacy HWND creation. Final real-host/package
qualification must additionally observe root attachment, no visible flash or
foreground activation, native resource cleanup and original renderer identity on
macOS, Linux and Windows Parallels, including initialization and close failures.

Tests, source verifiers, runtime/VM/image/lifetime/benchmark workloads and CI
qualification remain deferred until core feature freeze, as requested. Automatic
CI is not disabled. Compilation results are recorded in the delivery checkpoint;
compilation is not evidence that a window was created or that parity passed.
