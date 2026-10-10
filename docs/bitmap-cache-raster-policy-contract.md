# Source-owned BitmapCacheBrush shader raster policy

BitmapCacheBrush shader input is the selected cache's raw texture, not the
ordinary brush painted over the receiving frame. The original source establishes
this separate [software/hardware sampler path](https://github.com/dotnet/wpf/blob/381194e1ffe4d64fb747556fcaf76e1c34fe9df8/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/ShaderEffect.cpp#L419).
Cache raster size depends on source bounds, selected cache scale, source primary
DPI and device limits; receiving-window DPI is not a substitute. The previous
unexecuted natural-placement/brush-opacity sampler expectations require correction.

`PortableBitmapCacheRasterPolicy` is a pure immutable input value: two original
float DPI scales, two actual device texture limits and a nonzero coherent source
revision. It supplies no default policy and computes no raster size. Native and
managed consumers must preserve selected cache/source ownership, resolve their
own actual cache realization, and reject unavailable policy before publication.
The value validator is not proof that a limit came from a live owned device.

`WgpuContext.TryGetCacheRasterLimits` supplies actual device texture limits through
the optional `IWebGpuTextureLimitsSource` provider capability. Silk and Dawn query
their exact initialized device, not its adapter or requested limits. The context
holds its existing render lock and checks usability and identity before and after
the query. Failure leaves both outputs zero. No queue work, replacement device,
fallback dimensions, new external-initialization parameters or global limit cache
are introduced; an unknown external provider remains explicitly unavailable.
Actual browser-device ownership uses its separate provider implementation.

The optional `IPortablePrimaryDisplayRasterScaleSource` capability supplies a
separate primary-display observation. Its snapshot retains raw per-axis float
bits, an explicit Windows system-DPI or primary-monitor-content-scale policy,
a managed source identity and a nonzero revision. The provider must query its
actual source facility on the appropriate thread and retain its own lifetime.
No boxed native monitor/window handle, averaged axes, resolution ratio, rounding,
receiving-window DPI or inferred 96-DPI fallback is admitted. Non-Windows monitor
policy is explicit platform behavior, not evidence of original Windows parity.

Snapshot equality compares exact float bits and managed reference identity;
it never invokes source-owned equality/hash callbacks. Source policy, selected
device identity and lifetime must remain paired in retained compiler/session and
managed capture keys, including changes with unchanged visual bounds. Failed
new capture must not publish the prior generation as the new policy.

This is original ProGPU-owned contract code, with primary source research used
only to identify behavior and data boundaries. No foreign implementation is
copied. All operations are O(1), with no numerical whole-buffer loop, GPU work,
global DPI cache or per-command native crossing introduced by these DTOs.

Authored value/identity, unavailable/lost/serialized-query and both-owned-provider
limit controls are unexecuted. The source provider, paired native transport,
dedicated cache texture producer,
corrected original/provider pixels, package and source-host gates remain required.
No builds, tests, probes, VM/GPU/UI runs, verifiers or CI were performed here.
