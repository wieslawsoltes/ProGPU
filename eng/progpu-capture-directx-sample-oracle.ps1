[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string] $Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [string] $OutputDirectory,
    [string] $GitPath,
    [ValidateSet("HelloTriangle", "HelloTexture")]
    [string] $Sample = "HelloTriangle",
    [switch] $UseWarp
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$LockPath = Join-Path $PSScriptRoot "directx-graphics-samples.lock.json"
$Lock = Get-Content $LockPath -Raw | ConvertFrom-Json
$SampleConfig = if ($Sample -eq "HelloTriangle") { $Lock } else { $Lock.helloTexture }
$SampleClass = "D3D12$Sample"
$PatchPath = Join-Path $PSScriptRoot "directx-graphics-samples/$SampleClass-oracle.patch"
$OracleStem = if ($Sample -eq "HelloTriangle") {
    "microsoft-d3d12-hello-triangle"
} else {
    "microsoft-d3d12-hello-texture"
}
$CacheRoot = Join-Path $RepoRoot "artifacts/directx-graphics-samples"
$SourceDirectory = Join-Path $CacheRoot "source"
$InstrumentedDirectory = Join-Path $CacheRoot "instrumented"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $RepoRoot "artifacts/progpu-native/directx-oracle/windows-native"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$OraclePath = Join-Path $OutputDirectory "$OracleStem.ppm"

$RunningOnWindows = if (Get-Variable IsWindows -ErrorAction SilentlyContinue) {
    $IsWindows
} else {
    [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}
if (-not $RunningOnWindows) {
    throw "The Microsoft DirectX sample oracle can only run on Windows."
}
if (-not $GitPath) {
    $GitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $GitCommand) {
        throw "Git was not found. Add it to PATH or pass -GitPath."
    }
    $GitPath = $GitCommand.Source
}
if (-not (Test-Path $GitPath)) {
    throw "The configured Git executable does not exist: $GitPath"
}
$CreatedSourceCache = $false
if (-not (Test-Path (Join-Path $SourceDirectory ".git"))) {
    New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
    & $GitPath clone --filter=blob:none --no-checkout $Lock.repository $SourceDirectory
    if ($LASTEXITCODE -ne 0) { throw "DirectX sample clone failed." }
    & $GitPath -C $SourceDirectory sparse-checkout init --cone
    & $GitPath -C $SourceDirectory sparse-checkout set "Samples/Desktop/D3D12HelloWorld"
    $CreatedSourceCache = $true
}
if (-not $CreatedSourceCache -and
    (& $GitPath -C $SourceDirectory status --porcelain --untracked-files=no)) {
    throw "Refusing to change the modified DirectX sample source cache."
}
& $GitPath -C $SourceDirectory fetch --depth 1 origin $Lock.commit
& $GitPath -C $SourceDirectory checkout --detach $Lock.commit
if ($LASTEXITCODE -ne 0 -or
    (& $GitPath -C $SourceDirectory rev-parse HEAD).Trim() -ne $Lock.commit) {
    throw "The pinned DirectX sample checkout failed."
}

function Get-NormalizedSha256([string] $Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $normalized = [System.Collections.Generic.List[byte]]::new($bytes.Length)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        if ($bytes[$index] -eq 13 -and $index + 1 -lt $bytes.Length -and
            $bytes[$index + 1] -eq 10) {
            continue
        }
        $normalized.Add($bytes[$index])
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($normalized.ToArray())
        return ([BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

foreach ($entry in $SampleConfig.files.PSObject.Properties) {
    $path = Join-Path $SourceDirectory $entry.Name
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value -and
        (Get-NormalizedSha256 $path) -ne $entry.Value) {
        throw "DirectX sample source hash mismatch for '$($entry.Name)': $actual."
    }
}
$PackagePath = Join-Path $SourceDirectory "$($SampleConfig.sample)/packages.config"
$PackageContract = "id=`"$($Lock.agilityPackage)`" version=`"$($Lock.agilityVersion)`""
if (-not ((Get-Content $PackagePath -Raw).Contains($PackageContract))) {
    throw "The pinned DirectX sample does not use $PackageContract."
}

if (Test-Path $InstrumentedDirectory) {
    $resolvedCache = [System.IO.Path]::GetFullPath($CacheRoot) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedInstrumented = [System.IO.Path]::GetFullPath($InstrumentedDirectory)
    if (-not $resolvedInstrumented.StartsWith($resolvedCache, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an instrumented checkout outside the oracle cache."
    }
    & $GitPath -C $SourceDirectory worktree remove --force $InstrumentedDirectory 2>$null
    if (Test-Path $InstrumentedDirectory) {
        Remove-Item -LiteralPath $InstrumentedDirectory -Recurse -Force
    }
}
& $GitPath -C $SourceDirectory worktree prune
& $GitPath -C $SourceDirectory worktree add --detach $InstrumentedDirectory $Lock.commit
if ($LASTEXITCODE -ne 0) { throw "Could not create the oracle worktree." }
& $GitPath -C $InstrumentedDirectory apply --check $PatchPath
if ($LASTEXITCODE -ne 0) { throw "The oracle patch no longer applies." }
& $GitPath -C $InstrumentedDirectory apply $PatchPath
if ($LASTEXITCODE -ne 0) { throw "The oracle patch failed." }

$ToolComponent = if ($Platform -eq "ARM64") {
    "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
} else {
    "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
}
$VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
$VsInstall = (& $VsWhere -latest -products * -requires $ToolComponent -property installationPath |
    Select-Object -First 1)
if (-not $VsInstall) { throw "Visual Studio C++ build tools were not found." }
Import-Module (Join-Path $VsInstall "Common7/Tools/Microsoft.VisualStudio.DevShell.dll")
$Architecture = if ($Platform -eq "ARM64") { "arm64" } else { "x64" }
Enter-VsDevShell -VsInstallPath $VsInstall -SkipAutomaticLocation `
    -DevCmdArguments "-arch=$Architecture -host_arch=$Architecture" | Out-Null

$Project = Join-Path $InstrumentedDirectory "$($SampleConfig.sample)/$SampleClass.vcxproj"
$Packages = Join-Path (
    Split-Path -Parent (Split-Path -Parent $Project)) "packages"
msbuild.exe $Project /m:1 /t:Restore /p:RestorePackagesConfig=true `
    "/p:RestorePackagesPath=$Packages" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "DirectX sample package restore failed." }
$ExpectedAgilityProps = Join-Path $Packages (
    "$($Lock.agilityPackage).$($Lock.agilityVersion)/build/native/" +
    "Microsoft.Direct3D.D3D12.props")
if (-not (Test-Path $ExpectedAgilityProps)) {
    # Project-only packages.config restore uses the project directory as its
    # synthetic solution root. The upstream vcxproj intentionally imports from
    # ../packages, so mirror the restored packages into that expected root.
    $ProjectPackages = Join-Path (Split-Path -Parent $Project) "packages"
    if (-not (Test-Path $ProjectPackages)) {
        throw "The DirectX sample restore did not publish its package folder."
    }
    New-Item -ItemType Directory -Force -Path $Packages | Out-Null
    Copy-Item (Join-Path $ProjectPackages "*") $Packages -Recurse -Force
}
msbuild.exe $Project /m:1 /t:Build /p:Configuration=Release `
    "/p:Platform=$Platform" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "DirectX sample build failed." }

$Executable = Join-Path (Split-Path -Parent $Project) "bin/$Platform/Release/$SampleClass.exe"
if (-not (Test-Path $Executable)) {
    throw "The DirectX sample executable was not produced: $Executable"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Remove-Item -LiteralPath $OraclePath -Force -ErrorAction SilentlyContinue
$env:PROGPU_DIRECTX_ORACLE_OUTPUT = $OraclePath
try {
    $RunArguments = if ($UseWarp) { @("-warp") } else { @() }
    $SampleProcess = Start-Process `
        -FilePath $Executable `
        -ArgumentList $RunArguments `
        -Wait `
        -PassThru
    if ($SampleProcess.ExitCode -ne 0) {
        $ErrorPath = "$OraclePath.error.txt"
        $Failure = if (Test-Path $ErrorPath) {
            (Get-Content $ErrorPath -Raw).Trim()
        } else {
            "No native diagnostic was published."
        }
        throw "The DirectX sample exited with $($SampleProcess.ExitCode). $Failure"
    }
} finally {
    Remove-Item Env:PROGPU_DIRECTX_ORACLE_OUTPUT -ErrorAction SilentlyContinue
}
if (-not (Test-Path $OraclePath)) {
    throw "The DirectX sample did not publish its native frame."
}
$ExpectedLength = 16 + 1280 * 720 * 3
if ((Get-Item $OraclePath).Length -ne $ExpectedLength) {
    throw "The DirectX sample PPM has an unexpected length."
}
$ImageHash = (Get-FileHash $OraclePath -Algorithm SHA256).Hash
$Contract = [ordered]@{
    Contract = "Microsoft.DirectX-Graphics-Samples/$SampleClass"
    Repository = $Lock.repository
    Commit = $Lock.commit
    Sample = $SampleConfig.sample
    AgilityPackage = $Lock.agilityPackage
    AgilityVersion = $Lock.agilityVersion
    Platform = $Platform
    Adapter = $(if ($UseWarp) { "WARP" } else { "Default hardware adapter" })
    Image = $OraclePath
    ImageSha256 = $ImageHash
}
$ContractPath = Join-Path $OutputDirectory "$OracleStem.json"
$Contract | ConvertTo-Json -Depth 4 | Set-Content $ContractPath -Encoding utf8
Write-Output "Captured pinned Microsoft $SampleClass oracle: $OraclePath"
Write-Output "Agility SDK: $($Lock.agilityVersion); image SHA-256: $ImageHash"
