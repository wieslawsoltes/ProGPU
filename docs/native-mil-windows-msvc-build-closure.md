# Windows MSVC native package build closure

## Acceptance dependency and provenance

LibreWPF's package-mode Showcase needs Windows native payloads before native MIL
startup can be admitted and qualified. The Windows ARM64 build of ProGPU
`2ab498be3589e151f3ae344139a48667782a2608` exposed strict MSVC compilation
failures not observed in the preceding macOS/Linux Clang builds. This batch
repairs those source failures; it does not expand Direct2D coverage or weaken
the final renderer, package, architecture or CI gates.

All implementation and fixture changes derive from the original ProGPU files
named below. No external implementation was copied. The existing Windows build
entry remains in explicit `-BuildOnly` mode, including both renderer providers,
Direct2D, SDK archives, and test/sample compilation with `/W4 /WX`.

## Source changes and paired applicability

- `src/ProGPU.Native/src/Mil/progpu_native_mil.cpp`: name the prepared stroke
  geometry distinctly from its enclosing geometry; reject a nested picture
  stream larger than the uint32 wire limit before explicitly narrowing its size.
  This is native C++ naming and size-domain correctness, not a rendering change.
  Managed `NativeSceneStreamBuilder.TryAddLayerPictureMaskResource` consumes an
  int-length span and already compares its exact length with the uint wire field;
  it cannot represent the overflowing native size. No paired managed edit is
  necessary. The generated MIL ledger changes only its decoder digest.
- `src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp`: compare
  boolean affinity to an explicit bool conversion of the already validated
  trailing field. Both native C++ bool records and C ABI uint8 records still
  instantiate the same algorithm, and invalid ABI values still fail closed.
  Managed `TextLayout.GetCaretStop` and `MoveCaretVisually` already compare bool
  fields; they do not have this MSVC mixed-type issue. No ranking, bidi, source
  position, allocation or complexity behavior is changed.
- `progpu_native_text_interaction_interop_tests.cpp`: author differential
  coverage for both affinities at a shared logical position, visual movement
  in both directions and stationary movement, plus invalid trailing-byte
  rejection for movement. These fixtures are not yet executed.
- Native geometry, MIL, scene-builder and Direct2D compatibility fixtures: avoid Windows `far`
  and `min`/`max` macro collisions, distinguish local names rather than suppress
  shadow warnings, and use uint32 wire types in sampling/geometry/brush expectations.
  Assertions, COM ownership and fixture scope remain intact.

The added size check is constant-time control validation. The remaining edits
are type/name corrections and fixture authoring, not new CPU kernels or scalar
fallbacks. Existing GPU-first/SIMD contracts remain unchanged; no performance
claim follows from compilation.

## Build-only environment

The existing Windows 11 ARM64 Parallels VM resumed gracefully. Its existing
Visual Studio Build Tools use MSVC 19.44.35228.0 (toolset 14.44.35207). Portable
PowerShell 7.6.6 and MinGit 2.55.0.windows.5 are staged only in the task artifact
directory; their downloaded ZIP digests matched official release asset digests.
No machine/user PATH, VM settings or execution policy was changed. PowerShell 7
reports its existing RemoteSigned policy. Native builds do not request a policy
override. Windows PowerShell 5 remains Restricted.

The isolated guest source starts from the committed checkpoint and receives only
the explicitly listed source fixes. Unrelated working-tree scene changes and
performance artifact deletions are excluded. The commands are:

```powershell
./eng/build-progpu-native-windows.ps1 -Rid win-arm64 -Compiler MSVC -BuildOnly
./eng/build-progpu-native-windows.ps1 -Rid win-x64 -Compiler MSVC -BuildOnly
```

The ARM64 command completed successfully after these fixes, including all native
test/sample executables and required staged libraries. Nothing was executed for
runtime qualification. The x64 build remains a separate required result.
Further execution status and output staging are recorded in the consuming
LibreWPF build report. A command listed here is not evidence it completed. CMake compiler probes,
dependency restore, pinned input preparation and source generation are build
steps. Test execution, native contract verification, applications, GPU workloads,
benchmarks and CI polling remain deferred until feature freeze. In particular,
the required native contract verifier has not run for this implementation batch.

Windows managed/IJW production, complete package consumption and Windows native
SDK admission remain separate open requirements. Unix payloads produced at the
preceding checkpoint must be refreshed to the final delivery source; they are not
evidence for these newer edits.
