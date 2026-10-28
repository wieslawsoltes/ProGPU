$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'Arm64') { throw 'Native ARM64 host required.' }
$Output=[IO.Path]::GetFullPath($env:CAPTURE_OUTPUT)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Zip=Join-Path $Output 'Procdump.zip'
Invoke-WebRequest 'https://download.sysinternals.com/files/Procdump.zip' -OutFile $Zip
if ((Get-FileHash $Zip).Hash -ne '5fe68e81632f13188930affff20a9a9910170bae654360850d3108cead708a2d') { throw 'ProcDump archive drift.' }
Expand-Archive -LiteralPath $Zip -DestinationPath (Join-Path $Output 'procdump')
$ProcDump=Join-Path $Output 'procdump/procdump64a.exe'
if ((Get-FileHash $ProcDump).Hash -ne '3448555503526053bd819c760ad7b40368e130db8348f535f5212e12e9ab2ea5') { throw 'ProcDump ARM64 drift.' }
function Assert-MicrosoftSignature([string]$Path) {
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Invalid Microsoft signature: $Path" }
}
Assert-MicrosoftSignature $ProcDump
$Kits=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$Cdb=Join-Path $Kits 'Debuggers/arm64/cdb.exe'
if (-not (Test-Path $Cdb)) {
    # Disposable runner only: add the debugger component from its installed SDK.
    $Sdk=Get-ItemProperty 'HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        Where-Object { $_.PSObject.Properties.Name -contains 'DisplayName' -and $_.DisplayName -match 'Windows Software Development Kit' -and $_.PSObject.Properties.Name -contains 'BundleCachePath' } |
        Sort-Object DisplayVersion -Descending | Select-Object -First 1
    if (-not $Sdk -or -not (Test-Path $Sdk.BundleCachePath)) { throw 'ARM64 debugger unavailable; product has not run.' }
    Assert-MicrosoftSignature $Sdk.BundleCachePath
    $Installer=Start-Process -FilePath $Sdk.BundleCachePath -ArgumentList '/features OptionId.WindowsDesktopDebuggers /q /norestart' -PassThru
    if (-not $Installer.WaitForExit(120000)) { $Installer.Kill($true); throw 'Debugger setup exceeded its preparation bound.' }
    if ($Installer.ExitCode -ne 0) { throw "Debugger setup failed: $($Installer.ExitCode)" }
}
if (-not (Test-Path $Cdb)) { throw 'ARM64 CDB required before product execution.' }
Assert-MicrosoftSignature $Cdb
@{procDump=$ProcDump;cdb=$Cdb;cdbSha256=(Get-FileHash $Cdb).Hash.ToLowerInvariant();cdbVersion=(Get-Item $Cdb).VersionInfo.FileVersion;procDumpVersion=(Get-Item $ProcDump).VersionInfo.FileVersion} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'tools.json') -Encoding utf8
$VsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$Vs=(& $VsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath | Select-Object -First 1)
if (-not $Vs) { throw 'ARM64 compiler required for the CPU control.' }
Import-Module (Join-Path $Vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $Vs -SkipAutomaticLocation -DevCmdArguments '-arch=arm64 -host_arch=arm64' | Out-Null
Push-Location $Output
try {
    & cl.exe /nologo /Zi /Od "/Fe:$Output/CrashControl.exe" "$PSScriptRoot/CrashControl.cpp" /link /DEBUG
    if ($LASTEXITCODE -ne 0) { throw 'CPU capture control failed to compile.' }
} finally { Pop-Location }
