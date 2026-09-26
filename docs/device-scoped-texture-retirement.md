# Device-scoped texture retirement

## Defect and bounded fix

The managed compositor subscribed to the process-wide texture-ID disposal event.
Disposing a texture on an unrelated device therefore invalidated every retained
scene, and could turn an otherwise stable third frame into a cache miss.

The regression creates two actual independent GPU contexts, warms the dense
rounded-rectangle scene, disposes a 1x1 texture on the second device, and checks
the next frame's cache hit, specialization and complete pixel buffer. The
unchanged main implementation failed deterministically with `Compiled scene
available`, matching the diagnostic seen in PR188's Linux run. The historical
CI run did not record the offending texture identity, so this is not a claim
that its particular disposal event has been reconstructed.

`WgpuDeviceResourceDomain` now owns one immutable `WgpuDeviceIdentity`. Textures
and compositors capture that token, not a native pointer or resource-domain
lease. Shared surface contexts already share the same domain and therefore the
same token. Context disposal may clear its active domain without changing a
texture's captured identity. Late disposal/finalization still reaches the right
device's compositors. Unrelated devices return before mutating any cache.

The established ID-only notification remains intact. The typed notification
uses `finally` to preserve the legacy notification even if a new subscriber
throws. Resource release, queued bind-group retirement, source texture IDs,
device ownership and completion waits are unchanged. A token does not prove
that a device is live, retain native resources, or authorize native dereference.

## Validation

- Actual original-code negative control: foreign-device regression failed, 1/1.
- Fixed focused regression set: foreign disposal, same-device invalidation,
  original dense-rounded pixels, and existing binding-release checks passed.
- Fixed compositor class with immutable tokens and late-owner-disposal case:
  240 tests passed, zero skipped, on macOS ARM64. Full CI remains independent.
- Same-device case checks one legacy/typed notification despite repeated
  Dispose, cache invalidation, unchanged pixels and subsequent cache reuse.
- Late-retirement case disposes the actual context before its texture and
  verifies the original device token and exactly one notification survive.

No sleep, warmup retry, assertion relaxation, compiler/adapter selection change,
or canceled producer build is used. This is a retained-cache correctness fix,
not native application performance, whole-device residency or new shared-surface
platform qualification.

## Architecture references and unchanged contracts

The implementation comes from original ProGPU `GpuTexture` retirement,
`WgpuDeviceResourceDomain`, shared-context initialization, and compositor cache
invalidation. No third-party implementation was copied or ported.

[Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
and [Win2D device loss](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/handling-device-lost)
support distinguishing shared-device resources from unrelated devices. The
adopted concept is ownership-qualified invalidation; ProGPU retains its existing
typed identity and retirement machinery rather than adding a Direct2D bridge.

[CanvasKit](https://skia.org/docs/user/modules/canvaskit/),
[WebRender](https://github.com/servo/webrender), and
[Vello](https://github.com/linebender/vello) remain architectural comparisons for
retained GPU rendering. No display-list compiler, raster algorithm, culling
policy or cache replacement algorithm changes here.

[DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
[Parley](https://github.com/linebender/parley), and
[HarfBuzz clusters](https://harfbuzz.github.io/working-with-harfbuzz-clusters.html)
remain text-contract references. Shaping/layout reuse, fallback selection,
variable-font state and original cluster identities are unchanged.

| Concern | Decision |
| --- | --- |
| Startup and lazy pipelines | No eager GPU work; one identity token per existing domain. |
| Retained scenes and visibility | Preserve scene compiler/culling; stop unrelated-device invalidation. |
| Glyph/texture/path keys and eviction | Existing IDs, generations, budgets and eviction remain authoritative. |
| Demand uploads, workers, GPU batching | No new upload, submission, readback, worker or traversal. |
| DPI, subpixel rendering and hinting | Existing rendering paths and complete pixel oracle unchanged. |
| Device loss and atlas invalidation | Keep real resource retirement; token survives owner disposal but owns nothing. |

The foreign-device callback is O(1), allocation-free, and touches no cache.
Same-device cache cleanup retains its existing complexity. No latency or
scroll-throughput improvement is claimed without representative measurement.
