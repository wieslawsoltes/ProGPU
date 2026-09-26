# Android x64 rendering validation

The `Android x64 rendering` workflow exercises the real sample APK on an x86_64
Android emulator. It complements native-library staging tests: a successful
MSBuild item selection is not evidence that an APK loads or renders. The target
is the wgpu-native UI path discussed in [issue #75](https://github.com/wieslawsoltes/ProGPU/issues/75),
not Dawn media import or provider-resolved C++ renderer qualification.

## Inputs and execution

The workflow checks out the exact PR head. The existing
`eng/build-wgpu-native-android.sh x64` builds the repository's pinned wgpu-native
commit and Silk ABI using its locked dependencies, NDK `27.2.12479018` and Rust
`1.85.1`. Existing ELF architecture, SONAME, WebGPU export, page-alignment,
manifest and checksum checks remain enabled. Only Cargo build inputs are cached;
the provider is restaged and checked by the build script on every run.

`eng/progpu-build-android-sample.sh` builds the actual source sample with both
`RuntimeIdentifier` and `RuntimeIdentifiers` set to `android-x64`. Debug uses
`EmbedAssembliesIntoApk=true` so ordinary adb installation does not depend on
an IDE's Fast Deployment payload. The final APK must contain the exact staged
provider bytes in its x64 native directory. Its other native entries must also
be x64 ELF libraries. A desktop or wrong-architecture provider is not accepted
because its filename happens to match.

The Actions runner is Ubuntu 24.04 x64, with explicit accessible KVM and an
API 35 `google_apis;x86_64` image. The emulator uses `-gpu swiftshader` and
`-accel on`, not an ARM guest or silently unaccelerated execution. This is
software-Vulkan functional evidence; it is not physical-device GPU performance.
The emulator action is pinned by commit, but SDK emulator/image packages are
versioned external inputs whose actual versions must be retained with results.

`eng/progpu-test-android-emulator.sh` attaches to one already-running adb target;
it does not create another emulator or VM. It resolves the package's actual
launcher activity, installs the signed APK and launches it. Evidence includes
the sample process's Vulkan first-frame diagnostics, positive drawing/content
dimensions, foreground activity state, unfiltered logcat and a real screenshot.
An installed APK, live process, successful `am start`, assembly-probe message,
or activity showing an error view is not a rendering pass.

The job has a fixed 30-minute deadline and the application probe has a bounded
launch deadline. Failures retain diagnostics. Existing native, mobile-package,
browser, Release/AOT and application gates are unchanged. The workflow also
triggers for relevant stacked PRs; those still need the ordinary main-targeted
Build gates before final integration.

## Evidence and qualification

Artifacts are retained under `artifacts/progpu-native/android-x64-evidence`,
including the signed APK, provider/build provenance, build log/binlog, native
inventory, device/tool versions, activity state, full logcat and screenshots.
Parser unit tests use synthetic input only and do not qualify native binaries
or a device. A green device run must be followed by inspection of its actual
screenshot before claiming that the reported blank-display behavior is resolved.

Dawn `AHardwareBuffer` media import, the provider-resolved C++ engine, physical
device behavior/performance and fully trimmed/AOT Release execution remain
independent requirements. The UI lane does not replace or weaken them and does
not change product provider or renderer defaults.

## Primary references

- [.NET Android build properties](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties#embedassembliesintoapk)
  describe embedded managed assemblies versus Fast Deployment.
- [GitHub hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
  document Android hardware acceleration on Linux runners.
- [Android emulator acceleration](https://developer.android.com/studio/run/emulator-acceleration)
  distinguishes matching guest architecture, VM acceleration and graphics modes.
- [Android emulator runner configuration](https://github.com/ReactiveCircus/android-emulator-runner#configurations)
  defines explicit image architecture and emulator options; its defaults are not
  the x64/Vulkan contract used here.
