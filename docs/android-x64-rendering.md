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

The sample owns its Android target framework; the build invocation does not
replace every referenced project's framework with an Android global property.
The two XAML generator project references remove only target RID properties,
so the generator and its analyzer dependencies execute as RID-free host tools
while the application's runtime references retain `android-x64`. Executable
SDK reference-resolution tests cover both Android RIDs and both consumers.
Fresh-configuration Fluent-theme and mobile-sample library builds additionally
exercise the actual host generator closure; they are not APK runtime evidence.

The Actions runner is Ubuntu 24.04 x64, with explicit accessible KVM and an
API 35 AOSP `default;x86_64` image, without Google apps/services. This is the
ordinary full-rendering image, not an automated-test-device image with graphics
disabled. The emulator uses `-gpu swiftshader` and
`-accel on`, not an ARM guest or silently unaccelerated execution. This is
software-Vulkan functional evidence; it is not physical-device GPU performance.
SDK emulator/image packages are versioned external inputs whose actual versions
must be retained with results. The CI-owned lifecycle installs the same stable
API 35 image/emulator packages through the already provisioned Android tools.

`eng/progpu-run-android-emulator.py` creates one fresh AVD in its own temporary
directory and owns its emulator/logcat subprocess groups. It refuses existing
adb devices and is restricted to the Linux x64 Actions lane with accessible KVM.
It neither wipes another AVD nor kills the adb server; cleanup targets only its
own processes. Emulator versions, configuration, launch output, boot logcat,
activity/window/input state and a pre-install screenshot are retained.

Boot readiness must complete within 300 seconds of emulator launch. The boot
property alone is insufficient: provisioning and user setup must be complete,
and the resolved HOME component must be the actual resumed and focused activity.
Its own activity record must acknowledge the first/all/reported draw and current
visibility; its matching window must have a shown, ready, visible on-screen
surface with no keyguard. Ordinary Android 15 window dumps omit `mDrawState`, so
that optional field is not required. An explicitly pending state still rejects
the candidate. Draw flags from another activity/window cannot satisfy the check.
Any recorded boot ANR or fatal log is a hard failure, not a retryable condition.

The lifecycle sends no synthetic input, unlock command or ANR-dialog dismissal.
It retains the prior animation settings only after HOME readiness and rechecks
that readiness before installing the APK. Boot polling and every command share
the same remaining 300-second budget; diagnostic capture after a failure never
extends admission. The separate application probe retains its 120-second budget.

`eng/progpu-test-android-emulator.sh` attaches to one already-running adb target;
it does not create another emulator or VM. It resolves the package's actual
launcher activity, installs the signed APK and launches it. Evidence includes
the sample process's Vulkan first-frame diagnostics, positive drawing/content
dimensions, foreground activity state, unfiltered logcat and a real screenshot.
The sample must own the actual focused window both before and after capture:
a resumed activity alone does not exclude another application's ANR dialog.
An installed APK, live process, successful `am start`, assembly-probe message,
or activity showing an error view is not a rendering pass.

The first-frame marker means that CPU submission/presentation returned, not
that Android has displayed the buffer. The probe therefore polls actual PNGs
within the **same 120-second launch deadline**, with decoding itself bounded
by the remaining time. Only a structurally valid, opaque, focused screenshot
without distributed client content is pending; malformed PNGs, focus loss,
process replacement and application errors fail immediately. Every candidate
retains focus checks around capture and a subsequent process-specific log check.
No fixed delay is accepted as evidence and no launch deadline is extended.

The blank-frame guard reconstructs all five PNG filters for 8-bit RGB/RGBA,
including byte wraparound and Paeth tie order. It inspects only the central
80% width and 60% height, excluding status/navigation bars. At least two of nine
interior tiles in opposite rows or columns must contain contrast spanning two quantized 16-level RGB bins,
covering at least 0.1% of each tile and at least 16 pixels. Uniform backgrounds,
tiny noise, alpha-only differences, isolated bright pixels and a small overlay
crossing adjacent tile boundaries cannot pass.
This deliberately limited negative guard is not a gallery pixel oracle.
Processing is linear in decoded bytes; reconstruction needs two rows of scratch
beside the bounded inflated input, and each tile histogram has at most 4096 bins.

The job has a fixed 30-minute deadline and the application probe has a bounded
launch deadline. Failures retain diagnostics. Existing native, mobile-package,
browser, Release/AOT and application gates are unchanged. The workflow also
triggers for relevant stacked PRs; those still need the ordinary main-targeted
Build gates before final integration.

## Evidence and qualification

Artifacts are retained under `artifacts/progpu-native/android-x64-evidence`,
including the signed APK, provider/build provenance, build log/binlog, native
inventory, device/tool versions, activity state, full logcat and screenshots.
The initial screenshot, its tile metrics and focus dumps are retained separately
from the final candidate. Attempt results/timing and initial/final SurfaceFlinger
layer inventories and state supplement the existing unfiltered logs. The probe
also checks the cumulative live log so circular-buffer rotation cannot discard
an earlier application failure. Parser tests use synthetic positive cases and
an original rejected screenshot; they do not qualify native binaries or a device.
A green device run must be followed by inspection of its actual
screenshot before claiming that the reported blank-display behavior is resolved.

Run [36236053116](https://github.com/wieslawsoltes/ProGPU/actions/runs/36236053116)
verified the actual APK/provider and recorded a Vulkan first frame, but is not
a visible-rendering pass: a Pixel Launcher ANR began before sample launch and
covered the captured display. Its separate emulator-version probe also exposed
the missing host `libpulse.so.0` dependency. The CI setup now installs `libpulse0`,
uses the ordinary AOSP image to avoid Google first-boot services, and validates
window focus around the screenshot. CPU/RAM settings and all deadlines are
unchanged; no dialog is dismissed or ignored to produce a pass.

Run [36236752156](https://github.com/wieslawsoltes/ProGPU/actions/runs/36236752156)
then passed the original automated checks with the AOSP image, but manual review
rejected its black client. The 10:52:19.760 first-frame marker preceded an
emulator `Qsri fence` timeout at 10:52:22.887 and the screenshot near 10:52:23.
All 1,244,160 pixels in its selected interior were black. Its exact original PNG
is retained as a hash-verified negative fixture; the guard rejects all nine tiles.
The fence warning and timing motivate bounded presentation polling and additional
diagnostics, not a proven renderer defect or a claim that waiting fixes it.

Run [36238216481](https://github.com/wieslawsoltes/ProGPU/actions/runs/36238216481)
correctly failed its focus guard because Quickstep's ANR obscured the sample.
The prior action observed boot completion at 11:21:23.370 and immediately sent
keyevent 82; the launcher recorded an input-dispatch/no-focused-window ANR at
11:21:29.115, before APK installation at 11:21:35. Its retained ANR snapshot had
a surface and focus entry but a hidden `DRAW_PENDING` launcher buffer. This is
the negative boot fixture, not sample rendering evidence. The pinned action had
no hook between its boot-property poll and unconditional input, so its lifecycle
was replaced by original ProGPU orchestration rather than patched/copied action
implementation. This does not establish the separate Qsri timeout's cause.

Offline boot tests include the actual ANR excerpt, representative ordinary
Android 15 positive formats, incomplete provisioning, resolved HOME identity,
pending/hidden/stale windows, missing draw acknowledgements, cumulative fatal
logs, fixed resource configuration, remaining-deadline accounting and subprocess
ownership/timeout cleanup. Synthetic positives are parser tests only. A real
successful boot and visibly correct sample remain required after these changes.

`AndroidWindowHost` now owns a disposable subscription to the existing process-wide
`WgpuContext.OnWebGpuError` and `OnWebGpuDeviceLost` events. Typed failures reach
the same `ProGPU.Android` error log already rejected by this gate. Subscription
begins before renderer creation and ends before host teardown; stale captured
event delegates cannot invoke the released sink. These diagnostics do not
initialize, recover or poll devices, and do not claim per-device attribution.
Four host-side tests cover reporting, independent subscribers, repeated disposal,
an already-captured callback and invalid construction.

Applicability audit: this changes Android host diagnostics and external evidence
validation only, not either renderer's submission, resource lifetime, shaders or
public C ABI. The existing shared managed event source is reused; no C++ engine
algorithm is replaced or approximated. The current lane remains UI-only
wgpu-native; process-wide logging does not qualify the native-engine or Dawn path.
The PNG implementation is original code from the specification, not a port of
a third-party decoder. Its scalar byte reconstruction belongs only to offline
CI evidence processing, never the rendering or managed/native hot path.

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
- [Official AOSP image catalog](https://dl.google.com/android/repository/sys-img/android/sys-img2-3.xml)
  lists the Android 35 `default;x86_64` system image.
- [Android Vulkan implementation](https://source.android.com/docs/core/graphics/implement-vulkan)
  describes the native fence used by queue signal/release image operations;
  returning from CPU presentation is not proof of display completion.
- [Android SurfaceView](https://developer.android.com/reference/android/view/SurfaceView)
  and [SurfaceHolder.Callback2](https://developer.android.com/reference/android/view/SurfaceHolder.Callback2)
  were reviewed for surface/redraw ownership. No speculative callback or compositor
  behavior change is included without runtime evidence.
- [PNG filter specification](https://www.w3.org/TR/png-3/#9Filters)
  supplies the byte reconstruction, edge conditions and Paeth tie contract.
- [Pinned emulator runner lifecycle](https://github.com/ReactiveCircus/android-emulator-runner/blob/a421e43855164a8197daf9d8d40fe71c6996bb0d/src/emulator-manager.ts#L90)
  was inspected to identify the unconditional boot-time input and missing
  readiness hook; no implementation code was imported.
- [Android emulator command line](https://developer.android.com/studio/run/emulator-commandline)
  and [AVD manager commands](https://developer.android.com/tools/avdmanager)
  define the existing CLI/configuration contracts used by the owned lifecycle.
- [ADB device state](https://developer.android.com/tools/adb#devicestatus)
  explicitly distinguishes an attached device from a fully operational system.
