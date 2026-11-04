#requires -Version 7.2
# Read-only package-input verification. No native loading, adapter/device creation,
# build, download or existing artifact modification occurs in this validator.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($PayloadRoot)) { throw 'Use an absolute verified payload root.' }
$pin = Get-Content (Join-Path $PSScriptRoot 'dawn-system-warp/inputs.json') -Raw | ConvertFrom-Json
foreach ($rid in @('win-x64','win-arm64')) {
    $runtime = $pin.runtimes.$rid
    $directory = Join-Path $PayloadRoot "$rid/native"
    $names = @('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt')
    $actual = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($actual.Count -ne $names.Count -or @($actual | Where-Object { $_.PSIsContainer -or $_.Name -notin $names }).Count -ne 0) {
        throw "Only the exact companion/receipt/license inventory is admitted: $rid"
    }
    $manifestPath = Join-Path $directory 'progpu-dawn-system-warp.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 16384) { throw 'Oversized companion receipt.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.rid -ne $rid -or
        $manifest.dawnRevision -ne $pin.dawnRevision -or
        $manifest.generatedHeaderSha256 -ne $pin.generatedHeaderSha256 -or
        $manifest.importLibrarySha256 -ne $runtime.importLibrarySha256 -or
        $manifest.providerSha256 -ne $runtime.providerSha256 -or
        $manifest.originalProducerRun -ne $pin.producerRun -or
        $manifest.originalBuilderRevision -ne $pin.builderRevision -or
        $manifest.jinjaRevision -ne $pin.jinjaRevision -or
        $manifest.markupSafeRevision -ne $pin.markupSafeRevision -or
        $manifest.sourceRevision -notmatch '^[0-9a-f]{40}$' -or
        $manifest.status -ne 'unqualified-original-header-companion') { throw "Original companion receipt mismatch: $rid" }
    $pinsHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($pin | ConvertTo-Json -Depth 8 -Compress))))
    if ($manifest.inputPinsSha256 -ne $pinsHash -or $manifest.companionSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Companion receipt does not match the checked-in input pin.'
    }
    $path = Join-Path $directory 'progpu_dawn_system_warp.dll'
    $file = Get-Item -LiteralPath $path
    if ($file.Length -le 0 -or $file.Length -gt 64MB -or $file.Length -ne $manifest.companionBytes) {
        throw 'Companion length mismatch.'
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $manifest.companionSha256) {
        throw 'Actual companion hash differs from its receipt.'
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5A4D) { throw 'Invalid companion DOS header.' }
    $offset = [BitConverter]::ToUInt32($bytes,0x3C)
    if ($offset -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes,$offset) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes,$offset+4) -ne $runtime.peMachine) { throw "Actual companion PE machine mismatch: $rid" }
    if ((Get-Item (Join-Path $directory 'Dawn-LICENSE.txt')).Length -eq 0) { throw 'Missing original Dawn notice.' }
}
Write-Output 'Both original-header Windows companion payloads verified; runtime qualification remains separate.'
