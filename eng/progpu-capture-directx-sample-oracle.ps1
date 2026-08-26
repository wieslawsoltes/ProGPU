[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string] $Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }),
    [string] $OutputDirectory,
    [switch] $UseWarp
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$LockPath = Join-Path $PSScriptRoot "directx-graphics-samples.lock.json"
$PatchPath = Join-Path $PSScriptRoot "directx-graphics-samples/D3D12HelloTriangle-oracle.patch"
$Lock = Get-Content $LockPath -Raw | ConvertFrom-Json
$CacheRoot = Join-Path $RepoRoot "artifacts/directx-graphics-samples"
$SourceDirectory = Join-Path $CacheRoot "source"
$InstrumentedDirectory = Join-Path $CacheRoot "instrumented"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $RepoRoot "artifacts/progpu-native/directx-oracle/windows-native"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$OraclePath = Join-Path $OutputDirectory "microsoft-d3d12-hello-triangle.ppm"

if (-not $IsWindows) {
    throw "The Microsoft DirectX sample oracle can only run on Windows."
}
if (-not (Test-Path (Join-Path $SourceDirectory ".git"))) {
    New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
    git clone --filter=blob:none --no-checkout $Lock.repository $SourceDirectory
    if ($LASTEXITCODE -ne 0) { throw "DirectX sample clone failed." }
    git -C $SourceDirectory sparse-checkout init --cone
    git -C $SourceDirectory sparse-checkout set "Samples/Desktop/D3D12HelloWorld"
}
if (git -C $SourceDirectory status --porcelain --untracked-files=no) {
    throw "Refusing to change the modified DirectX sample source cache."
}
git -C $SourceDirectory fetch --depth 1 origin $Lock.commit
git -C $SourceDirectory checkout --detach $Lock.commit
if ($LASTEXITCODE -ne 0 -or
    (git -C $SourceDirectory rev-parse HEAD).Trim() -ne $Lock.commit) {
    throw "The pinned DirectX sample checkout failed."
}

foreach ($entry in $Lock.files.PSObject.Properties) {
    $path = Join-Path $SourceDirectory $entry.Name
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) {
        throw "DirectX sample source hash mismatch for '$($entry.Name)': $actual."
    }
}
$PackagePath = Join-Path $SourceDirectory "$($Lock.sample)/packages.config"
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
    git -C $SourceDirectory worktree remove --force $InstrumentedDirectory 2>$null
    if (Test-Path $InstrumentedDirectory) {
        Remove-Item -LiteralPath $InstrumentedDirectory -Recurse -Force
    }
}
git -C $SourceDirectory worktree prune
git -C $SourceDirectory worktree add --detach $InstrumentedDirectory $Lock.commit
if ($LASTEXITCODE -ne 0) { throw "Could not create the oracle worktree." }
git -C $InstrumentedDirectory apply --check $PatchPath
if ($LASTEXITCODE -ne 0) { throw "The oracle patch no longer applies." }
git -C $InstrumentedDirectory apply $PatchPath
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

$Project = Join-Path $InstrumentedDirectory "$($Lock.sample)/D3D12HelloTriangle.vcxproj"
$Packages = Join-Path (Split-Path -Parent $Project) "../packages"
msbuild.exe $Project /m:1 /t:Restore /p:RestorePackagesConfig=true `
    "/p:RestorePackagesPath=$Packages" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "DirectX sample package restore failed." }
msbuild.exe $Project /m:1 /t:Build /p:Configuration=Release `
    "/p:Platform=$Platform" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "DirectX sample build failed." }

$Executable = Join-Path (Split-Path -Parent $Project) "bin/$Platform/Release/D3D12HelloTriangle.exe"
if (-not (Test-Path $Executable)) {
    throw "The DirectX sample executable was not produced: $Executable"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Remove-Item -LiteralPath $OraclePath -Force -ErrorAction SilentlyContinue
$env:PROGPU_DIRECTX_ORACLE_OUTPUT = $OraclePath
try {
    $RunArguments = if ($UseWarp) { @("-warp") } else { @() }
    & $Executable @RunArguments
    if ($LASTEXITCODE -ne 0) { throw "The DirectX sample exited with $LASTEXITCODE." }
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
    Contract = "Microsoft.DirectX-Graphics-Samples/D3D12HelloTriangle"
    Repository = $Lock.repository
    Commit = $Lock.commit
    Sample = $Lock.sample
    AgilityPackage = $Lock.agilityPackage
    AgilityVersion = $Lock.agilityVersion
    Platform = $Platform
    Adapter = $(if ($UseWarp) { "WARP" } else { "Default hardware adapter" })
    Image = $OraclePath
    ImageSha256 = $ImageHash
}
$ContractPath = Join-Path $OutputDirectory "microsoft-d3d12-hello-triangle.json"
$Contract | ConvertTo-Json -Depth 4 | Set-Content $ContractPath -Encoding utf8
Write-Output "Captured pinned Microsoft D3D12HelloTriangle oracle: $OraclePath"
Write-Output "Agility SDK: $($Lock.agilityVersion); SHA-256: $ImageHash"
