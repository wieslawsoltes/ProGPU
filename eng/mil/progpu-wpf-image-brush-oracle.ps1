param(
    [Parameter(Mandatory = $true)][string]$ProGpuCaptureDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run with powershell.exe -NoProfile -Sta -File.'
}
Add-Type -AssemblyName WindowsBase, PresentationCore, System.Xaml
# Original public-API diagnostic. All pixel loops are scalar reference oracles.
Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'ProGpuWpfImageBrushOracle.cs')) -ReferencedAssemblies @(
    [System.Windows.Rect].Assembly.Location,
    [System.Windows.Media.ImageBrush].Assembly.Location,
    [System.Xaml.XamlReader].Assembly.Location
)
[ProGpuWpfImageBrushOracle]::Run($ProGpuCaptureDirectory, $OutputDirectory)
