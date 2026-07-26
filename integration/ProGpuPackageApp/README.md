# ProGPU package integration app

This code-only desktop app verifies the published integration surface without any Avalonia or ProGPU `ProjectReference`. It starts with Silk.NET windowing and the ProGPU renderer, then uses `IProGpuApiLeaseFeature` from a custom draw operation to submit ProGPU vector commands and an animated WGSL shader through the WebGPU ShaderToy extension. The shader lives in `Shaders/ApiLeaseWave.wgsl`, is embedded at build time, and is decoded once through `ShaderResource`.

Use freshly packed packages from this checkout:

```bash
./integration/ProGpuPackageApp/run.sh local
```

Use the exact-identity source-built Avalonia replacement stack from an isolated
local feed:

```bash
./integration/ProGpuPackageApp/run.sh replacement
```

Local mode also consumes ProGPU runtime packages from the sibling `../ProGPU/artifacts/packages/Release` directory. Set `PROGPU_PACKAGE_SOURCE` when the ProGPU checkout or package output is elsewhere.

Use only packages indexed on NuGet.org:

```bash
./integration/ProGpuPackageApp/run.sh nuget
```

Both modes use a temporary NuGet configuration, HTTP cache, and global-packages folder. Local mode packs the integration packages first and puts the local package source before NuGet.org.

Run a deterministic native smoke that opens maximized, renders the API-lease and WGSL example, and closes after two seconds:

```bash
PROGPU_INTEGRATION_SMOKE=1 ./integration/ProGpuPackageApp/run.sh local
PROGPU_INTEGRATION_SMOKE=1 ./integration/ProGpuPackageApp/run.sh nuget
```

Run the package-only shared-device disposal-order gate:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_MULTI_WINDOW_SMOKE=1 \
./integration/ProGpuPackageApp/run.sh replacement
```

This opens two windows on one typed WebGPU device domain, disposes the original
device-owning window first, proves that the surviving window continues
rendering, then opens and disposes another borrowing window and proves the
survivor remains healthy. The gate requires rendered frames after both disposal
orders, one retained composition scene, and zero flattened fallback nodes.

For Xcode Instruments, keep the final surviving window alive longer than the
capture so `xctrace` owns process termination and can finalize the trace:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_MULTI_WINDOW_SMOKE=1 \
PROGPU_INTEGRATION_PROFILE_HOLD_SECONDS=20 \
./integration/ProGpuPackageApp/run.sh replacement
```

The profiling hold accepts 1 through 120 seconds. It changes only the delay
before shutdown: after both disposal-order checks for the multi-window smoke,
or after startup for the ordinary one-window smoke. Without it, the ordinary
and multi-window smokes retain their two-second and 650 ms delays.

For a non-interactive compile check, set `PROGPU_INTEGRATION_BUILD_ONLY=1`:

```bash
PROGPU_INTEGRATION_BUILD_ONLY=1 ./integration/ProGpuPackageApp/run.sh local
PROGPU_INTEGRATION_BUILD_ONLY=1 ./integration/ProGpuPackageApp/run.sh replacement
PROGPU_INTEGRATION_BUILD_ONLY=1 ./integration/ProGpuPackageApp/run.sh nuget
```

After `tools/pack-avalonia-progpu-stack.sh` has already completed, set
`PROGPU_REUSE_REPLACEMENT_STACK=1` to reuse that validated local feed. The
consumer gate still checks the NuGet SHA-512 identity of the restored Avalonia,
renderer, Silk.NET host, and all eight ProGPU runtime-closure packages against
the validated local package payloads. It deletes same-version entries from its
isolated cache before the local-only restore, so a previously published runtime
cannot be mixed into the source-built stack.

In replacement mode the app is also compiled with a package-only runtime gate.
`PROGPU_INTEGRATION_SMOKE=1` requires rendered ProGPU frames, at least one
retained Avalonia composition scene, and zero flattened fallback nodes. This
proves that the restored renderer contains and executes the exact-source
compositor seam rather than only matching package bytes.

Publish and run the same exact restored replacement stack as a trimmed
self-contained NativeAOT executable:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_SMOKE=1 \
./integration/ProGpuPackageApp/run.sh replacement
```

The runner resolves the host RID, restores the AOT compiler/runtime graph in
the same isolated package folder used by the SHA-512 identity gate, publishes
with full trimming, then executes the native binary. The temporary packages,
compiler artifacts, and publish directory are deleted by the existing exit
trap. Set `PROGPU_INTEGRATION_RUNTIME_IDENTIFIER` only for an explicit
host-compatible RID override. `PROGPU_INTEGRATION_BUILD_ONLY=1` publishes and
validates the executable without launching it.

Override `PROGPU_INTEGRATION_PACKAGE_VERSION` to validate another integration preview.
