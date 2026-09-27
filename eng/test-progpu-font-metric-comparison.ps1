$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'progpu-font-metric-comparison.ps1')
$passed = 0
$bits = [BitConverter]::SingleToInt32Bits([single]9)
foreach ($offset in @(-2, -1, 0, 1, 2)) {
    Assert-NativeFontMetric 9 ([BitConverter]::Int32BitsToSingle($bits + $offset)) "accepted/$offset"
    $passed++
}
foreach ($value in @([BitConverter]::Int32BitsToSingle($bits - 3),
    [BitConverter]::Int32BitsToSingle($bits + 3), [single]::NaN, [single]::PositiveInfinity,
    [single]::NegativeInfinity, [single]0, [single]-9, [single]4.5, [single]18)) {
    $rejected = $false
    try { Assert-NativeFontMetric 9 $value 'rejected' } catch { $rejected = $true }
    if (-not $rejected) { throw "An invalid native font metric was accepted: $value" }
    $passed++
}
$arithmetic = [pscustomobject]@{ SizeInPoints = 9; ImplicitHeight = 16; Explicit96 = 16; Explicit192 = 32 }
$reference = [pscustomobject]@{ PortableArithmetic = $arithmetic }
$actual = [pscustomobject]@{ SizeInPoints = 9; ImplicitHeight = 16; Explicit96 = 16; Explicit192 = 32; PortableArithmetic = $arithmetic }
Assert-PortableFontArithmetic $reference $actual 'exact'
$passed++
$actual.SizeInPoints = [BitConverter]::Int32BitsToSingle($bits + 1)
$rejected = $false
try { Assert-PortableFontArithmetic $reference $actual 'one-ulp-implementation-error' } catch { $rejected = $true }
if (-not $rejected) { throw 'Portable arithmetic must reject even a one-ULP implementation difference.' }
$passed++
if ($passed -ne 16) { throw 'Incomplete font metric comparison controls.' }
Write-Host 'All 16 font metric comparison controls passed.'
