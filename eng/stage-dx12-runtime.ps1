#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string] $Rid,
    [Parameter(Mandatory)][string] $DependencyDirectory,
    [Parameter(Mandatory)][string] $CompilerDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Runtime output must be a new directory.' }
$NativePin = Get-Content -Raw (Join-Path $PSScriptRoot 'progpu-native-wgpu.version.json') | ConvertFrom-Json
$BuildPin = Get-Content -Raw (Join-Path $PSScriptRoot 'wgpu-dxc/build-inputs.json') | ConvertFrom-Json
$CompilerPin = Get-Content -Raw (Join-Path $PSScriptRoot 'wgpu-dxc/compiler-package.json') | ConvertFrom-Json
$Dependency = Get-Content -Raw (Join-Path $DependencyDirectory 'wgpu-native-build.json') | ConvertFrom-Json
$Compiler = Get-Content -Raw (Join-Path $CompilerDirectory 'dxc-compiler-package.json') | ConvertFrom-Json
& python (Join-Path $PSScriptRoot 'progpu-wgpu-windows-runtime.py') --rid $Rid --verify-dependency $DependencyDirectory
if ($LASTEXITCODE -ne 0) { throw 'Native dependency provenance differs from reviewed inputs.' }
if ($Compiler.schemaVersion -ne 1 -or $Compiler.rid -cne $Rid -or
    $Compiler.packageId -cne $CompilerPin.packageId -or $Compiler.packageVersion -cne $CompilerPin.version -or
    $Compiler.packageSha256 -cne $CompilerPin.sha256 -or
    $Compiler.authorCertificateSha256 -cne $CompilerPin.authorCertificateSha256) {
    throw 'Compiler provenance differs from the reviewed signed package.'
}
$Machine = [int]$CompilerPin.runtimes.$Rid.machine
$Files = @(
    @{ Root = $DependencyDirectory; Name = 'wgpu_native.dll'; Hash = $Dependency.librarySha256 },
    @{ Root = $CompilerDirectory; Name = 'dxcompiler.dll'; Hash = $CompilerPin.runtimes.$Rid.'dxcompiler.dll' },
    @{ Root = $CompilerDirectory; Name = 'dxil.dll'; Hash = $CompilerPin.runtimes.$Rid.'dxil.dll' }
)
function Assert-Dx12RuntimeFile([string] $Path, [string] $ExpectedHash, [int] $Machine) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash -ine $ExpectedHash) { throw "Runtime hash mismatch: $Path" }
    $Reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($Reader.BaseStream.Length -lt 64 -or $Reader.ReadUInt16() -ne 0x5A4D) { throw 'Runtime DOS header is invalid.' }
        $Reader.BaseStream.Position = 0x3C
        $Offset = $Reader.ReadUInt32()
        if ($Offset -gt $Reader.BaseStream.Length - 6) { throw 'Runtime PE header is truncated.' }
        $Reader.BaseStream.Position = $Offset
        if ($Reader.ReadUInt32() -ne 0x4550 -or $Reader.ReadUInt16() -ne $Machine) { throw 'Runtime PE architecture differs from the selected RID.' }
    } finally { $Reader.Dispose() }
}
foreach ($File in $Files) {
    Assert-Dx12RuntimeFile (Join-Path $File.Root $File.Name) $File.Hash $Machine
}
$Parent = [IO.Path]::GetDirectoryName($OutputDirectory)
[IO.Directory]::CreateDirectory($Parent) | Out-Null
$Pending = Join-Path $Parent ('.dx12-staging-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($Pending) | Out-Null
try {
    # Exact allowlist: no runtime directory glob and no WARP/system binaries.
    foreach ($Name in @('wgpu_native.dll', 'wgpu-native-build.json', 'LICENSE.MIT', 'LICENSE.APACHE', 'wgpu-LICENSE.MIT', 'wgpu-LICENSE.APACHE', 'webgpu-headers-LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $DependencyDirectory $Name) -Destination $Pending
    }
    foreach ($Name in @('dxcompiler.dll', 'dxil.dll', 'dxc-compiler-package.json', 'LICENSE-LLVM.txt', 'LICENSE-MS.txt', 'LICENCE-MIT.txt')) {
        Copy-Item -LiteralPath (Join-Path $CompilerDirectory $Name) -Destination $Pending
    }
    # NuGet reserves .nuspec files; retain the original package metadata as XML.
    Copy-Item -LiteralPath (Join-Path $CompilerDirectory 'Microsoft.Direct3D.DXC.nuspec') -Destination (Join-Path $Pending 'Microsoft.Direct3D.DXC.package.xml')
    # Recheck the copied bytes, closing a source-copy race before publication.
    foreach ($File in $Files) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Pending $File.Name)).Hash -ine $File.Hash) {
            throw 'Runtime changed during staging.'
        }
    }
    foreach ($Property in $Dependency.files.PSObject.Properties) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Pending $Property.Name)).Hash -ine $Property.Value) {
            throw 'Native dependency notice or runtime changed during staging.'
        }
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Pending 'wgpu-native-build.json')).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $DependencyDirectory 'wgpu-native-build.json')).Hash) {
        throw 'Native dependency receipt changed during staging.'
    }
    [ordered]@{ schemaVersion = 1; rid = $Rid; nativeRevision = $Dependency.nativeRevision;
        librarySha256 = $Dependency.librarySha256; compilerPackageSha256 = $Compiler.packageSha256;
        qualification = 'package-input-only' } | ConvertTo-Json |
        Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Pending 'progpu-dx12-runtime.json')
    [IO.Directory]::Move($Pending, $OutputDirectory)
} finally {
    if (Test-Path -LiteralPath $Pending) { Remove-Item -LiteralPath $Pending -Recurse -Force }
}
Write-Output "Staged verified DX12 package input ($Rid): $OutputDirectory"
