[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Rid = $(if ($env:PROGPU_NATIVE_RID) { $env:PROGPU_NATIVE_RID } else { "win-x64" }),
    [string] $NativeBinaryDirectory,
    [string] $Configuration = "Release",
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "tests/ProGPU.Direct2D.Win2D.Integration/ProGPU.Direct2D.Win2D.Integration.csproj"
$PackageName = "ProGPU.Direct2D.Win2D.Integration"
$Platform = if ($Rid -eq "win-arm64") { "ARM64" } else { "x64" }

if (-not $IsWindows) {
    throw "The genuine Win2D integration gate requires Windows."
}
if (-not $NativeBinaryDirectory) {
    $NativeBinaryDirectory = Join-Path $RepoRoot "artifacts/progpu-native/build-$Rid"
}
$Direct2DBinary = Join-Path $NativeBinaryDirectory "progpu_native_direct2d.dll"
if (-not (Test-Path $Direct2DBinary)) {
    $Direct2DBinary = Join-Path $NativeBinaryDirectory "Release/progpu_native_direct2d.dll"
}
if (-not (Test-Path $Direct2DBinary)) {
    throw "The qualified Direct2D provider was not found under '$NativeBinaryDirectory'."
}

$ExistingPackage = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($ExistingPackage) {
    $ExistingPackage | Remove-AppxPackage
}

dotnet publish $Project `
    -c $Configuration `
    -r $Rid `
    -p:Platform=$Platform `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    -p:WindowsAppSDKSelfContained=true `
    -p:ProGpuDirect2DNativeBinary=$Direct2DBinary
if ($LASTEXITCODE -ne 0) {
    throw "The packaged genuine Win2D integration application failed to build."
}

$Manifest = Get-ChildItem `
    -Path (Join-Path $RepoRoot "tests/ProGPU.Direct2D.Win2D.Integration/obj") `
    -Filter "AppxManifest.xml" `
    -Recurse |
    Where-Object { $_.FullName -match "PackageLayout" } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $Manifest) {
    throw "The Win2D integration package layout manifest was not produced."
}

Add-AppxPackage -Register $Manifest.FullName
$InstalledPackage = Get-AppxPackage -Name $PackageName
$ResultPath = Join-Path `
    $env:LOCALAPPDATA `
    "Packages/$($InstalledPackage.PackageFamilyName)/LocalState/direct2d-win2d-result.json"
if (Test-Path $ResultPath) {
    Remove-Item -LiteralPath $ResultPath -Force
}

Start-Process explorer.exe "shell:AppsFolder\$($InstalledPackage.PackageFamilyName)!App"
$Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while (-not (Test-Path $ResultPath)) {
    if ([DateTime]::UtcNow -ge $Deadline) {
        throw "The packaged genuine Win2D integration application did not produce evidence within $TimeoutSeconds seconds."
    }
    Start-Sleep -Milliseconds 250
}

$Evidence = Get-Content $ResultPath -Raw | ConvertFrom-Json
$Evidence | ConvertTo-Json -Depth 8
if ($Evidence.Status -ne "passed") {
    throw "The genuine Win2D Direct2D/Dawn integration gate failed: $($Evidence.Error)"
}

Write-Host "Qualified genuine Microsoft Win2D drawing on the ProGPU Direct2D/Dawn surface."
