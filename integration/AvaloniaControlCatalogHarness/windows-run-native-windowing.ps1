param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,
    [Parameter(Mandatory = $true)]
    [string] $Page,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [Parameter(Mandatory = $true)]
    [string] $ScreenshotPath,
    [int] $WarmupFrames = 10,
    [int] $MeasureFrames = 20
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force (Split-Path $OutputPath) |
    Out-Null
$env:PROGPU_AVALONIA_BENCHMARK_OUTPUT = $OutputPath
$env:PROGPU_AVALONIA_BENCHMARK_SCREENSHOT = $ScreenshotPath
$env:PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES =
    $WarmupFrames.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
$env:PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES =
    $MeasureFrames.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)

& $AppPath --native-windowing --page $Page
exit $LASTEXITCODE
