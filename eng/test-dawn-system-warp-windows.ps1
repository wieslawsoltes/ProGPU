#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Rid)
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
$build = Join-Path $workspace 'original-build'
$payload = Join-Path $workspace "payload/$Rid/native"
& (Join-Path $PSScriptRoot 'build-dawn-system-warp-windows.ps1') -Rid $Rid -BuildDirectory $build -OutputDirectory $payload
$evidence = Join-Path $repo "artifacts/dawn-system-warp/$Rid"
if (Test-Path -LiteralPath $evidence) { throw 'Evidence must be fresh.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath (Join-Path $payload 'progpu-dawn-system-warp.json') -Destination $evidence
foreach ($kind in @('jit','aot')) {
    $publish = Join-Path $workspace "consumer-$kind"
    $aot = if ($kind -eq 'aot') { 'true' } else { 'false' }
    dotnet publish (Join-Path $repo 'tests/ProGPU.DawnSystemWarp.Conformance/ProGPU.DawnSystemWarp.Conformance.csproj') -c Release -r $Rid --self-contained true "-p:PublishAot=$aot" -o $publish
    if ($LASTEXITCODE -ne 0) { throw "The $kind consumer did not publish." }
    foreach ($name in @('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt')) {
        $destination = Join-Path $publish $name
        if (Test-Path -LiteralPath $destination) { throw "Refusing to replace consumer asset: $name" }
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $destination
    }
    $stdout = Join-Path $evidence "$kind-stdout.log"
    $stderr = Join-Path $evidence "$kind-stderr.log"
    $process = Start-Process -FilePath (Join-Path $publish 'ProGPU.DawnSystemWarp.Conformance.exe') -ArgumentList $architecture -WorkingDirectory $publish -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (-not $process.WaitForExit(120000)) { throw "The $kind WARP consumer exceeded its 120-second process bound." }
        if ($process.ExitCode -ne 0) { throw "The $kind WARP consumer failed: $($process.ExitCode). See $stderr" }
        if (-not (Select-String -LiteralPath $stdout -SimpleMatch 'Dawn system WARP conformance passed: 2 device lifetimes, 4 full RGBA readbacks, 0 skipped.' -Quiet)) {
            throw "The $kind consumer did not execute every readback."
        }
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.Dispose()
    }
}
