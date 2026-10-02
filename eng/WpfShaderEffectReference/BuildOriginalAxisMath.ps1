[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('X64', 'Arm64')]
    [string] $Architecture,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $SourceCommit,
    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The original SDK companion requires Windows.' }
if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture) {
    throw 'The original SDK companion requires a native, matching-architecture tool host.'
}
$Repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ActualCommit = & git -C $Repository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $ActualCommit.Trim() -cne $SourceCommit) { throw 'Reference source identity changed.' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) { throw 'Build the original managed reference first.' }
$Destination = Join-Path $OutputDirectory 'OriginalShaderAxisMath.dll'
$Provenance = Join-Path $OutputDirectory 'OriginalShaderAxisMath.provenance.json'
if ((Test-Path -LiteralPath $Destination) -or (Test-Path -LiteralPath $Provenance)) {
    throw 'Refusing to replace an existing SDK companion or provenance receipt.'
}
$BuildDirectory = Join-Path $Repository "artifacts/wpf-axis-sdk-$Architecture"
if (Test-Path -LiteralPath $BuildDirectory) { throw 'SDK companion build directory must be new.' }
New-Item -ItemType Directory -Path $BuildDirectory | Out-Null

$Target = $Architecture.ToLowerInvariant()
$Component = if ($Architecture -ceq 'Arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$Installations = & $VsWhere -latest -products * -requires $Component -property installationPath
$DiscoveryExit = $LASTEXITCODE
$Installation = $Installations | Select-Object -First 1
if ($DiscoveryExit -ne 0 -or -not $Installation -or -not (Test-Path -LiteralPath $Installation -PathType Container)) {
    throw 'The requested installed Microsoft C++ toolchain is unavailable.'
}
Import-Module (Join-Path $Installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $Installation -SkipAutomaticLocation -DevCmdArguments "-arch=$Target -host_arch=$Target" | Out-Null
$Compiler = (Get-Command cl.exe -CommandType Application).Source
$Source = Join-Path $PSScriptRoot 'OriginalShaderAxisMath.cpp'
$Library = Join-Path $BuildDirectory 'OriginalShaderAxisMath.dll'
$Dependencies = Join-Path $BuildDirectory 'dependencies.json'
$Arguments = @('/nologo', '/std:c++20', '/permissive-', '/W4', '/WX', '/O2', '/fp:strict', '/EHsc', '/MD', '/LD',
    '/sourceDependencies', $Dependencies, "/Fo$(Join-Path $BuildDirectory 'OriginalShaderAxisMath.obj')",
    "/Fe$Library", $Source, '/link', "/IMPLIB:$(Join-Path $BuildDirectory 'OriginalShaderAxisMath.lib')")
Push-Location $BuildDirectory
try {
    & $Compiler @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Original SDK companion compilation failed.' }
} finally { Pop-Location }

function Get-Identity([string] $Path) {
    $Item = Get-Item -LiteralPath $Path
    [ordered]@{ Path = $Item.FullName; Bytes = $Item.Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

# Inspect the produced PE, not the compiler path or an archive directory name.
$Stream = [System.IO.File]::OpenRead($Library)
$Reader = [System.IO.BinaryReader]::new($Stream)
try {
    if ($Stream.Length -lt 64 -or $Reader.ReadUInt16() -ne 0x5A4D) { throw 'SDK companion is not a PE image.' }
    $Stream.Position = 0x3C
    $Header = $Reader.ReadInt32()
    if ($Header -lt 64 -or $Header -gt $Stream.Length - 24) { throw 'SDK companion has an invalid PE offset.' }
    $Stream.Position = $Header
    if ($Reader.ReadUInt32() -ne 0x4550) { throw 'SDK companion has an invalid PE signature.' }
    $Machine = $Reader.ReadUInt16()
    $ExpectedMachine = if ($Architecture -ceq 'Arm64') { 0xAA64 } else { 0x8664 }
    if ($Machine -ne $ExpectedMachine) { throw 'SDK companion has the wrong target architecture.' }
} finally { $Reader.Dispose(); $Stream.Dispose() }

# Use the compiler's actual dependency report; an ambient SDK folder is not
# evidence that its headers were consumed. Do not copy or patch SDK sources.
if ((Get-Item -LiteralPath $Dependencies).Length -gt 1MB) { throw 'Compiler dependency report exceeds its bound.' }
$DependencyData = Get-Content -LiteralPath $Dependencies -Raw | ConvertFrom-Json
if ([System.IO.Path]::GetFullPath($DependencyData.Data.Source) -ine [System.IO.Path]::GetFullPath($Source)) {
    throw 'Compiler dependency report belongs to another source file.'
}
$Includes = @($DependencyData.Data.Includes | Sort-Object -Unique)
if ($Includes.Count -lt 1 -or $Includes.Count -gt 1024) { throw 'Compiler include inventory is outside its bound.' }
$Headers = @($Includes | Where-Object { [System.IO.Path]::GetFileName($_) -like 'DirectXMath*' })
foreach ($Required in @('DirectXMath.h', 'DirectXMathMatrix.inl', 'DirectXMathVector.inl')) {
    if (@($Headers | Where-Object { [System.IO.Path]::GetFileName($_) -ceq $Required }).Count -ne 1) {
        throw "Missing or ambiguous consumed SDK header: $Required"
    }
}
$NativeBackends = @('c1xx.dll', 'c2.dll') | ForEach-Object { Get-Identity (Join-Path (Split-Path -Parent $Compiler) $_) }
$Receipt = [ordered]@{
    Schema = 1; SourceCommit = $SourceCommit; Architecture = $Architecture; PeMachine = $Machine
    Source = Get-Identity $Source; Binary = Get-Identity $Library
    Compiler = Get-Identity $Compiler; CompilerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Compiler).FileVersion
    CompilerBackends = @($NativeBackends); Arguments = $Arguments
    DirectXHeaders = @($Headers | ForEach-Object { Get-Identity $_ })
    AllIncludedHeaders = @($Includes | ForEach-Object { Get-Identity $_ })
    DependencyReport = Get-Identity $Dependencies
    Qualification = 'Installed original SDK arithmetic only; not the unknown header/toolchain used to build the loaded wpfgfx module.'
}
$ReceiptJson = $Receipt | ConvertTo-Json -Depth 8
if ([System.Text.Encoding]::UTF8.GetByteCount($ReceiptJson) -gt 1MB) { throw 'SDK provenance receipt exceeds its bound.' }
# Copy with overwrite disabled. Both destinations were checked before building.
[System.IO.File]::Copy($Library, $Destination, $false)
$ReceiptStream = [System.IO.File]::Open($Provenance, [System.IO.FileMode]::CreateNew)
try {
    $Bytes = [System.Text.Encoding]::UTF8.GetBytes($ReceiptJson)
    $ReceiptStream.Write($Bytes, 0, $Bytes.Length)
} finally { $ReceiptStream.Dispose() }
Write-Output "Prepared original SDK companion for $Architecture at $SourceCommit; no reference program was executed."
