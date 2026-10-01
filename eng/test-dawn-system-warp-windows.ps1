#requires -Version 7.2
[CmdletBinding(DefaultParameterSetName='Source')]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Rid,
    [Parameter(Mandatory,ParameterSetName='Package')][string]$PackageSource,
    [Parameter(Mandatory,ParameterSetName='Package')][string]$PackageVersion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -ine $architecture) {
    throw 'Use the target-native Windows runner and PowerShell process.'
}
$component = if ($Rid -eq 'win-arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products * -requires $component -property installationPath | Select-Object -First 1
if (-not $installation) { throw 'Matching Visual Studio C++ tools were not found.' }
Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments "-arch=$architecture -host_arch=$architecture" | Out-Null
$repo = Split-Path $PSScriptRoot -Parent
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('progpu-dawn-warp.' + [Guid]::NewGuid().ToString('N'))
$packageMode = $PSCmdlet.ParameterSetName -eq 'Package'
$publicationArguments = @()
$payload = $null
if ($packageMode) {
    if (-not [IO.Path]::IsPathFullyQualified($PackageSource) -or -not (Test-Path -LiteralPath $PackageSource -PathType Container)) {
        throw 'Package qualification requires an absolute existing package source.'
    }
    $publicationArguments = @('-p:ProGpuDawnUsePackage=true', "-p:ProGpuDawnPackageSource=$PackageSource", "-p:ProGpuDawnPackageVersion=$PackageVersion")
} else {
    $build = Join-Path $workspace 'original-build'
    $payload = Join-Path $workspace "payload/$Rid/native"
    & (Join-Path $PSScriptRoot 'build-dawn-system-warp-windows.ps1') -Rid $Rid -BuildDirectory $build -OutputDirectory $payload
}
$evidence = Join-Path $repo "artifacts/dawn-system-warp/$Rid"
if (Test-Path -LiteralPath $evidence) { throw 'Evidence must be fresh.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
if (-not $packageMode) {
    Copy-Item -LiteralPath (Join-Path $payload 'progpu-dawn-system-warp.json') -Destination $evidence
}
foreach ($kind in @('jit','aot')) {
    $publish = Join-Path $workspace "consumer-$kind"
    $aot = if ($kind -eq 'aot') { 'true' } else { 'false' }
    dotnet publish (Join-Path $repo 'tests/ProGPU.DawnSystemWarp.Conformance/ProGPU.DawnSystemWarp.Conformance.csproj') -c Release -r $Rid --self-contained true "-p:PublishAot=$aot" -o $publish @publicationArguments
    if ($LASTEXITCODE -ne 0) { throw "The $kind consumer did not publish." }
    foreach ($name in @('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt')) {
        $destination = Join-Path $publish $name
        if ($packageMode) {
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "NuGet did not deliver $name for $kind." }
        } else {
            if (Test-Path -LiteralPath $destination) { throw "Refusing to replace consumer asset: $name" }
            Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $destination
        }
    }
    foreach ($control in @('readback','foreign-resolver','device-loss','request-cancellation','callback-fault','adapter-abandonment')) {
        $stdout = Join-Path $evidence "$kind-$control-stdout.log"
        $stderr = Join-Path $evidence "$kind-$control-stderr.log"
        $arguments = @($architecture)
        $expected = 'Dawn system WARP conformance passed: 2 device lifetimes, 4 full RGBA readbacks, 0 skipped.'
        if ($control -eq 'foreign-resolver') {
            $arguments += '--foreign-resolver'
            $expected = 'Dawn system WARP foreign resolver rejected before native imports.'
        } elseif ($control -eq 'device-loss') {
            $arguments += '--device-loss'
            $expected = 'Dawn system WARP native device loss and independent replacement readback passed.'
        } elseif ($control -eq 'request-cancellation') {
            $arguments += '--request-cancellation'
            $expected = 'Dawn system WARP native adapter and device request cancellation retired exactly once.'
        } elseif ($control -eq 'callback-fault') {
            $arguments += '--callback-fault'
            $expected = 'Dawn system WARP throwing loss subscriber and diagnostic writer preserved native retirement.'
        } elseif ($control -eq 'adapter-abandonment') {
            $arguments += '--adapter-abandonment'
            $expected = 'Dawn ordinary adapter request abandonment retired its native result and userdata exactly once.'
        }
        $process = Start-Process -FilePath (Join-Path $publish 'ProGPU.DawnSystemWarp.Conformance.exe') -ArgumentList $arguments -WorkingDirectory $publish -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        try {
            if (-not $process.WaitForExit(120000)) { throw "The $kind/$control WARP consumer exceeded its 120-second process bound." }
            if ($process.ExitCode -ne 0) { throw "The $kind/$control WARP consumer failed: $($process.ExitCode). See $stderr" }
            if (-not (Select-String -LiteralPath $stdout -SimpleMatch $expected -Quiet)) {
                throw "The $kind/$control consumer did not execute every required check."
            }
        } finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            $process.Dispose()
        }
    }
}
if (-not $packageMode) {
    # Publish only after both executable modes passed, retaining the RID directory
    # so multiple producer artifacts can be merged without filename collisions.
    $artifact = Join-Path $repo "artifacts/dawn-system-warp/payload/$Rid/native"
    if (Test-Path -LiteralPath $artifact) { throw 'Companion artifact publication must be fresh.' }
    New-Item -ItemType Directory -Path $artifact | Out-Null
    foreach ($name in @('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $artifact
    }
}
