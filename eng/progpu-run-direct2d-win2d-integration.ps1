[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string] $Rid = $(if ($env:PROGPU_NATIVE_RID) { $env:PROGPU_NATIVE_RID } else { "win-x64" }),
    [string] $NativeBinaryDirectory,
    [string] $Configuration = "Release",
    [int] $TimeoutSeconds = 120,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot "tests/ProGPU.Direct2D.Win2D.Integration/ProGPU.Direct2D.Win2D.Integration.csproj"
$PackageName = "ProGPU.Direct2D.Win2D.Integration"
$Platform = if ($Rid -eq "win-arm64") { "ARM64" } else { "x64" }

$RunningOnWindows =
    [System.Environment]::OSVersion.Platform -eq
        [System.PlatformID]::Win32NT
if (-not $RunningOnWindows) {
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
    Where-Object { $_.FullName -match "\\arm64\\signtool\.exe$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $SignTool) {
    throw "The ARM64 Windows SDK signtool.exe was not found."
}

$Certificate = $null
$TrustedCertificate = $null
$TemporaryDirectory = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("progpu-win2d-signing-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $TemporaryDirectory | Out-Null
$PfxPath = Join-Path $TemporaryDirectory "progpu-win2d-test.pfx"
$CertificatePath = Join-Path $TemporaryDirectory "progpu-win2d-test.cer"
$SignedPackagePath = Join-Path $TemporaryDirectory "integration.msix"
$Password = [Guid]::NewGuid().ToString("N")
$SecurePassword = ConvertTo-SecureString $Password -AsPlainText -Force
try {
    [System.IO.File]::Copy(
        "\\?\" + $Package.FullName,
        $SignedPackagePath,
        $true)
    $Certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject "CN=ProGPU" `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -KeySpec Signature `
        -HashAlgorithm SHA256 `
        -NotAfter ([DateTime]::Now.AddDays(1)) `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    Export-PfxCertificate `
        -Cert $Certificate `
        -FilePath $PfxPath `
        -Password $SecurePassword | Out-Null
    Export-Certificate `
        -Cert $Certificate `
        -FilePath $CertificatePath | Out-Null
    $TrustedCertificate = Import-Certificate `
        -FilePath $CertificatePath `
        -CertStoreLocation "Cert:\CurrentUser\Root"

    & $SignTool.FullName sign `
        /fd SHA256 `
        /f $PfxPath `
        /p $Password `
        $SignedPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "Signing the genuine Win2D integration package failed."
    }

    Add-AppxPackage -Path $SignedPackagePath
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
} finally {
    Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction SilentlyContinue
    if ($TrustedCertificate) {
        Remove-Item `
            -LiteralPath ("Cert:\CurrentUser\Root\" + $TrustedCertificate.Thumbprint) `
            -Force `
            -ErrorAction SilentlyContinue
    }
    if ($Certificate) {
        Remove-Item `
            -LiteralPath ("Cert:\CurrentUser\My\" + $Certificate.Thumbprint) `
            -Force `
            -ErrorAction SilentlyContinue
    }
    Remove-Item `
        -LiteralPath $TemporaryDirectory `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue
}
