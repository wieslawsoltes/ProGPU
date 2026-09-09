# Portable active access-key scope

## Core application dependency

The LibreWPF MVP has an ordinary `_File` menu and `_About` action. F10/Alt menu
entry with no focused element reaches source `KeyboardNavigation.OnEnterMenuMode`.
That branch previously called user32 GetActiveWindow on every OS. Independently,
default AccessKeyManager scope lookup selected a native HWND on Windows or merely
the first live presentation source elsewhere. Neither identifies an active
portable window reliably. This finding is source-backed, not runtime reproduced.

## Typed boundary and source ownership

`IPortableAccessKeyScopeSource` is an optional source-root capability. Its boolean
read means that the root's actual portable window is live, visible, active and
allowed to receive input. It is not a request to activate or focus that window.
Source WPF Window implements it from existing host-fed IsActive, source visibility,
live portable activation ownership and ProGPU PortableModalInputScope admission.
No reflection, opaque handle probing or allocating WindowState snapshot is used.

LibreWPF's shared active-source resolver filters source ownership, current
dispatcher, disposal and this capability. Zero eligible sources returns no scope;
multiple eligible sources also returns no scope rather than guessing from source
creation order. Missing custom-host capability fails closed. Source-specific menu
and popup scopes remain unchanged. Nonactivating popups do not steal the default
window scope; the owner continues to supply activation through the existing host
policy. Detachment, disposal, hide and modal blocking remove eligibility.

Both AccessKeyManager's default lookup and KeyboardNavigation's no-focus menu
entry use that resolver. A frozen portable media choice never falls through to
user32 when no scope exists. Native Windows MIL keeps its original HWND lookup.
Window activation remains host-owned; this change does not synthesize IsActive or
change keyboard focus, modal sessions, popup window styles or native event loops.

The capability belongs in ProGPU's neutral interop assembly. Enumeration and WPF
access-key/menu semantics belong in source WPF, not a new ProGPU renderer or WPF
bridge workaround. Both native and managed portable renderers use the same path.
There is no C++ rendering algorithm to duplicate for this source-input change.

## Complexity and evidence

Lookup is O(S) for registered sources, using the existing weak source collection
and its snapshot enumeration, with no new persistent cache or per-root DTO. Each
eligibility read is constant work. This is infrequent, object-identity/dispatcher-
dependent control flow, not an independent-lane compute workload or CPU rendering
fallback. No GPU calls, native crossings or pixel readback are added. No measured
performance improvement is claimed.

Authored LibreWPF fixtures cover typed active-root selection, conflicting state,
missing capability, detached/disposed roots, default access-key dispatch, actual
source Window visibility/activation/modal admission, and F10 entry without focus.
Compilation is not runtime evidence. Final package-mode MVP menu/keyboard runs,
multiple-window/dispatcher behavior, native popup focus restoration and Windows
native/portable comparisons remain required after feature freeze. Windows SDK
admission is unchanged.

## Primary contracts consulted

- [GetActiveWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getactivewindow)
  returns the active HWND for the calling thread's queue, not a portable source
  identity. Retain its use only for native Windows routing.
- [Window.IsActive](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.isactive)
  describes actual activation state. Read the existing source value; do not infer
  activation from source registration, retained keyboard focus or Show intent.
- Existing original ProGPU `PortableModalInputScope` and LibreWPF source
  `AccessKeyManager`, `KeyboardNavigation` and host activation policy are the
  implementation provenance. No foreign implementation code was imported.
