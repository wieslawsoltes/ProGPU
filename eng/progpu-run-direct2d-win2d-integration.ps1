[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Rid = $(if ($env:PROGPU_NATIVE_RID) { $env:PROGPU_NATIVE_RID } else { "win-x64" }),
    [string] $NativeBinaryDirectory,
    [string] $Configuration = "Release",
    [int] $TimeoutSeconds = 120,
    [string] $SigningCertificateThumbprint = $(
        if ($env:PROGPU_WIN2D_SIGNING_CERTIFICATE_THUMBPRINT) {
            $env:PROGPU_WIN2D_SIGNING_CERTIFICATE_THUMBPRINT
        } else {
            ""
        }),
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "tests/ProGPU.Direct2D.Win2D.Integration/ProGPU.Direct2D.Win2D.Integration.csproj"
$PackageName = "ProGPU.Direct2D.Win2D.Integration"
$ResultFileName = "direct2d-win2d-result.json"
$ProgressFileName = "direct2d-win2d-progress.txt"
$Platform = if ($Rid -eq "win-arm64") { "ARM64" } else { "x64" }

$RunningOnWindows =
    [System.Environment]::OSVersion.Platform -eq
        [System.PlatformID]::Win32NT
if (-not $RunningOnWindows) {
    throw "The genuine Win2D integration gate requires Windows."
}
$SigningCertificateThumbprint =
    ($SigningCertificateThumbprint -replace '\s', '').ToUpperInvariant()
if (-not $SigningCertificateThumbprint) {
    throw "Set -SigningCertificateThumbprint or PROGPU_WIN2D_SIGNING_CERTIFICATE_THUMBPRINT to a pre-provisioned CN=ProGPU package-signing certificate."
}
$SigningCertificate = Get-Item `
    -LiteralPath ("Cert:\CurrentUser\My\" + $SigningCertificateThumbprint) `
    -ErrorAction SilentlyContinue
if (-not $SigningCertificate -or -not $SigningCertificate.HasPrivateKey) {
    throw "The pre-provisioned package-signing certificate '$SigningCertificateThumbprint' is missing from CurrentUser/My or has no private key."
}
if ($SigningCertificate.Subject -ne "CN=ProGPU") {
    throw "The package-signing certificate subject must exactly match the package publisher CN=ProGPU."
}
$TrustedSigningCertificate = @(
    Get-Item `
        -LiteralPath ("Cert:\CurrentUser\Root\" + $SigningCertificateThumbprint) `
        -ErrorAction SilentlyContinue
    Get-Item `
        -LiteralPath ("Cert:\LocalMachine\Root\" + $SigningCertificateThumbprint) `
        -ErrorAction SilentlyContinue
) | Select-Object -First 1
if (-not $TrustedSigningCertificate) {
    throw "The package-signing certificate '$SigningCertificateThumbprint' is not pre-trusted in CurrentUser/Root or LocalMachine/Root. Provision trust outside the gate."
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

if (-not $SkipBuild) {
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
}

$Package = Get-ChildItem `
    -Path (Join-Path $RepoRoot "tests/ProGPU.Direct2D.Win2D.Integration/AppPackages") `
    -Filter "*.msix" `
    -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $Package) {
    throw "The Win2D integration MSIX package was not produced."
}

$SignTool = Get-ChildItem `
    -Path (Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin") `
    -Filter "signtool.exe" `
    -Recurse |
    Where-Object { $_.FullName -match "\\$Platform\\signtool\.exe$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $SignTool) {
    throw "The $Platform Windows SDK signtool.exe was not found."
}

$TemporaryDirectory = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("progpu-win2d-signing-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $TemporaryDirectory | Out-Null
$SignedPackagePath = Join-Path $TemporaryDirectory "integration.msix"
try {
    [System.IO.File]::Copy(
        "\\?\" + $Package.FullName,
        $SignedPackagePath,
        $true)
    & $SignTool.FullName sign `
        /fd SHA256 `
        /sha1 $SigningCertificateThumbprint `
        /s My `
        $SignedPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signing the genuine Win2D integration package failed."
    }

    Add-AppxPackage -Path $SignedPackagePath
    $InstalledPackage = Get-AppxPackage -Name $PackageName
    $ResultPath = Join-Path `
        $env:LOCALAPPDATA `
        "Packages/$($InstalledPackage.PackageFamilyName)/LocalState/$ResultFileName"
    $FallbackResultPath = Join-Path `
        $env:LOCALAPPDATA `
        "$PackageName/$ResultFileName"
    $ProgressPath = Join-Path `
        $env:LOCALAPPDATA `
        "$PackageName/$ProgressFileName"
    foreach ($ExistingResultPath in @($ResultPath, $FallbackResultPath)) {
        if (Test-Path $ExistingResultPath) {
            Remove-Item -LiteralPath $ExistingResultPath -Force
        }
    }
    if (Test-Path $ProgressPath) {
        Remove-Item -LiteralPath $ProgressPath -Force
    }

    Start-Process explorer.exe "shell:AppsFolder\$($InstalledPackage.PackageFamilyName)!App"
    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path $ResultPath) -and
           -not (Test-Path $FallbackResultPath)) {
        if ([DateTime]::UtcNow -ge $Deadline) {
            $LastStage = if (Test-Path $ProgressPath) {
                Get-Content $ProgressPath -Raw
            } else {
                "not-started"
            }
            throw "The packaged genuine Win2D integration application did not produce evidence within $TimeoutSeconds seconds; last stage: $LastStage."
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not (Test-Path $ResultPath)) {
        $ResultPath = $FallbackResultPath
    }

    $Evidence = Get-Content $ResultPath -Raw | ConvertFrom-Json
    $Evidence | ConvertTo-Json -Depth 8
    if ($Evidence.Status -ne "passed") {
        throw "The genuine Win2D Direct2D/Dawn integration gate failed: $($Evidence.Error)"
    }

    Write-Host "Qualified genuine Microsoft Win2D drawing on the ProGPU Direct2D/Dawn surface."
} finally {
    Get-Process -Name $PackageName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue
    Remove-Item `
        -LiteralPath $TemporaryDirectory `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
}
