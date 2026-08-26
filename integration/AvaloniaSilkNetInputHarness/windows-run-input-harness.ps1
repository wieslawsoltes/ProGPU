param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("InitialPosition", "TitleBarDrag", "Resize", "Input", "Touch")]
    [string] $Scenario,
    [string] $AppPath = "",
    [string] $OutputPath = "",
    [string] $Expected = "",
    [string] $Position = "",
    [string] $ExpectedPosition = "",
    [int] $TimeoutSeconds = 30,
    [switch] $TraceChromeDrag
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $PSScriptRoot "AvaloniaSilkNetInputHarness.exe"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = "C:\Temp\progpu-$($Scenario.ToLowerInvariant()).json"
}

switch ($Scenario) {
    "InitialPosition" {
        $Expected = "initial-position"
        $Position = "180,140"
        $ExpectedPosition = "180,140"
        $TimeoutSeconds = 10
    }
    "TitleBarDrag" {
        $Expected = "move"
        $TimeoutSeconds = 60
        $TraceChromeDrag = $true
    }
    "Resize" {
        $Expected = "resize"
        $TimeoutSeconds = 60
        $TraceChromeDrag = $true
    }
    "Input" {
        $Expected =
            "keyboard,text,pointer,wheel,shortcut,mouse-left," +
            "mouse-right,mouse-middle,mouse-x1,mouse-x2"
        $TimeoutSeconds = 60
    }
    "Touch" {
        $Expected = "touch"
        $TimeoutSeconds = 60
    }
}

New-Item -ItemType Directory -Force (Split-Path $OutputPath) |
    Out-Null
Remove-Item $OutputPath -Force -ErrorAction SilentlyContinue
Remove-Item "$OutputPath.ready" -Force -ErrorAction SilentlyContinue
$env:PROGPU_AVALONIA_INPUT_OUTPUT = $OutputPath
$env:PROGPU_AVALONIA_INPUT_EXPECT = $Expected
$env:PROGPU_AVALONIA_INPUT_TIMEOUT_SECONDS =
    $TimeoutSeconds.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture)
$env:PROGPU_AVALONIA_WINDOW_POSITION = $Position
$env:PROGPU_AVALONIA_WINDOW_EXPECT_POSITION = $ExpectedPosition
$env:PROGPU_AVALONIA_TRACE_CHROME_DRAG = if ($TraceChromeDrag) {
    "1"
}
else {
    "0"
}
$env:PROGPU_AVALONIA_TRACE_WINDOW_EVENTS = "1"
$env:PROGPU_AVALONIA_TRACE_WINDOW_EVENTS_PATH =
    "$OutputPath.events.log"
Remove-Item $env:PROGPU_AVALONIA_TRACE_WINDOW_EVENTS_PATH `
    -Force `
    -ErrorAction SilentlyContinue

& $AppPath
exit $LASTEXITCODE
