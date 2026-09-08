# Native MIL source-service startup

## Core application dependency

LibreWPF package-mode applications may construct FormattedText, measure inline
content or query geometry in their application/window constructors, before any
ProGPU window host exists. The native SDK previously selected portable media
before module initialization, but installed native text formatting only in the
host constructor. Unsupported early text could therefore reach the transitional
empty-line fallback. Geometry registration through activation's static constructor
also happened after source-module initialization.

`ProGpuWpfNativeMediaServices.Initialize()` now selects portable media and installs
lazy text and geometry defaults before either source module constructors or the
LibreWinForms initializer. Native host construction uses the same initializer.
Custom hosts must call it before constructing source-built WPF objects, not after
creating a Windows-MIL resource. A conflicting frozen media selection throws
before installing services; repeating the same initialization is allowed.

Initialization allocates no native renderer, window, device, surface or font
context. Font contexts remain lazy and are created only on an actual formatting
request. It does not itself select host/window renderer options or grant SDK
platform admission. Normal managed-portable host initialization is unchanged.

## Default and explicit provider ownership

ProGPU's `PortableDefaultServiceSlot<T>` separates a process-lifetime default from
the replaceable explicit registration. Text and geometry registries share it:

- The first default wins, including when installed beneath an active override.
- Explicit registrations always take precedence; the latest registration wins.
- Disposing the current override reveals the default. Disposing an older override
  cannot clear a newer one, and no old override is resurrected.
- Without an installed default, disposing the current override still leaves the
  service absent. Ensure/Register reject null.
- Registration does not own or dispose the service. A reader retains its acquired
  managed reference; callers own any explicit service's resource synchronization.

This repairs the previous behavior where disposing a temporary override could
erase the only default registration and restore the missing-provider path. Get and
EnsureDefault perform O(1) atomic/reference operations without per-call allocation;
explicit Register allocates one lifetime token. These are control-plane operations,
not compute-heavy loops or GPU fallbacks. Existing native/managed algorithms and
intrinsic metric/geometry work are unchanged; no speed improvement is claimed.

## Applicability, research and remaining admission

Source services are independent of glyph/geometry rasterizer choice. The registry
fix applies to both renderer consumers; early native text installation is specific
to an explicit native-MIL host/SDK request. The native C++/C ABI, shaders, retained
scene/cache keys, atlas generations, worker preparation, font fallback, variable
state, hinting and device-loss ownership are not changed by registration timing.
The [cross-engine research record](native-mil-text-source-integration.md)
retains the relevant separation: Skia/HarfBuzz layout results, DirectWrite/Win2D
range services, Parley reusable font/layout state, and Vello/WebRender rendering
ownership. Adopt lazy reusable CPU services; reject eager GPU creation or an empty
paragraph as a substitute for source-service readiness. No foreign implementation
is copied into ProGPU.

Windows SDK admission remains guarded. `TextFormatterImp.IsNativeLineServicesAvailable`
still selects Windows LineServices by OS, and remaining source text contracts and
Windows media utility routes need their application-level connections. Early
provider readiness does not prove that every consumer uses it. Keep the source/
package startup trace on that dependency; do not enable Windows admission solely
because this helper or the direct host compiles.

## Authored qualification

Slot fixtures cover first-default priority, explicit replacement, idempotent and
out-of-order disposal, missing defaults, null rejection and concurrent override
churn. The existing native host now constructs real styled text before creating
its host. The package SDK smoke App constructor checks service readiness, measures
bidi text and performs a geometry union/query when native mode is compiled.
Source guards preserve ordering and the independent Windows admission guard.

These fixtures and application assertions are authored, not executed. The package
constructor's native conditional branch awaits rebuilt package-mode qualification.
No tests, source verifiers, apps, VM/GPU workloads, benchmarks or CI checks are run
during this implementation-first batch. Final qualification must prove startup
ordering and output using the exact delivered managed/native/package binaries.

Compile-only checkpoint: ProGPU fixtures build with zero warnings/errors, bridge
fixtures with 116 warnings/zero errors, and the source-built native host harness
with four warnings/zero errors. The new SDK constructor checks remain authored
pending package rebuild; these build results do not cover that conditional branch.
Latest fetched ProGPU main is included. No runtime or CI qualification is claimed.
