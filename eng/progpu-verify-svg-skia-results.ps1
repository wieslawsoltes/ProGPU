param(
    [Parameter(Mandatory = $true)]
    [string] $NativeResults,

    [Parameter(Mandatory = $true)]
    [string] $ProGpuResults,

    [Parameter(Mandatory = $true)]
    [string] $KnownDifferences
)

$ErrorActionPreference = 'Stop'

function Read-TestRun([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Test results were not found: $Path"
    }

    [xml] $document = Get-Content -LiteralPath $Path -Raw
    $namespace = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    $counters = $document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $namespace)
    if ($null -eq $counters) {
        throw "TRX counters were not found: $Path"
    }

    $failedFixtures = @(
        $document.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $namespace) |
            ForEach-Object {
                if ($_.testName -notmatch 'name: "([^"]+)"') {
                    throw "Unable to extract the W3C fixture from '$($_.testName)'."
                }

                $Matches[1]
            } |
            Sort-Object -Unique
    )

    return [pscustomobject]@{
        Total = [int] $counters.total
        Passed = [int] $counters.passed
        Failed = [int] $counters.failed
        Skipped = [int] $counters.total - [int] $counters.executed
        FailedFixtures = $failedFixtures
    }
}

function Assert-Counts($Run, [int] $Total, [int] $Passed, [int] $Failed, [int] $Skipped, [string] $Name) {
    $actual = "$($Run.Total)/$($Run.Passed)/$($Run.Failed)/$($Run.Skipped)"
    $expected = "$Total/$Passed/$Failed/$Skipped"
    if ($actual -ne $expected) {
        throw "$Name totals changed. Expected total/passed/failed/skipped $expected, actual $actual."
    }
}

$native = Read-TestRun $NativeResults
$progpu = Read-TestRun $ProGpuResults

$expectedFixtures = @(
    Get-Content -LiteralPath $KnownDifferences |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') } |
        Sort-Object -Unique
)

$difference = @(Compare-Object -ReferenceObject $expectedFixtures -DifferenceObject $progpu.FailedFixtures)
if ($difference.Count -ne 0) {
    $formatted = $difference | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" }
    throw "The ProGPU W3C difference inventory changed:`n$($formatted -join [Environment]::NewLine)"
}

Assert-Counts $native 533 530 0 3 'Native W3C'
Assert-Counts $progpu 533 485 45 3 'ProGPU W3C'

Write-Host "Svg.Skia W3C parity inventory verified: native 530/533 with 3 skips; ProGPU 485/533 with 45 reviewed differences and 3 skips."
