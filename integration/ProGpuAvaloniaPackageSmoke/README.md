# ProGPU Avalonia package smoke

This code-only app consumes ProGPU exclusively through NuGet package
references. It validates typed Silk.NET startup, the ProGPU renderer, the
managed OpenType shaper, a retained composition scene, and a real WebGPU
presentation path.

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Set `PROGPU_INTEGRATION_BUILD_ONLY=1` for restore/build validation without
opening a desktop window.
