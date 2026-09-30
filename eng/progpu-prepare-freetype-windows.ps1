[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Rid,
    [Parameter(Mandatory)]
    [string] $Workspace
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The Windows font dependency producer requires Windows.' }
$TargetArchitecture = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$HostArchitecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw 'The font dependency producer requires a native x64 or ARM64 tool host.' }
}
$ToolComponent = if ($Rid -eq 'win-arm64') {
    'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
} else {
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
}
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$VsInstall = (& $VsWhere -latest -products * -requires $ToolComponent -property installationPath |
    Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or -not $VsInstall) { throw 'The requested Visual Studio C tools are missing.' }
Import-Module (Join-Path $VsInstall 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $VsInstall -SkipAutomaticLocation `
    -DevCmdArguments "-arch=$TargetArchitecture -host_arch=$HostArchitecture" | Out-Null

$GpgCommand = Get-Command gpg.exe -ErrorAction SilentlyContinue
$Gpg = if ($GpgCommand) { $GpgCommand.Source } else {
    # The Git for Windows distribution provides GnuPG; do not install or edit
    # a system library, and keep the producer's GNUPGHOME task-owned.
    Join-Path $env:ProgramFiles 'Git/usr/bin/gpg.exe'
}
if (-not (Test-Path $Gpg -PathType Leaf)) { throw 'A real GnuPG executable is required.' }
if (-not (Get-Command ninja.exe -ErrorAction SilentlyContinue)) { throw 'Ninja is required.' }
python (Join-Path $PSScriptRoot 'progpu-prepare-freetype.py') `
    --workspace $Workspace --rid $Rid --cc cl --cxx cl --gpg $Gpg
if ($LASTEXITCODE -ne 0) { throw "The signed font dependency producer failed for $Rid." }
