param([Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64')][string] $Rid)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Drawing clip comparison requires actual Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/windows-drawing-clips/$Rid"
if (Test-Path -LiteralPath $output) { throw 'Drawing clip evidence already exists.' }
New-Item -ItemType Directory -Path $output | Out-Null
$receipts = @()
foreach ($kind in @('Reference', 'Portable')) {
    $project = Join-Path $root "eng/probes/DrawingClips/$kind/DrawingClips.$kind.csproj"
    & dotnet build $project -c Release -r $Rid --nologo
    if ($LASTEXITCODE -ne 0) { throw "Drawing clip probe build failed: $kind" }
    $framework = if ($kind -eq 'Reference') { 'net10.0-windows' } else { 'net10.0' }
    $dll = Join-Path $root "eng/probes/DrawingClips/$kind/bin/Release/$framework/$Rid/DrawingClips.$kind.dll"
    $receipt = Join-Path $output "$kind.json"
    $stdout = Join-Path $output "$kind-stdout.log"
    $stderr = Join-Path $output "$kind-stderr.log"
    $child = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"{0}"' -f $dll), ('"{0}"' -f $receipt)) -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        if (!$child.WaitForExit(120000)) { throw "Drawing clip probe $kind exceeded 120 seconds." }
        if ($child.ExitCode -ne 0) { throw "Drawing clip probe $kind failed: $($child.ExitCode)." }
    } finally {
        if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit(5000) | Out-Null }
        $child.Dispose()
        Get-Content -LiteralPath $stdout
        Get-Content -LiteralPath $stderr
    }
    $receipts += Get-Content -Raw -LiteralPath $receipt | ConvertFrom-Json
}
$expected, $actual = $receipts
$architecture = if ($Rid -eq 'win-x64') { 'X64' } else { 'Arm64' }
if ($expected.Architecture -cne $architecture -or $actual.Architecture -cne $architecture) {
    throw 'Drawing clip probes ran on the wrong architecture.'
}
if ($expected.Assembly -ceq $actual.Assembly -or $expected.AssemblySha256 -ceq $actual.AssemblySha256 -or
    $expected.AssemblyPath -notmatch '[\\/]shared[\\/]Microsoft.WindowsDesktop.App[\\/]') {
    throw 'The reference must use the independent Microsoft Windows Desktop assembly.'
}
$names = @('Replace', 'Intersect', 'Union', 'Xor', 'Exclude', 'Complement', 'save', 'container', 'flush', 'translate', 'page', 'display', 'rotate')
$transformNames = foreach ($operation in @('assign', 'elements', 'multiply', 'scale', 'translate', 'rotate', 'assign-offset', 'elements-offset')) {
    $values = @('NaN', 'Infinity', '-Infinity', '0')
    if ($operation -in @('assign', 'elements', 'multiply', 'scale')) { $values += @('1E-12', '1E+30', '-2') }
    foreach ($value in $values) { "$operation/$value" }
}
foreach ($receipt in $receipts) {
    if ($receipt.Cases.Count -ne 13 -or ($receipt.Cases.Name -join ',') -cne ($names -join ',')) {
        throw 'All 13 distinct drawing clip cases must execute in source order.'
    }
    $keys = @($receipt.Transforms | ForEach-Object { "$($_.Operation)/$($_.Value)" })
    if ($receipt.Transforms.Count -ne 44 -or ($keys -join ',') -cne ($transformNames -join ',')) {
        throw 'All 44 distinct transform cases must execute in source order.'
    }
}
for ($index = 0; $index -lt 44; $index++) {
    $reference = $expected.Transforms[$index]
    $portable = $actual.Transforms[$index]
    if ($reference.Error -cne $portable.Error -or
        ($reference.Clip | ConvertTo-Json -Compress) -cne ($portable.Clip | ConvertTo-Json -Compress) -or
        $reference.Matrix.Count -ne 6 -or $portable.Matrix.Count -ne 6) {
        throw "Transform mutation mismatch: $($transformNames[$index])."
    }
    for ($element = 0; $element -lt 6; $element++) {
        # Numeric equality treats signed zero alike; no tolerance. Named NaN
        # components are explicitly retained by the separate translation cases.
        if (-not ([double]$reference.Matrix[$element]).Equals([double]$portable.Matrix[$element])) {
            throw "Transform matrix mismatch: $($transformNames[$index])/$element."
        }
    }
}
Write-Host "Drawing transforms match Microsoft: $Rid / 44 cases, exact acceptance and unchanged state after rejection."
for ($index = 0; $index -lt 13; $index++) {
    $reference = $expected.Cases[$index]
    $portable = $actual.Cases[$index]
    foreach ($property in @('Bounds', 'RegionBounds', 'Visible', 'Ink', 'PixelsSha256')) {
        if (($reference.$property | ConvertTo-Json -Compress) -cne ($portable.$property | ConvertTo-Json -Compress)) {
            throw "Drawing clip mismatch: $($reference.Name)/$property. See both retained receipts."
        }
    }
}
Write-Host "Drawing clips match Microsoft: $Rid / 13 cases, exact bounds, visibility, and every pixel."
