# Source-built WPF media startup selection

## Contract and core dependency

The LibreWPF package-mode MVP/Toolkit startup path must choose portable media
ownership before constructing WPF objects. Window-host callbacks are too late to
make this decision: media objects can acquire composition locks or initialize the
media system before the first window is shown.

`ProGPU.Wpf.Interop/PortableWpfRuntime.cs` owns the original, shared typed policy:

- Default: Windows MIL on Windows, portable media elsewhere. This preserves
  ordinary source-built Windows WPF when no portable backend is selected.
- `SelectMediaBackend(Portable)` selects source-built portable transport, not a
  GPU adapter or managed/native ProGPU renderer. It does not initialize WebGPU.
- `GetMediaBackendAndFreeze()` atomically acquires the process-shared choice.
  The first consumer freezes it. Repeating the same selection is safe; changing
  it after acquisition throws. Shutdown does not reset it because other threads,
  resource objects or load contexts may retain media state.
- Invalid enum values and Windows MIL on non-Windows platforms fail without
  changing the configured state. Diagnostic getters do not freeze selection.
- Selection and first acquisition synchronize through one lock. Frozen reads are
  allocation-free O(1) volatile reads with no locking. There is one policy and one
  lock allocation at initialization. This is control state, not SIMD/compute work.

The host and source-built WPF must share the same interop assembly identity.
The existing LibreWPF harness load context explicitly shares that assembly; its
native-host entry point now selects portable media before loading source WPF.
The SDK native bootstrap selects it before WPF/WinForms initialization on its
currently admitted platforms. An already-initialized wrong backend is an error,
not a request to migrate live resources.

## Paired consumers and remaining implementation

### Input device ownership connection

The package MVP/Toolkit action is to focus an editor, hold a modifier, type and
select with the mouse on a ProGPU-hosted Windows window. Source inspection found
that InputManager still selected Win32 keyboard/mouse devices by OS, while the
typed portable input bridge updates PortableKeyboardDevice/PortableMouseDevice.
It also allowed WPF TSF to promote host-delivered keys and attach source editor
text stores despite having no WPF-owned HWND composition geometry.

InputManager now freezes the shared media choice before constructing devices.
Portable media selects host-owned keyboard/button state on every OS; native Windows
MIL retains Win32 devices. This is process/domain startup ownership, not a live
per-event device switch. ProGPU input reports continue through the existing typed
source raw-input pipeline. The source TSF manager skips promotion for portable
devices and leaves WPF's TSF message pump disabled. Automatic focus does not
associate WPF IMM contexts. Source editors do not schedule or attach WPF TSF stores
under portable input ownership; native WPF keeps its existing behavior.

Committed character delivery is not an IME implementation. Nondefault automatic
input-method preferences fail explicitly until the host composition contract can
apply them. Composition updates/cancellation, candidate positioning, reconversion,
host input-scope policy and public input-method state/configuration semantics remain
separate work. No process-wide TSF service availability override, blanket removal
of Windows text-service checks, invented composition state or native SDK admission
is introduced. Actual Windows language/keyboard/system settings remain OS services.

This adapter change applies equally to managed and C++ ProGPU renderers. The
reusable host event contract and native event producer are unchanged; there is no
renderer algorithm, C ABI or shader change to mirror. Selection is O(1) startup
configuration; event ownership checks are O(1) and allocation-free. No compute or
CPU fallback was added and no performance improvement is claimed.

Authored source fixtures cover selected device types (including an independent
Windows-MIL lane), host key/button state, one committed-text delivery, rejected
unsupported preferences and a live source text-view/editor update without a WPF
TSF store. These are compilation-only checkpoints until feature freeze. Windows
package startup, real keyboard/IME behavior and VM/GPU/CI qualification remain open.

Both source fixture assemblies now accept the test-process-only startup setting
`LIBREWPF_TEST_MEDIA_BACKEND=Portable` or `WindowsMil`. Their shared module initializer
selects before source object/device construction; invalid values fail explicitly.
Unset preserves the platform default. Windows native and portable qualification
must run as separate processes: there is no production reset or admission bypass.
In particular, portable-only fixtures must not be counted as Windows evidence when
they were skipped under the default Windows-MIL selection.

### Media transport connection

LibreWPF routes `MediaSystem` startup/connect/shutdown/redirection behavior,
`CompositionEngineLock`, and `MediaContextNotificationWindow` through the policy.
Portable media contexts use a managed lock for their shared registration list;
no Windows MIL partition, transport, lock or notification HWND is created by
these portable branches. Both asynchronous and synchronous channel allocation
reject portable callers before Windows channel construction. A caller needing
render-to-bitmap must use its portable implementation, not a hidden native channel.

This is common host integration for managed and native ProGPU renderers. The
C++ renderer has no WPF media-system singleton or Windows-MIL startup dependency;
no C ABI, shader, scene compiler, native device lifetime or drawing algorithm
changes are applicable. All existing GPU-first/SIMD policy remains unchanged.
Provenance is original ProGPU policy code plus source-built WPF integration;
no third-party implementation was copied or translated into ProGPU.

**Not complete Windows package support:** geometry/media-resource helpers still
have OS-selected Windows MIL utility calls, and popup/interop-handle creation has
separate ownership branches. The Windows native SDK guard remains until those
core consumers are connected. The selected policy does not prove that all legacy
MIL P/Invokes have been removed from an application. Native HWND platform services
and DirectWrite can remain Windows-specific where they are intentional services;
do not replace every OS check mechanically.

## Design references and applicability

The sources below inform separation of ownership; the startup policy is an
independent implementation, not a port of any engine's configuration code.

- [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  and [Win2D device loss](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm):
  adopt explicit ownership boundaries; reject switching backend identity beneath
  existing device-dependent resources. Device recovery remains in the same
  selected ProGPU backend and reconstructs resources as already implemented.
- [Skia canvas](https://skia.org/docs/user/api/skcanvas_overview/) and
  [shaped text](https://skia.org/docs/dev/design/text_shaper/): preserve separation
  of drawing and reusable text preparation. Selection does not reshape text,
  discover fonts, rebuild display lists or eagerly compile pipelines.
- [WebRender overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  and [Vello renderer](https://docs.rs/vello/latest/vello/struct.Renderer.html):
  preserve retained scene/device ownership separation. Existing visibility
  culling, worker preparation, demand uploads, batching, cache keys/eviction and
  scene generations are unchanged, not reimplemented in startup policy.
- [Parley layout context](https://docs.rs/parley/latest/parley/struct.LayoutContext.html)
  and [HarfBuzz plans](https://harfbuzz.github.io/shaping-plans-and-caching.html):
  keep text layout/shaping reuse separate. Font fallback, variable-font state,
  DPI/subpixel/hinting and glyph/atlas generations remain existing renderer/text
  contracts; no text quality or performance change is claimed.

## Authored qualification

`PortableWpfRuntimeTests` covers platform defaults, diagnostic reads, explicit
selection, frozen/idempotent selection, invalid/unavailable values, late backend
switch rejection, and concurrent first-use/selection. Tests use isolated internal
policy instances, not a reset of the production singleton. Signed test-assembly
friend access introduces no public testing surface or reflection.

LibreWPF source-contract fixtures cover startup/channel/lock guard placement and
SDK ordering. The existing native host fixture now requires the shared portable
selection to have been acquired. These are not proof that every legacy native
utility has been bypassed. Final qualification still requires real package apps
on Windows, macOS and Linux, native dependency observation, interaction/image and
device-lifetime comparisons, and exact-head CI. Tests and runtime workloads remain
deferred until the requested core feature freeze; compilation is recorded in the
linked PR checkpoint, not represented as execution or a speed measurement.

Compilation checkpoint (2026-09-08, repository SDK from the LibreWPF root): final
Release `ProGPU.Tests` builds with 0 warnings/0 errors, source-built WPF host
harness with 4/0, `ProGPU.Wpf.Tests` with 115/0, and SDK smoke harness with 0/0.
The actual SDK bootstrap source compiles in both conditional renderer branches
with 0/0 against source-built WPF and the updated interop assembly. The first
policy-test compile reported blocking-task analyzer warnings; the final race
fixture uses async coordination and a bounded timeout. No fixture was executed.
