# Native package owner-query validation

## Failure and correction

Build 34501162087 at 2bf43d76 failed in the source-independent native
NuGet consumer's point/region participation fixture. Every native RID job
passed; the failed package job prevented LibreWPF Build 34504992403 from
staging that exact successful build, as required by its dependency gate.

The fixture incorrectly required summary.Id to contain the selected owner in
both query modes. The original ProGPU production shader,
src/ProGPU.Vector/Shaders/GpuHitTesting.wgsl, record_hit, stores the topmost
record in slot zero only for zero-list queries. List queries keep counters
in slot zero and ordered owner records after it. The native Wait contract
preserves that distinction.

The fixture now selects the appropriate record and checks owner 42, primitive
zero and its bound original owner map, as well as participation and counts.
Failures print the participation, query flags, counts and actual owner/index.
No shader, query, timeout, tolerance or package gate was changed.

## Evidence and limits

On macOS ARM64, the complete default consumer built with zero warnings/errors
and passed against the current 08a23ba7 native libraries, using the existing
ProGpuNativeUseProjectReference development lane. It exercised native ABI,
both MIL providers, retained rendering, native GPU owner snapshots and scene
generation isolation. Logs: artifacts/release-hour/package-owner-query-build.log
and package-owner-query-tests.log.

This is local consumer execution, not a newly qualified all-RID NuGet package.
Exact-head CI must rebuild, package and execute the source-independent consumer
before LibreWPF may stage the dependency. Native application integration and the
remaining source document contracts are separate blockers.
