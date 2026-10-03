# Browser device texture-limit ownership

`BrowserWebGpuApi` implements the optional `IWebGpuTextureLimitsSource` seam.
Only a provider constructed with the default `BrowserGpuRuntime.Dispatch` path
after successful runtime initialization captures the private device generation.
Its opaque `DeviceHandle` is compared, never dereferenced. Arbitrary capabilities
passed to `BrowserGpuContext.Create`, adapter limits, requested limits and default
constants cannot supply this capability.

The main-thread JavaScript runtime reads the actual
[`GPUDevice.limits`](https://developer.mozilla.org/en-US/docs/Web/API/GPUDevice/limits)
`maxTextureDimension2D` value, returning it as both maximum width and height.
These are device limits, not the adapter's potential limits. The shared API
publishes outputs only after exact generation and device-object checks before
and after the read, with a positive integer representable as `uint`.
Initializing a replacement immediately invalidates old identities. Late managed
or JavaScript initialization cannot publish a superseded generation. The
[`GPUDevice.lost`](https://developer.mozilla.org/en-US/docs/Web/API/GPUDevice/lost)
promise revokes the optional capability upon observed loss or intentional
destruction; loss of an older device cannot invalidate a replacement. There is
no synchronous WebGPU loss oracle or GPU completion claim here.

Unsupported, uninitialized, replaced, lost or retiring/disposed providers return
false with both outputs zero. A custom dispatcher never borrows the default
runtime's proof, even if it happens to forward some commands there. Disposal
revokes the capability before flushing, so a failed flush cannot leave a limit
capability active while retirement is retried. Queries do not encode, flush,
submit, poll or wait for commands and do not change existing constructors.

## Worker boundary

The existing Worker/IsolatedWorker protocol publishes initialization diagnostics
and an unversioned asynchronous `device-lost` message followed by page reload.
It does not expose an authoritative synchronous worker-device identity/lifetime
snapshot to the managed main realm. Both worker modes therefore return false
and zero outputs, including when an older main-realm device remains present.
Implementing that optional worker capability requires a paired lifetime-aware
transport; a cached capabilities reply or a main-thread placeholder is not one.

## Authored controls and status

`BrowserTextureLimitsTests` covers custom dispatch, foreign/null tokens,
uninitialized default dispatch, caller-created capability records, retirement,
zero outputs and untouched pending command bytes. The independent lifecycle
script `eng/progpu-test-browser-texture-limits.mjs` invokes the actual runtime
functions with owned test doubles: exact device values distinct from adapter
values, replacement, late initialization, old/current loss, destruction, malformed
limits, reentrant getter replacement, worker rejection and generation exhaustion.
Those doubles test ownership policy, not real browser device availability.

No build, test, JavaScript execution, syntax check, verifier, browser/GPU/UI/VM
execution or CI dispatch was performed for this source checkpoint. Final
integrated managed/browser qualification remains required. This optional
metadata provider does not enable automatic browser/WPF BitmapCacheBrush
admission, change sampling, or qualify the corrected sampler implementation.
