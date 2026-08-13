[CmdletBinding()]
param(
    [string] $Rid = $(if ($env:PROGPU_NATIVE_RID) { $env:PROGPU_NATIVE_RID } else { "win-x64" })
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
        $PackageRid = "win-x64"
        $RunnableArchitecture = [System.Runtime.InteropServices.Architecture]::X64
    }
    "win-arm64" {
        $CMakeArchitecture = "ARM64"
        $DevCmdArchitecture = "arm64"
        $DevCmdHostArchitecture = "arm64"
        $ToolComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
        $LibraryMachine = "ARM64"
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

cmake -S (Join-Path $RepoRoot "src/ProGPU.Native") -B $BuildDir -A $CMakeArchitecture `
    -DPROGPU_NATIVE_WEBGPU_INCLUDE_DIR="$IncludeDir" `
    -DPROGPU_NATIVE_WEBGPU_LIBRARY="$ImportLibrary" `
    -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR="$DawnHeadersDir" `
    -DPROGPU_NATIVE_BUILD_SAMPLE=ON `
    -DBUILD_TESTING=ON
cmake --build $BuildDir --config Release --parallel

$NativeDll = Join-Path $BuildDir "Release/progpu_native.dll"
if (-not (Test-Path $NativeDll)) {
    throw "The native renderer DLL was not produced: $NativeDll"
}
$DawnDll = Join-Path $BuildDir "Release/progpu_native_dawn.dll"
if (-not (Test-Path $DawnDll)) {
    throw "The provider-resolved Dawn renderer DLL was not produced: $DawnDll"
}
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
$NativePdb = Join-Path $BuildDir "Release/progpu_native.pdb"
$DawnPdb = Join-Path $BuildDir "Release/progpu_native_dawn.pdb"
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

$CurrentArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
if ($CurrentArchitecture -eq $RunnableArchitecture) {
    $env:PATH = "$(Join-Path $BuildDir 'Release');$RuntimeDir;$env:PATH"
    ctest --test-dir $BuildDir -C Release --output-on-failure
    $SampleOutput = Join-Path $RepoRoot "artifacts/progpu-native/sample/progpu-native-managed-$Rid.ppm"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SampleOutput) | Out-Null
    dotnet run --project (Join-Path $RepoRoot "src/ProGPU.Native.ManagedSample/ProGPU.Native.ManagedSample.csproj") -c Release -- $SampleOutput
    dotnet run --project (Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj") -c Release -- --group-opacity --rectangles 384 --warmup 4 --iterations 8
    dotnet run --project (Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj") -c Release -- --external-images --warmup 2 --iterations 4
    dotnet run --project (Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj") -c Release -- --masked-images --warmup 2 --iterations 4
    $VectorClipScenes = @("", "--analytic", "--geometry", "--paths", "--glyphs", "--images")
    foreach ($Scene in $VectorClipScenes) {
        $SceneArgs = @()
        if ($Scene) {
            $SceneArgs += $Scene
        }
        $SceneArgs += @("--group-vector-clip-chain", "--rectangles", "96", "--warmup", "2", "--iterations", "4")
        dotnet run `
            --project (Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj") `
            -c Release -- `
            @SceneArgs
    }
    $EffectScenes = @("", "--analytic", "--geometry", "--paths", "--glyphs", "--images")
    foreach ($Effect in @("--group-gaussian-blur", "--group-drop-shadow")) {
        foreach ($Scene in $EffectScenes) {
            $SceneArgs = @()
            if ($Scene) {
                $SceneArgs += $Scene
            }
            $SceneArgs += @($Effect, "--rectangles", "96", "--warmup", "2", "--iterations", "4")
            dotnet run `
                --project (Join-Path $RepoRoot "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj") `
                -c Release -- `
                @SceneArgs
        }
    }
} else {
    Write-Host "Cross-compiled $Rid; execution is deferred to a matching-architecture CI lane."
}

Write-Host "Staged ProGPU native renderer for $Rid in $PackageStage"
