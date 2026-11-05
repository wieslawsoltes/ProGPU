# Paired font-capture consumer

This windowless control compares two ProGPU-authored fonts across interpreters
35/40 and automatic, SIMD and scalar projection. It retains the original unfitted
coordinates and placements while checking the exact scan-metadata transfer,
caller storage isolation, duplicate/no-ink descriptors and selected-view lifetime.
It performs no desktop input or GPU rendering and does not qualify source pixels.

For a complete matching package closure, run from the repository root:

```sh
dotnet run --project tests/ProGPU.FontCapture.PackageConsumer -c Release \
  -p:ProGpuNativePackageSource=/absolute/path/to/packages \
  -p:ProGpuNativePackageVersion=THE_EXACT_CANDIDATE_VERSION
```

Source preparation uses `-p:ProGpuNativeUseProjectReference=true` instead and
requires the matching built native library on the native loader search path.
A source run is not a package result. Record the actual loaded native library
and SHA-256, all twelve capture lines and the successful process exit. There is
no fallback when a requested projection, native export or topology is unavailable.

The existing Build consumer matrix runs this once per platform's `all`/`core`
lane; its native package artifact includes the matching Scene.Native managed
closure. The existing Release consumer matrix runs it against the complete
portable package artifact on all six desktop RIDs. No new workflow or job is
introduced, and the original rendering/pixel consumers remain mandatory.
