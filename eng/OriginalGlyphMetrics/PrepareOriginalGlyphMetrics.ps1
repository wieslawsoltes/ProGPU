#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string] $Architecture,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string] $SourceCommit,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [switch] $Observe
)

# Authoring this launcher is not execution permission. Default: build only.
# -Observe is for a separately authorized, bounded original CPU observation.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Same selected-native-host/target contract as the reviewed ProGPU-owned
# WpfShaderEffectReference/BuildOriginalAxisMath.ps1 at cb9096b11. No SDK
# implementation or coefficient source is inspected, patched, or copied.
function Get-OriginalSdkToolchain([string] $Installation, [string] $VCToolsDirectory, [string] $Architecture) {
    if ($Architecture -cnotin @('X64', 'Arm64') -or [string]::IsNullOrWhiteSpace($VCToolsDirectory) -or
        -not [IO.Path]::IsPathFullyQualified($VCToolsDirectory)) {
        throw 'The selected native C++ toolchain directory or architecture is unavailable.'
    }
    $SelectedRoot = [IO.Path]::GetFullPath((Join-Path $Installation 'VC/Tools/MSVC')) + [IO.Path]::DirectorySeparatorChar
    $ToolsDirectory = [IO.Path]::GetFullPath($VCToolsDirectory)
    if (-not $ToolsDirectory.StartsWith($SelectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The selected C++ toolchain does not belong to the selected Visual Studio installation.'
    }
    $Target = $Architecture.ToLowerInvariant()
    $Directory = Join-Path $ToolsDirectory "bin/Host$Target/$Target"
    $Compiler = Join-Path $Directory 'cl.exe'
    $Linker = Join-Path $Directory 'link.exe'
    foreach ($Tool in @($Compiler, $Linker)) {
        if (-not (Test-Path -LiteralPath $Tool -PathType Leaf)) { throw "Missing selected native C++ tool: $Tool" }
    }
    [pscustomobject]@{
        Directory = [IO.Path]::GetFullPath($Directory)
        Compiler = (Get-Item -LiteralPath $Compiler).FullName
        Linker = (Get-Item -LiteralPath $Linker).FullName
    }
}
function Get-Identity([string] $Path) {
    $Item = Get-Item -LiteralPath $Path
    [ordered]@{ Path = $Item.FullName; Bytes = $Item.Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Get-PeMachine([string] $Path) {
    $Stream = [IO.File]::OpenRead($Path)
    $Reader = [IO.BinaryReader]::new($Stream)
    try {
        if ($Stream.Length -lt 64 -or $Reader.ReadUInt16() -ne 0x5A4D) { throw 'Not a PE image.' }
        $Stream.Position = 0x3C
        $Header = $Reader.ReadInt32()
        if ($Header -lt 64 -or $Header -gt $Stream.Length - 24) { throw 'Invalid PE offset.' }
        $Stream.Position = $Header
        if ($Reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
        $Reader.ReadUInt16()
    } finally { $Reader.Dispose(); $Stream.Dispose() }
}
function Write-NewUtf8([string] $Path, [string] $Text) {
    $Data = [Text.Encoding]::UTF8.GetBytes($Text)
    $Stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $Stream.Write($Data, 0, $Data.Length) } finally { $Stream.Dispose() }
}
function Invoke-BoundedChild([string] $Executable, [string[]] $Arguments, [string] $WorkingDirectory,
    [string] $LogPrefix, [int] $Seconds, [long] $MemoryLimit) {
    $Info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $Info.WorkingDirectory = $WorkingDirectory
    $Info.UseShellExecute = $false
    $Info.CreateNoWindow = $true
    $Info.RedirectStandardOutput = $true
    $Info.RedirectStandardError = $true
    foreach ($Argument in $Arguments) { $Info.ArgumentList.Add($Argument) }
    $Process = [Diagnostics.Process]::new()
    $Process.StartInfo = $Info
    $Stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $Peak = 0L
    try {
        if (-not $Process.Start()) { throw 'Owned child failed to start.' }
        $Output = $Process.StandardOutput.ReadToEndAsync()
        $Errors = $Process.StandardError.ReadToEndAsync()
        while (-not $Process.WaitForExit(100)) {
            $Process.Refresh()
            $Peak = [Math]::Max($Peak, $Process.PeakWorkingSet64)
            if ($Stopwatch.Elapsed.TotalSeconds -gt $Seconds -or ($MemoryLimit -gt 0 -and $Peak -gt $MemoryLimit)) {
                $Process.Kill($true)
                $Process.WaitForExit()
                throw 'Owned child exceeded its original time/memory bound; not source API evidence.'
            }
        }
        $Process.Refresh()
        $Peak = [Math]::Max($Peak, $Process.PeakWorkingSet64)
        $OutText = $Output.GetAwaiter().GetResult()
        $ErrorText = $Errors.GetAwaiter().GetResult()
        if ([Text.Encoding]::UTF8.GetByteCount($OutText) -gt 16MB -or [Text.Encoding]::UTF8.GetByteCount($ErrorText) -gt 1MB) {
            throw 'Owned child output exceeded its bound.'
        }
        Write-NewUtf8 "$LogPrefix.stdout.txt" $OutText
        Write-NewUtf8 "$LogPrefix.stderr.txt" $ErrorText
        if ($Stopwatch.Elapsed.TotalSeconds -gt $Seconds -or ($MemoryLimit -gt 0 -and $Peak -gt $MemoryLimit)) {
            throw 'Completed child exceeded time/memory bound; no successful receipt.'
        }
        if ($Process.ExitCode -ne 0) { throw "Owned child failed with exit code $($Process.ExitCode); retained logs are not source policy." }
        [pscustomobject]@{ Stdout = $OutText; PeakWorkingSet = $Peak; ElapsedMilliseconds = $Stopwatch.ElapsedMilliseconds }
    } finally { $Process.Dispose() }
}

if (-not $IsWindows) { throw 'The original glyph observer requires Windows.' }
$Runtime = [Runtime.InteropServices.RuntimeInformation]
if ($Runtime::ProcessArchitecture.ToString() -cne $Architecture -or $Runtime::OSArchitecture.ToString() -cne $Architecture) {
    throw 'Use native matching process, OS and selected tool architectures; no emulated host.'
}
$Repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ActualCommit = & git -C $Repository rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $ActualCommit.Trim() -cne $SourceCommit) { throw 'Reference source identity changed.' }
$Dirty = & git -C $Repository status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $Dirty) { throw 'Original observer requires a clean committed source tree.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output directory must be new; never overwrite an original receipt.' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$BuildDirectory = Join-Path $OutputDirectory 'build'
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
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
$Toolchain = Get-OriginalSdkToolchain $Installation $env:VCToolsInstallDir $Architecture
$Compiler = $Toolchain.Compiler
$Linker = $Toolchain.Linker
$Source = Join-Path $PSScriptRoot 'OriginalGlyphMetrics.cpp'
$Object = Join-Path $BuildDirectory 'OriginalGlyphMetrics.obj'
$Binary = Join-Path $BuildDirectory 'OriginalGlyphMetrics.exe'
$Dependencies = Join-Path $BuildDirectory 'dependencies.json'
$SdkVersion = $env:WindowsSDKVersion.TrimEnd('\', '/')
$SdkRoot = [IO.Path]::GetFullPath($env:WindowsSdkDir)
$SdkInclude = Join-Path $SdkRoot "Include/$SdkVersion"
$SdkLibraries = Join-Path $SdkRoot "Lib/$SdkVersion/um/$Target"
$Libraries = @('dwrite.lib', 'd2d1.lib', 'bcrypt.lib', 'ole32.lib') | ForEach-Object { Join-Path $SdkLibraries $_ }
foreach ($Library in $Libraries) {
    if (-not (Test-Path -LiteralPath $Library -PathType Leaf)) { throw "Selected SDK import library unavailable: $Library" }
}
$Arguments = @('/nologo', '/std:c++20', '/permissive-', '/W4', '/WX', '/O2', '/fp:strict', '/EHsc', '/MD', '/c',
    '/sourceDependencies', $Dependencies, "/Fo$Object", "/I$(Join-Path $Repository 'src/ProGPU.Native/include')",
    "/I$(Join-Path $Repository 'src/ProGPU.Native/tests')", $Source)
$LinkArguments = @('/NOLOGO', '/INCREMENTAL:NO', "/MACHINE:$Target", '/SUBSYSTEM:CONSOLE', "/OUT:$Binary", $Object) + $Libraries
$CompilerResult = Invoke-BoundedChild $Compiler $Arguments $BuildDirectory (Join-Path $BuildDirectory 'compile') 60 0
$LinkResult = Invoke-BoundedChild $Linker $LinkArguments $BuildDirectory (Join-Path $BuildDirectory 'link') 60 0
$Machine = Get-PeMachine $Binary
$ExpectedMachine = if ($Architecture -ceq 'Arm64') { 0xAA64 } else { 0x8664 }
if ($Machine -ne $ExpectedMachine) { throw 'Original observer PE target differs from selected architecture.' }
if ((Get-Item -LiteralPath $Dependencies).Length -gt 1MB) { throw 'Compiler dependency report exceeds bound.' }
$DependencyData = Get-Content -LiteralPath $Dependencies -Raw | ConvertFrom-Json
if ([IO.Path]::GetFullPath($DependencyData.Data.Source) -ine [IO.Path]::GetFullPath($Source)) { throw 'Unrelated compiler dependency report.' }
$Includes = @($DependencyData.Data.Includes | Sort-Object -Unique)
if ($Includes.Count -lt 1 -or $Includes.Count -gt 1024) { throw 'Compiler include inventory is outside bound.' }
foreach ($Required in @('dwrite.h', 'dwrite_1.h', 'dwrite_2.h', 'dwrite_3.h', 'd2d1.h', 'bcrypt.h')) {
    $Matches = @($Includes | Where-Object { [IO.Path]::GetFileName($_) -ieq $Required })
    if ($Matches.Count -ne 1 -or -not [IO.Path]::GetFullPath($Matches[0]).StartsWith(
        ([IO.Path]::GetFullPath($SdkInclude) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Missing, ambiguous or non-selected consumed SDK interface header: $Required"
    }
}
$Os = Get-CimInstance Win32_OperatingSystem
$Receipt = [ordered]@{
    Schema = 1; Kind = 'original-glyph-metrics'; Qualified = $false; SourceCommit = $SourceCommit
    Architecture = $Architecture; PeMachine = $Machine; ProcessArchitecture = $Runtime::ProcessArchitecture.ToString()
    OsArchitecture = $Runtime::OSArchitecture.ToString(); OsVersion = $Os.Version; OsBuild = $Os.BuildNumber; OsCaption = $Os.Caption
    Source = Get-Identity $Source; Binary = Get-Identity $Binary; Launcher = Get-Identity $PSCommandPath
    Compiler = Get-Identity $Compiler; CompilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Compiler).FileVersion
    CompilerBackends = @(@('c1xx.dll', 'c2.dll') | ForEach-Object { Get-Identity (Join-Path $Toolchain.Directory $_) })
    Linker = Get-Identity $Linker; Arguments = $Arguments; LinkArguments = $LinkArguments
    SelectedToolDirectory = $Toolchain.Directory; SdkRoot = $SdkRoot; SdkVersion = $SdkVersion
    IncludeEnvironment = $env:INCLUDE; LibraryEnvironment = $env:LIB
    ImportLibraries = @($Libraries | ForEach-Object { Get-Identity $_ })
    AllIncludedHeaders = @($Includes | ForEach-Object { Get-Identity $_ }); DependencyReport = Get-Identity $Dependencies
    CompilerElapsedMilliseconds = $CompilerResult.ElapsedMilliseconds; LinkerElapsedMilliseconds = $LinkResult.ElapsedMilliseconds
    ObservationRequested = [bool]$Observe; ObservationExecuted = $false
    Scope = 'Installed original DirectWrite and CPU geometry APIs only; not DrawGlyphRun, product, GPU or UI qualification.'
}
Write-NewUtf8 (Join-Path $OutputDirectory 'build-provenance.json') ($Receipt | ConvertTo-Json -Depth 12)
if (-not $Observe) {
    Write-Output 'Prepared original glyph observer only; no observer was executed.'
    return
}
$FontDirectory = Join-Path $OutputDirectory 'fonts'
[IO.Directory]::CreateDirectory($FontDirectory) | Out-Null
$Observation = Invoke-BoundedChild $Binary @($FontDirectory) $BuildDirectory (Join-Path $OutputDirectory 'observer') 60 128MB
$Parsed = $Observation.Stdout | ConvertFrom-Json -Depth 48
if ($Parsed.schema -ne 1 -or $Parsed.qualified -ne $false -or $Parsed.kind -cne 'original-cpu-glyph-metrics' -or
    $Parsed.families.Count -ne 14 -or $Parsed.attempted_instances -gt 40 -or $Parsed.outline_calls -gt 280 -or $Parsed.analysis_calls -gt 280) {
    throw 'Original observation envelope/inventory changed.'
}
$OriginalModules = @($Parsed.directwrite_module, $Parsed.geometry_module) | ForEach-Object {
    $Path = [IO.Path]::GetFullPath($_)
    if ([IO.Path]::GetDirectoryName($Path) -ine [Environment]::SystemDirectory) { throw 'Original API loaded outside native Windows system directory.' }
    [ordered]@{ File = Get-Identity $Path; Version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion; PeMachine = Get-PeMachine $Path }
}
$Fonts = @($Parsed.families | ForEach-Object {
    if ($_.name -cnotmatch '^[a-z0-9-]+$') { throw 'Unexpected font artifact name.' }
    $File = Get-Identity (Join-Path $FontDirectory ($_.name + '.font'))
    if ($File.Sha256 -ine $_.font_sha256 -or $File.Bytes -ne $_.font_bytes) { throw 'Original font artifact identity mismatch.' }
    $File
})
Write-NewUtf8 (Join-Path $OutputDirectory 'observations.json') $Observation.Stdout
$Receipt.ObservationExecuted = $true
$Receipt['Observation'] = Get-Identity (Join-Path $OutputDirectory 'observations.json')
$Receipt['OriginalModules'] = $OriginalModules
$Receipt['FontArtifacts'] = $Fonts
$Receipt['PeakWorkingSet'] = $Observation.PeakWorkingSet
$Receipt['ElapsedMilliseconds'] = $Observation.ElapsedMilliseconds
Write-NewUtf8 (Join-Path $OutputDirectory 'provenance.json') ($Receipt | ConvertTo-Json -Depth 12)
Write-Output 'Retained original CPU observations with qualified=false; no source policy or rendering qualification inferred.'
