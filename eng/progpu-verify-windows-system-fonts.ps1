param([Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string] $Rid)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows system font validation requires actual Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/windows-system-fonts/$Rid"
New-Item -ItemType Directory -Force $output | Out-Null
$reference = Join-Path $root 'eng/probes/SystemFonts/Reference/SystemFonts.Reference.csproj'
$portable = Join-Path $root 'eng/probes/SystemFonts/Portable/SystemFonts.Portable.csproj'
foreach ($project in @($reference, $portable)) {
    & dotnet build $project -c Release -r $Rid --nologo
    if ($LASTEXITCODE -ne 0) { throw "System font probe build failed: $project" }
}
$referenceDll = Join-Path $root "eng/probes/SystemFonts/Reference/bin/Release/net10.0-windows/$Rid/SystemFonts.Reference.dll"
$portableDll = Join-Path $root "eng/probes/SystemFonts/Portable/bin/Release/net10.0/$Rid/SystemFonts.Portable.dll"
$expectedRoles = @('CaptionFont', 'DefaultFont', 'DialogFont', 'IconTitleFont', 'MenuFont', 'MessageBoxFont', 'SmallCaptionFont', 'StatusFont')
$properties = @('Role', 'Name', 'Size', 'SizeInPoints', 'Style', 'Unit', 'GdiCharSet', 'GdiVerticalFont', 'SystemFontName', 'IsSystemFont')
foreach ($mode in @('unaware', 'system', 'per-monitor-v2')) {
    $referencePath = Join-Path $output "$mode-reference.json"
    $portablePath = Join-Path $output "$mode-portable.json"
    & dotnet $referenceDll $mode $referencePath
    if ($LASTEXITCODE -ne 0) { throw "Microsoft reference failed: $mode" }
    & dotnet $portableDll $mode $portablePath
    if ($LASTEXITCODE -ne 0) { throw "Portable system fonts failed: $mode" }
    $expected = Get-Content -Raw $referencePath | ConvertFrom-Json
    $actual = Get-Content -Raw $portablePath | ConvertFrom-Json
    $architecture = if ($Rid -eq 'win-x64') { 'X64' } else { 'Arm64' }
    if ($expected.Architecture -cne $architecture -or $actual.Architecture -cne $architecture) {
        throw 'System font probes ran with an incorrect architecture.'
    }
    if ($expected.Assembly -ceq $actual.Assembly -or $expected.AssemblySha256 -ceq $actual.AssemblySha256 -or
        $expected.AssemblyPath -notmatch '[\\/]shared[\\/]Microsoft.WindowsDesktop.App[\\/]') {
        throw 'System font oracle must load the independent Microsoft Windows Desktop assembly.'
    }
    foreach ($receipt in @($expected, $actual)) {
        if ($receipt.Mode -cne $mode -or $receipt.Fonts.Count -ne 8 -or
            (($receipt.Fonts.Role | Sort-Object) -join ',') -cne ($expectedRoles -join ',')) {
            throw 'Incomplete system font role/mode coverage.'
        }
    }
    foreach ($font in $expected.Fonts) {
        $found = @($actual.Fonts | Where-Object Role -CEQ $font.Role)
        if ($found.Count -ne 1) { throw "Missing or duplicated role: $($font.Role)" }
        foreach ($property in $properties) {
            if ($font.$property -cne $found[0].$property) {
                throw "$mode/$($font.Role)/${property}: expected '$($font.$property)', actual '$($found[0].$property)'"
            }
        }
    }
    Write-Host "System fonts match Microsoft: $Rid / $mode / all 8 roles and ownership checks."
}
