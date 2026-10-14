param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$install) { throw 'Native ARM64 compiler installation is unavailable.' }
Import-Module (Join-Path $install 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $install -SkipAutomaticLocation -DevCmdArguments '-arch=arm64 -host_arch=arm64' | Out-Null
Set-Location $Root
foreach ($name in @('wgpu_native', 'progpu_native')) {
    $exports = & dumpbin.exe /nologo /exports ($name + '.dll') | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z0-9_@?$]*)') { $Matches[1] }
    } | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0 -or $exports.Count -lt 100) { throw 'Incomplete original DLL export table.' }
    [string[]]$definition = @("LIBRARY $name", 'EXPORTS')
    $definition += $exports
    [IO.File]::WriteAllLines((Join-Path $Root ($name + '.def')), $definition)
    & lib.exe /nologo ('/def:' + $name + '.def') /machine:ARM64 ('/out:' + $name + '.lib')
    if ($LASTEXITCODE -ne 0) { throw 'Import library generation failed.' }
}
$compiler = (Get-Command clang-cl.exe -ErrorAction Stop).Source
& $compiler --target=aarch64-pc-windows-msvc /nologo /std:c++20 /O2 /MD /EHsc /I. replay-warp.cpp progpu_native.lib wgpu_native.lib /Fe:replay-warp.exe
if ($LASTEXITCODE -ne 0) { throw 'Original source probe compilation failed.' }
& dumpbin.exe /nologo /headers replay-warp.exe | Select-String 'machine \('
