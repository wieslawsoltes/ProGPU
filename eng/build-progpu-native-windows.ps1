[CmdletBinding()]
param(
    [string] $Rid = $(if ($env:PROGPU_NATIVE_RID) { $env:PROGPU_NATIVE_RID } else { "win-x64" }),
    [ValidateSet("ClangCL", "MSVC")]
    [string] $Compiler = $(if ($env:PROGPU_NATIVE_WINDOWS_COMPILER) { $env:PROGPU_NATIVE_WINDOWS_COMPILER } else { "ClangCL" }),
    [ValidateSet("Ninja", "VisualStudio")]
    [string] $Generator = $(if ($env:PROGPU_NATIVE_WINDOWS_GENERATOR) { $env:PROGPU_NATIVE_WINDOWS_GENERATOR } else { "Ninja" }),
    [ValidateSet("Full", "Smoke")]
    [string] $BenchmarkProfile = $(if ($env:PROGPU_NATIVE_WINDOWS_BENCHMARK_PROFILE) { $env:PROGPU_NATIVE_WINDOWS_BENCHMARK_PROFILE } else { "Full" }),
    [switch] $SkipExtendedIntegration
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ExpectedCommit = "33133da4ec5a0174cb21539ef2d3346f75200411"
$ExpectedHeadersCommit = "aef5e428a1fdab2ea770581ae7c95d8779984e0a"
$ExpectedDawnHeadersCommit = "01addc4ba8a2915a061b7095a6768b512071ab96"
$PackageVersion = "2.23.0"
$SourceDir = Join-Path $RepoRoot "artifacts/wgpu-native-src"
$DawnHeadersDir = Join-Path $RepoRoot "artifacts/webgpu-headers-dawn"
$BuildDir = Join-Path $RepoRoot "artifacts/progpu-native/build-$Rid"
$IncludeDir = Join-Path $RepoRoot "artifacts/progpu-native/include-$Rid"
$RuntimeDir = Join-Path $RepoRoot "artifacts/progpu-native/runtime-$Rid"
$PackageStage = Join-Path $RepoRoot "artifacts/progpu-native/package/runtimes/$Rid/native"

if (-not $IsWindows) {
    throw "This build entry point is for Windows hosts."
}

switch ($Rid) {
    "win-x64" {
        $CMakeArchitecture = "x64"
        $DevCmdArchitecture = "x64"
        $DevCmdHostArchitecture = "x64"
        $ToolComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
        $LibraryMachine = "X64"
        $ClangTarget = "x86_64-pc-windows-msvc"
        $PackageRid = "win-x64"
        $RunnableArchitecture = [System.Runtime.InteropServices.Architecture]::X64
    }
    "win-arm64" {
        $CMakeArchitecture = "ARM64"
        $DevCmdArchitecture = "arm64"
        $DevCmdHostArchitecture = "arm64"
        $ToolComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
        $LibraryMachine = "ARM64"
        $ClangTarget = "aarch64-pc-windows-msvc"
        $PackageRid = "win-arm64"
        $RunnableArchitecture = [System.Runtime.InteropServices.Architecture]::Arm64
    }
    default { throw "Unsupported Windows native RID '$Rid'." }
}

dotnet restore (Join-Path $RepoRoot "src/ProGPU.Backend.Native/ProGPU.Backend.Native.csproj")
if (-not (Test-Path (Join-Path $SourceDir ".git"))) {
    git clone --filter=blob:none https://github.com/gfx-rs/wgpu-native.git $SourceDir
}
git -C $SourceDir fetch --depth 1 origin $ExpectedCommit
git -C $SourceDir checkout --detach $ExpectedCommit
git -C $SourceDir submodule update --init --depth 1 ffi/webgpu-headers
if ((git -C $SourceDir rev-parse HEAD).Trim() -ne $ExpectedCommit) {
    throw "The pinned wgpu-native source checkout is incorrect."
}
if ((git -C (Join-Path $SourceDir "ffi/webgpu-headers") rev-parse HEAD).Trim() -ne $ExpectedHeadersCommit) {
    throw "The pinned WebGPU headers checkout is incorrect."
}
if (-not $SkipExtendedIntegration) {
    if (-not (Test-Path (Join-Path $DawnHeadersDir ".git"))) {
        git clone --filter=blob:none https://github.com/webgpu-native/webgpu-headers.git $DawnHeadersDir
    }
    if (git -C $DawnHeadersDir status --porcelain --untracked-files=no) {
        throw "Refusing to change a modified Dawn WebGPU header checkout."
    }
    git -C $DawnHeadersDir fetch --depth 1 origin $ExpectedDawnHeadersCommit
    git -C $DawnHeadersDir checkout --detach $ExpectedDawnHeadersCommit
    if ((git -C $DawnHeadersDir rev-parse HEAD).Trim() -ne $ExpectedDawnHeadersCommit) {
        throw "The pinned Dawn WebGPU headers checkout is incorrect."
    }
}

$GlobalPackagesLine = (dotnet nuget locals global-packages --list | Select-Object -First 1)
$GlobalPackages = ($GlobalPackagesLine -replace '^[^:]+:\s*', '').TrimEnd('/', '\')
$WgpuDll = Join-Path $GlobalPackages "silk.net.webgpu.native.wgpu/$PackageVersion/runtimes/$PackageRid/native/wgpu_native.dll"
if (-not (Test-Path $WgpuDll)) {
    throw "The pinned Silk.NET wgpu-native runtime is missing: $WgpuDll"
}

New-Item -ItemType Directory -Force -Path $IncludeDir, $RuntimeDir, $PackageStage | Out-Null
Copy-Item (Join-Path $SourceDir "ffi/webgpu-headers/webgpu.h") $IncludeDir -Force
Copy-Item (Join-Path $SourceDir "ffi/wgpu.h") $IncludeDir -Force
Copy-Item $WgpuDll (Join-Path $RuntimeDir "wgpu_native.dll") -Force

$VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
$VsInstall = (& $VsWhere -latest -products * -requires $ToolComponent -property installationPath | Select-Object -First 1)
if (-not $VsInstall) {
    throw "Visual Studio C++ build tools were not found."
}
Import-Module (Join-Path $VsInstall "Common7/Tools/Microsoft.VisualStudio.DevShell.dll")
Enter-VsDevShell -VsInstallPath $VsInstall -SkipAutomaticLocation -DevCmdArguments "-arch=$DevCmdArchitecture -host_arch=$DevCmdHostArchitecture" | Out-Null

$DefFile = Join-Path $RuntimeDir "wgpu_native.def"
$ImportLibrary = Join-Path $RuntimeDir "wgpu_native.lib"
$Exports = & dumpbin.exe /nologo /exports $WgpuDll |
    ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z0-9_@?$]*)') {
            $Matches[1]
        }
    } |
    Sort-Object -Unique
if (-not $Exports -or $Exports.Count -lt 100) {
    throw "Could not recover the wgpu-native export table."
}
[System.IO.File]::WriteAllLines($DefFile, @("LIBRARY wgpu_native", "EXPORTS") + $Exports)
& lib.exe /nologo "/def:$DefFile" "/machine:$LibraryMachine" "/out:$ImportLibrary"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ImportLibrary)) {
    throw "Failed to generate the pinned wgpu-native import library."
}

[string[]] $GeneratorArguments = if ($Generator -eq "Ninja") {
    if (-not (Get-Command ninja.exe -ErrorAction SilentlyContinue)) {
        throw "Ninja is required for the default fast Windows native build. Use -Generator VisualStudio only as a compatibility fallback."
    }
    @("-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release")
} else {
    @("-A", $CMakeArchitecture)
}
[string[]] $CompilerArguments = if ($Generator -eq "Ninja") {
    if ($Compiler -eq "ClangCL") {
        @(
            "-DCMAKE_CXX_COMPILER=clang-cl",
            "-DCMAKE_CXX_COMPILER_TARGET=$ClangTarget")
    } else {
        @("-DCMAKE_CXX_COMPILER=cl")
    }
} elseif ($Compiler -eq "ClangCL") {
    @("-T", "ClangCL")
} else {
    @()
}
# Named-module import coverage remains on LLVM Clang + Ninja on Linux. The
# Windows lanes compile the same portable C++20 surface through clang-cl and
# MSVC while avoiding CMake/Visual Studio dependency-scanning incompatibilities.
$ModuleArgument = "OFF"
$DawnIncludeArgument = if ($SkipExtendedIntegration) { "" } else { $DawnHeadersDir }
$BuildSampleArgument = if ($SkipExtendedIntegration) { "OFF" } else { "ON" }
cmake -S (Join-Path $RepoRoot "src/ProGPU.Native") -B $BuildDir @GeneratorArguments @CompilerArguments `
    -DPROGPU_NATIVE_ENABLE_CPP_MODULES="$ModuleArgument" `
    -DPROGPU_NATIVE_WEBGPU_INCLUDE_DIR="$IncludeDir" `
    -DPROGPU_NATIVE_WEBGPU_LIBRARY="$ImportLibrary" `
    -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR="$DawnIncludeArgument" `
    -DPROGPU_NATIVE_BUILD_SAMPLE="$BuildSampleArgument" `
    -DBUILD_TESTING=ON
if ($LASTEXITCODE -ne 0) {
    throw "CMake configuration failed for $Compiler/$Rid."
}
$BuildConfigurationArguments = if ($Generator -eq "VisualStudio") { @("--config", "Release") } else { @() }
cmake --build $BuildDir @BuildConfigurationArguments --parallel
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed for $Compiler/$Rid."
}

$BinaryDirectory = if ($Generator -eq "VisualStudio") { Join-Path $BuildDir "Release" } else { $BuildDir }
$NativeDll = Join-Path $BinaryDirectory "progpu_native.dll"
if (-not (Test-Path $NativeDll)) {
    throw "The native renderer DLL was not produced: $NativeDll"
}
$DawnDll = Join-Path $BinaryDirectory "progpu_native_dawn.dll"
$ExpectedNativeExports = Get-Content (Join-Path $RepoRoot "eng/progpu-native-exports.txt") |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Sort-Object -Unique
$ActualNativeExports = & dumpbin.exe /nologo /exports $NativeDll |
    ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(progpu_native_[A-Za-z0-9_]+)') {
            $Matches[1]
        }
    } |
    Sort-Object -Unique
$ExportDifference = Compare-Object $ExpectedNativeExports $ActualNativeExports
if ($ExportDifference) {
    $ExportDifference | Format-Table | Out-String | Write-Error
    throw "The ProGPU native exported-symbol surface changed."
}
if (-not $SkipExtendedIntegration) {
    if (-not (Test-Path $DawnDll)) {
        throw "The provider-resolved Dawn renderer DLL was not produced: $DawnDll"
    }
    $ExpectedDawnExports = Get-Content (Join-Path $RepoRoot "eng/progpu-native-dawn-exports.txt") |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    $ActualDawnExports = & dumpbin.exe /nologo /exports $DawnDll |
        ForEach-Object {
            if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(progpu_native_[A-Za-z0-9_]+)') {
                $Matches[1]
            }
        } |
        Sort-Object -Unique
    $DawnExportDifference = Compare-Object $ExpectedDawnExports $ActualDawnExports
    if ($DawnExportDifference) {
        $DawnExportDifference | Format-Table | Out-String | Write-Error
        throw "The ProGPU Dawn adapter exported-symbol surface changed."
    }
    $DawnImports = & dumpbin.exe /nologo /imports $DawnDll
    if ($DawnImports | Select-String -Pattern '\bwgpu[A-Z]' -CaseSensitive) {
        throw "The ProGPU Dawn adapter imports WebGPU procedures directly."
    }
    Copy-Item $NativeDll (Join-Path $PackageStage "progpu_native.dll") -Force
    Copy-Item $DawnDll (Join-Path $PackageStage "progpu_native_dawn.dll") -Force
    $SdkPackageStage = Join-Path $PackageStage "sdk"
    New-Item -ItemType Directory -Force -Path $SdkPackageStage | Out-Null
    $SdkLibraries = @(
        "progpu_native_dawn.lib",
        "progpu_native_compression.lib",
        "progpu_native_hit_testing.lib",
        "progpu_native_image.lib",
        "progpu_native_mil.lib",
        "progpu_native_text.lib",
        "progpu_native_scene_builder.lib"
    )
    foreach ($SdkLibraryName in $SdkLibraries) {
        $SdkLibrary = Join-Path $BinaryDirectory $SdkLibraryName
        if (-not (Test-Path $SdkLibrary)) {
            throw "The native C++ SDK library was not produced: $SdkLibrary"
        }
        Copy-Item $SdkLibrary (Join-Path $SdkPackageStage $SdkLibraryName) -Force
    }
    $NativePdb = Join-Path $BinaryDirectory "progpu_native.pdb"
    $DawnPdb = Join-Path $BinaryDirectory "progpu_native_dawn.pdb"
    if ((Test-Path $NativePdb) -or (Test-Path $DawnPdb)) {
        $SymbolStage = Join-Path $RepoRoot "artifacts/progpu-native/symbols/$Rid"
        New-Item -ItemType Directory -Force -Path $SymbolStage | Out-Null
        if (Test-Path $NativePdb) {
            Copy-Item $NativePdb $SymbolStage -Force
        }
        if (Test-Path $DawnPdb) {
            Copy-Item $DawnPdb $SymbolStage -Force
        }
    }
}

$CurrentArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
if ($CurrentArchitecture -eq $RunnableArchitecture) {
    $env:PATH = "$BinaryDirectory;$RuntimeDir;$env:PATH"
    ctest --test-dir $BuildDir -C Release --output-on-failure
    if ($LASTEXITCODE -ne 0) {
        throw "Native C++20 tests failed for $Compiler/$Rid."
    }
    if ($SkipExtendedIntegration) {
        Write-Host "MSVC compiler qualification passed without rebuilding the Dawn ABI, packaging, sample, or managed differential matrix."
    } else {
        $SampleDirectory = Join-Path $RepoRoot "artifacts/progpu-native/sample"
        New-Item -ItemType Directory -Force -Path $SampleDirectory | Out-Null
        $NativeSample = Join-Path $BinaryDirectory "progpu_native_sample.exe"
        $NativeSampleOutput = Join-Path $SampleDirectory "progpu-native-sample.ppm"
        $NativeProviderEvidence = Join-Path $SampleDirectory "progpu-native-provider.txt"
        $NativeFont = Join-Path $RepoRoot "src/ProGPU.Fonts.Inter/Fonts/Inter-Medium.ttf"
        & $NativeSample $NativeSampleOutput $NativeProviderEvidence $NativeFont
        if ($LASTEXITCODE -ne 0) {
            throw "The D3D12 native renderer backend sample failed."
        }
        $NativeProviderReport = Get-Content $NativeProviderEvidence -Raw
        $IsParallelsDisplayAdapter =
            $NativeProviderReport -match "(?m)^adapter=Parallels Display Adapter"
        $SampleOutput = Join-Path $RepoRoot "artifacts/progpu-native/sample/progpu-native-managed-$Rid.ppm"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SampleOutput) | Out-Null
        $ManagedSampleProject = Join-Path $RepoRoot "src/ProGPU.Native.ManagedSample/ProGPU.Native.ManagedSample.csproj"
        $ManagedSampleDll = Join-Path $RepoRoot "src/ProGPU.Native.ManagedSample/bin/Release/net10.0/ProGPU.Native.ManagedSample.dll"
        $BenchmarkProject = Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj"
        $BenchmarkDll = Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/bin/Release/net10.0/ProGPU.Native.Benchmarks.dll"
        dotnet build $ManagedSampleProject -c Release --nologo
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ManagedSampleDll)) {
            throw "The managed native-renderer sample build failed."
        }
        dotnet build $BenchmarkProject -c Release --nologo
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $BenchmarkDll)) {
            throw "The native differential benchmark build failed."
        }
        & dotnet $ManagedSampleDll $SampleOutput
        if ($LASTEXITCODE -ne 0) {
            throw "The managed native-renderer sample failed."
        }
        function Invoke-NativeBenchmark {
            & dotnet $BenchmarkDll @args
            if ($LASTEXITCODE -ne 0) {
                throw "The native differential benchmark failed: $args"
            }
        }
        if ($IsParallelsDisplayAdapter) {
            # The Parallels D3D12 driver removes the device in the legacy managed
            # renderer's dense mixed-picture path. Keep the full 384-command
            # stress on the C++ renderer, then require a bounded live pixel
            # differential without executing that unsafe managed workload.
            Invoke-NativeBenchmark --managed-picture --profile-native-only --rectangles 384 --warmup 4 --iterations 8
            Invoke-NativeBenchmark --managed-picture --rectangles 1 --warmup 0 --iterations 1
            Write-Host "Qualified the Parallels mixed-picture profile with native stress plus bounded differential parity."
        } else {
            Invoke-NativeBenchmark --managed-picture --rectangles 384 --warmup 4 --iterations 8
        }
        Invoke-NativeBenchmark --group-opacity --rectangles 384 --warmup 4 --iterations 8
        Invoke-NativeBenchmark --external-images --warmup 2 --iterations 4
        Invoke-NativeBenchmark --masked-images --warmup 2 --iterations 4
        Invoke-NativeBenchmark --semantic-local-cache-brush-mask
        Invoke-NativeBenchmark --semantic-local-cache-fant
        Invoke-NativeBenchmark --semantic-local-cache-multi-guideline
        Invoke-NativeBenchmark --semantic-nested-cache-effect
        Invoke-NativeBenchmark --semantic-cache-mask-effect
        Invoke-NativeBenchmark --semantic-cache-effect-clip
        Invoke-NativeBenchmark --semantic-bounded-effect
        Invoke-NativeBenchmark --semantic-scene --rectangles 96 --warmup 2 --iterations 4
        Invoke-NativeBenchmark --semantic-layer-effects --rectangles 96 --warmup 2 --iterations 4
        Invoke-NativeBenchmark --text-shaping --text-repeats 2 --warmup 8 --iterations 16
        if ($BenchmarkProfile -eq "Smoke") {
            # Windows qualifies the D3D12-specific paths with one representative
            # member of every retained resource/effect family. The exhaustive
            # scene cross-product remains on the Linux/macOS integration lanes.
            Invoke-NativeBenchmark --paths --group-vector-clip-chain --rectangles 96 --warmup 2 --iterations 4
            Invoke-NativeBenchmark --images --group-effect-chain --rectangles 96 --warmup 2 --iterations 4
            Invoke-NativeBenchmark --images --group-blend-mode Overlay --rectangles 96 --warmup 2 --iterations 4
            Invoke-NativeBenchmark --group-blend-mode ColorDodge --rectangles 96 --warmup 2 --iterations 4
            Write-Host "Completed the bounded Windows D3D12 differential smoke profile."
        } else {
            $VectorClipScenes = @("", "--analytic", "--geometry", "--paths", "--glyphs", "--images")
        foreach ($Scene in $VectorClipScenes) {
            $SceneArgs = @()
            if ($Scene) {
                $SceneArgs += $Scene
            }
            $SceneArgs += @("--group-vector-clip-chain", "--rectangles", "96", "--warmup", "2", "--iterations", "4")
            Invoke-NativeBenchmark @SceneArgs
        }
        $EffectScenes = @("", "--analytic", "--geometry", "--paths", "--glyphs", "--images")
        foreach ($Effect in @(
            "--group-gaussian-blur",
            "--group-drop-shadow",
            "--group-effect-chain")) {
            foreach ($Scene in $EffectScenes) {
                $SceneArgs = @()
                if ($Scene) {
                    $SceneArgs += $Scene
                }
                $SceneArgs += @($Effect, "--rectangles", "96", "--warmup", "2", "--iterations", "4")
                Invoke-NativeBenchmark @SceneArgs
            }
        }
        foreach ($BlendMode in @("SrcAtop", "Overlay")) {
            foreach ($Scene in $EffectScenes) {
                $SceneArgs = @()
                if ($Scene) {
                    $SceneArgs += $Scene
                }
                $SceneArgs += @(
                    "--group-blend-mode", $BlendMode,
                    "--rectangles", "96", "--warmup", "2", "--iterations", "4")
                Invoke-NativeBenchmark @SceneArgs
            }
        }
        foreach ($BlendMode in @("ColorDodge", "Saturation")) {
            Invoke-NativeBenchmark `
                --group-blend-mode $BlendMode `
                --rectangles 96 --warmup 2 --iterations 4
        }
        }
    }
} else {
    Write-Host "Cross-compiled $Rid; execution is deferred to a matching-architecture CI lane."
}

if ($SkipExtendedIntegration) {
    Write-Host "Qualified the ProGPU native renderer with $Compiler/$Generator for $Rid."
} else {
    Write-Host "Staged ProGPU native renderer for $Rid in $PackageStage"
}
