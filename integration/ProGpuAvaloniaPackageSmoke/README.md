# ProGPU Avalonia package smoke

This code-only app consumes ProGPU exclusively through NuGet package
references. It validates typed Silk.NET startup, the ProGPU renderer, the
managed OpenType shaper, a retained composition scene, and a real WebGPU
presentation path.

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Exercise both shared-device window destruction orders from the same isolated
package consumer:

```bash
PROGPU_PACKAGE_SMOKE_MULTI_WINDOW=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Publish and execute a genuine standalone NativeAOT binary:

```bash
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_RUNTIME_IDENTIFIER=osx-arm64 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

The NativeAOT lane restores the IL compiler into the private package cache and
fails if publish leaves a managed application DLL in place of the expected
native executable.

Set `PROGPU_INTEGRATION_BUILD_ONLY=1` for restore/build validation without
opening a desktop window.
