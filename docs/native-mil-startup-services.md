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

Windows SDK admission remains guarded. The source text dispatch connection below
replaces the OS-only LineServices choice, but remaining source text contracts and
Windows media utility routes still need application-level closure. Early provider
readiness does not prove that every consumer uses it. Do not enable Windows
admission solely because this helper or the direct host compiles.

## Source formatter dispatch connection

Acceptance path: the existing native host's pre-host FormattedText and inline
TextBlock, and the package-mode MVP constructor/first layout. Source inspection
found that Windows still used LineServices and that even non-Windows simple text
could bypass the registered native paragraph provider. This is a source-backed
blocker, not a reproduced application failure during the deferred-test phase.

`TextFormatterImp` now freezes/reads `PortableWpfRuntime` for text-engine ownership.
Windows-MIL mode preserves native LineServices. Portable mode invokes the registered
provider before any simple-line shortcut, capturing it once for the request. Errors
and null provider results cannot turn into legacy empty paragraphs. Explicit
portable Windows use without a provider fails before simple/native formatting;
the preexisting provider-less non-Windows bring-up path remains transitional.

Wrapped continuations resolve their owned immutable paragraph before querying the
registry. Removing or replacing a provider cannot discard or reshape an already
formatted paragraph. Changed continuation width/source positions remain explicit
failures. A clone survives disposal of the preceding line and original break.

The same frozen choice now guards Classification's native table initialization and
LineServices control-string lookup: `TextRunCacheImp`/`TypefaceMap` need character
classification, and `FormatSettings.FetchTextRun` needs TextStore's separator/hidden
characters even when a native LineServices context is never created. These routes
reuse the existing portable source implementations; they are not a new Unicode
algorithm or proof of classification parity. Portable context acquisition and
optimal paragraph-cache creation reject before allocating native contexts.

Intrinsic minimum/maximum measurement and whole-word wrapping are now connected
through the [shared native implementation](native-mil-intrinsic-text.md). Missing
provider metrics still reject instead of reporting a formatted line as an intrinsic
minimum. Forced optimal-break reconstruction, optimal paragraph caches, display
hinting, trimming, embedded objects and the other source restrictions remain open.

This change adapts the existing research decision—retain reusable shaped/layout
results independently of rasterization—and does not change fonts, shaping,
line-breaking algorithms, cache/atlas state or GPU work. Both rendering consumers
can use the same typed paragraph; native startup installs its provider. Dispatch
adds O(1) control/reference work, no additional per-request selection allocation,
and no CPU pixel readback. Existing intrinsic metric work remains unchanged.
No foreign code is imported and no speed claim is made.

Authored fixtures cover public simple-Latin provider dispatch, provider errors/null
results, cloned continuation after unregister/disposal, and explicit rejection of
missing intrinsic metrics/optimal contexts. These dispatch fixtures skip a Windows-MIL test
process rather than mutating its frozen resource domain; explicit-portable Windows
coverage belongs to the existing native application/VM gate. Source guards retain
the Windows SDK admission barrier. All fixture execution remains deferred.

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
