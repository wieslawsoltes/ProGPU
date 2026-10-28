#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string] $Rid)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'DX12 runtime production requires Windows.' }
$Root = Split-Path -Parent $PSScriptRoot
$Architecture = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$Triple = if ($Rid -eq 'win-arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -ine $Architecture) {
    throw 'Run the DX12 runtime build in a target-native PowerShell process.'
}
$Component = if ($Rid -eq 'win-arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$VisualStudio = (& $VsWhere -latest -products * -requires $Component -property installationPath | Select-Object -First 1)
if (-not $VisualStudio) { throw 'Matching Visual Studio build tools are required.' }
Import-Module (Join-Path $VisualStudio 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $VisualStudio -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=$Architecture" | Out-Null
$LibclangPin = Get-Content -Raw (Join-Path $PSScriptRoot 'wgpu-dxc/libclang-package.json') | ConvertFrom-Json
$Download = Join-Path $Root "artifacts/progpu-dx12/download/$Rid"
[IO.Directory]::CreateDirectory($Download) | Out-Null
$LibclangId = "libclang.runtime.$Rid"
$LibclangArchive = Join-Path $Download "$LibclangId.$($LibclangPin.version).nupkg"
if (-not (Test-Path -LiteralPath $LibclangArchive)) {
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$LibclangId/$($LibclangPin.version)/$LibclangId.$($LibclangPin.version).nupkg" -OutFile $LibclangArchive
}
$Libclang = Join-Path $Root "artifacts/progpu-dx12/libclang/$Rid"
& (Join-Path $PSScriptRoot 'stage-wgpu-libclang.ps1') -Rid $Rid -PackagePath $LibclangArchive -OutputDirectory $Libclang
$env:LIBCLANG_PATH = $Libclang
$Inputs = Get-Content -Raw (Join-Path $PSScriptRoot 'wgpu-dxc/build-inputs.json') | ConvertFrom-Json
$Toolchain = "$($Inputs.rustVersion)-$Triple"
& rustup toolchain install $Toolchain --profile minimal --target $Triple
if ($LASTEXITCODE -ne 0) { throw 'Pinned Rust toolchain installation failed.' }
$Cargo = (& rustup which --toolchain $Toolchain cargo | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Pinned Cargo was not found.' }
$Rustc = (& rustup which --toolchain $Toolchain rustc | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Pinned Rust compiler was not found.' }
$Dependency = Join-Path $Root "artifacts/progpu-dx12/dependency/$Rid"
& (Join-Path $PSScriptRoot 'build-wgpu-native-windows.ps1') -Rid $Rid -ArtifactDirectory $Dependency -CargoExecutable $Cargo -RustcExecutable $Rustc
$Pin = Get-Content -Raw (Join-Path $PSScriptRoot 'wgpu-dxc/compiler-package.json') | ConvertFrom-Json
$Archive = Join-Path $Download "Microsoft.Direct3D.DXC.$($Pin.version).nupkg"
if (-not (Test-Path -LiteralPath $Archive)) { Invoke-WebRequest -Uri $Pin.url -OutFile $Archive }
$Compiler = Join-Path $Root "artifacts/progpu-dx12/compiler/$Rid"
& (Join-Path $PSScriptRoot 'stage-dxc-compiler.ps1') -Rid $Rid -PackagePath $Archive -OutputDirectory $Compiler
& (Join-Path $PSScriptRoot 'stage-dx12-runtime.ps1') -Rid $Rid -DependencyDirectory $Dependency -CompilerDirectory $Compiler -OutputDirectory (Join-Path $Root "artifacts/progpu-dx12/package/$Rid/native")

# The default FXC payload uses these exact source-built bytes without DXC/DXIL.
& python (Join-Path $PSScriptRoot 'progpu-wgpu-windows-runtime.py') --rid $Rid --stage $Dependency --output (Join-Path $Root "artifacts/progpu-wgpu-windows/package/$Rid/native")
if ($LASTEXITCODE -ne 0) { throw 'Default Windows runtime staging failed.' }
