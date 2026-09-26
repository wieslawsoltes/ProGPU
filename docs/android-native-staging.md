# Android native-library staging

The source project and packaged Android targets previously had different
contracts. Source builds globbed all ABI directories and accepted either ABI
for provider and zero-copy checks. Package targets omitted the Dawn provider,
warned only about wgpu-native, and inferred the engine ABI from a path ending
in `native`. An ARM64 library could therefore hide a missing x64 provider.

`ProGpuStageAndroidNativeLibraries` now owns source and package selection. It
resolves exact filenames per requested RID, preserves the caller's root
properties and existing items, and adds explicit `Abi`/`RuntimeIdentifier`
metadata. Source, package-root and runtimes-root layouts share the same
algorithm. Unsupported RIDs and missing required Dawn fail before new native
items are published. Ordinary missing providers retain warning behavior.
The native-engine package entries and manifests are unchanged.

Selection runs at target execution, before `_CategorizeAndroidLibraries`, item
validation and library import collection, so late SDK properties are visible.
Categorization must see the items before it captures a class library's native
payload for its AAR; staging at `PrepareForBuild` alone is too late. The installed .NET Android
36.1.53 SDK selects outer ABIs from `RuntimeIdentifiers` when present, and
creates per-RID publish inner builds. The target follows those two contexts;
it does not interpret `-r android-x64` alone as overriding the sample's dual-RID
declaration. [Microsoft's native-library item contract](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-items#androidnativelibrary)
documents explicit ABI metadata. The installed
`Microsoft.Android.Sdk.Tooling.targets`,
`Microsoft.Android.Sdk.AssemblyResolution.targets`, and
`Xamarin.Android.Common.targets` supplied the build-order reference.

This changes packaging metadata only. Managed and C++ renderer paths receive
the same provider/engine files; neither renderer's algorithms, runtime provider
selection, library ABI pins, GPU defaults or media-import semantics change.
No third-party implementation was imported. Work is bounded by the requested
RID list, three library families and three exact candidate paths per family;
it performs no recursive directory scan or render-time work.

## Evidence and remaining qualification

On 2026-09-26, the actual source project was evaluated with .NET 10.0.201 and
Android workload 36.1.53 for an explicitly x64-only request. With no provider
files installed, the target emitted `PROGPUANDROID001` for x64 and no native
items. Enabling `ProGpuRequireZeroCopyMedia` emitted `PROGPUANDROID002` and
failed with no native items. All eleven Android media source-contract tests
passed after moving their staging assertions to the shared target.

All 36 executable `AndroidNativeLibraryStagingTests` passed on macOS ARM64.
They launch SDK-independent MSBuild projects against an exact
copy of the packaged targets and inspect actual output items, including failed
builds. Coverage includes all three families/layouts and both ABIs, layout
priority, strict missing-Dawn failures, partial multi-RID input, unsupported and
duplicate RIDs, late SDK properties, outer/inner RID selection, package-default
engine paths, caller-owned item preservation and non-Android no-op behavior.
The matrix exposed and verified a real metadata-ordering correction: each
fallback layout must inspect the preceding MSBuild update, not another metadata
assignment in the same update. Fixtures use text markers, never native binaries.

The first compiled x64 sample APK in run
[36235352764](https://github.com/wieslawsoltes/ProGPU/actions/runs/36235352764)
also exposed a scheduling defect: SDK 36.1.69 categorized the host library
before its dynamic native items existed, so the final APK omitted wgpu-native.
The four new scheduling cases fail before the correction and pass after it,
observing both ABIs and application/library contexts without directly invoking
the staging target. An independent call to the installed 36.1.53 SDK's actual
`_CategorizeAndroidLibraries` target likewise changed from empty output to the
exact x64 marker in `EmbeddedNativeLibrary`, with its ABI/RID metadata intact.
That marker probe does not build an AAR or load native code; the compiled APK
and runtime gate must still verify real provider bytes and rendering.

These checks do not establish actual ELF architecture, successful Android
compilation, APK archive contents, device loading, Vulkan capabilities or
rendered output. The host has the Android workload/runtime packs but no usable
SDK/NDK, adb, emulator, AVD contents or Android ProGPU native payloads. No APK
or emulator launch was claimed. The x64 sample report
[ProGPU #75](https://github.com/wieslawsoltes/ProGPU/issues/75) remains open:
its assembly-probe log alone does not establish a native provider failure.

Runtime qualification must build the exact pinned provider and native-engine
artifacts, inspect actual ELF architectures and `lib/x86_64` APK entries, then
capture a complete launch log and rendered output on an x64 Android target
with Vulkan support. Android's [emulator acceleration guidance](https://developer.android.com/studio/run/emulator-acceleration)
requires a matching accelerated guest architecture; an ARM64 run is not x64
qualification. Physical-device performance, trimmed/AOT media behavior and
full application fidelity remain independent gates.
