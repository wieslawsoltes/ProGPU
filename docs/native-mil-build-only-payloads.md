# Native payload production before qualification

The LibreWPF package MVP needs native renderer and SDK payloads on each desktop
RID before package-mode startup can be qualified. The ordinary ProGPU native build
scripts also execute CTest, output/export checks, samples and differential runs;
`SkipExtendedIntegration` is a compiler qualification profile, not build-only.

For the implementation-before-validation phase, use explicit CLI-only entry modes:

```sh
# Matching-architecture macOS or Linux build host; Clang and Ninja defaults.
./eng/build-progpu-native.sh --build-only
```

```powershell
# Windows PowerShell 7 host with the existing Visual Studio/LLVM prerequisites.
./eng/build-progpu-native-windows.ps1 -Rid win-arm64 -BuildOnly
./eng/build-progpu-native-windows.ps1 -Rid win-x64 -BuildOnly
```

These modes restore the existing managed dependency package, prepare pinned wgpu
and Dawn headers, configure and compile all native targets with tests/sample
compilation enabled, and stage required renderer and SDK files under
`artifacts/progpu-native/package/runtimes/<rid>/native`. Unix builds produce both
wgpu-native and provider-resolved Dawn libraries plus six SDK archives. Windows
also requires the Direct2D provider DLL and the Dawn import library. Optional PDB
staging uses the same function as normal Windows qualification. All required files
must exist and be nonempty before payload copying starts. This is not an atomic
package-set replacement; use an isolated checkout/staging set for fresh provenance.

No CTest, sample application, managed differential/benchmark, native-output export
check, protocol/coverage verifier or release qualification executes in this mode.
Required CMake code generation and compiler feature probes still run as build
steps. Windows still reads the pinned third-party wgpu DLL export table to produce
the linker import library; this is a build input, not native renderer export
qualification. Unix no longer needs to execute the Dawn verifier to build that
renderer: both paths share dependency-only header preparation from the same
version manifest. Modified existing Dawn header checkouts remain rejected.

The switches cannot be enabled through inherited environment state and reject
combination with the reduced compiler-qualification profile. Normal no-switch
build and release workflows retain their qualification gates and do not select
build-only. Tests still compile in build-only; none are removed from CMake.
Unix build-only parallelism defaults to four jobs and accepts
`PROGPU_NATIVE_BUILD_JOBS`. Existing explicit dependency/build/runtime directory
overrides remain available; do not point them at dirty or mismatched artifacts.

Staged files are **unqualified**, even though their directory is package-compatible.
No success message, executable/image evidence or manifest is manufactured for
skipped gates. Build every required platform payload before packaging. The WPF
SDK build-packages-only lane retains its complete RID and Windows managed-runtime
requirements; native-only output is not an installable LibreWPF package or proof
of source Application startup/Windows admission. Windows managed transport and
IJW payload production remains a separate WPF build step.

Before delivery, rerun the ordinary full native scripts and package/application
gates on the final commits, including default/forced GPU/SIMD paths, DirectX
sample comparisons, image/lifetime/performance tests and required CI. Build-only
does not waive those gates or establish merge readiness.

Implementation provenance is the original ProGPU native build, Dawn verifier and
Windows staging code. The dependency helper extracts the existing pinned checkout
logic rather than introducing a new dependency version or renderer. This is
build orchestration with no renderer, native ABI, GPU kernel or CPU hot-path change.
Both native providers remain compiled; managed portable rendering is unaffected.
Authored source-contract fixtures cover CLI-only selection, required artifacts,
early exit and preserved workflow/verifier gates. Fixture execution is deferred.
