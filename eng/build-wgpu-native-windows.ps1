#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Rid = 'win-x64',
    [string] $BuildDirectory,
    [string] $ArtifactDirectory,
    [string] $CargoExecutable = 'cargo',
    [string] $RustcExecutable = 'rustc',
    [ValidateRange(1, 64)]
    [int] $Jobs = 2
)

# Builds the shared default-FXC/optional-DXC dependency, not a qualified package.
# The caller supplies a matching MSVC developer shell and build-time libclang.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The Windows wgpu-native dependency requires a Windows build host.' }
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Pin = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'progpu-native-wgpu.version.json') | ConvertFrom-Json
$InputRoot = Join-Path $PSScriptRoot 'wgpu-dxc'
$Inputs = Get-Content -Raw -LiteralPath (Join-Path $InputRoot 'build-inputs.json') | ConvertFrom-Json
$LockFile = Join-Path $InputRoot 'Cargo.lock'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $LockFile).Hash -ne $Inputs.lockSha256) {
    throw 'The reviewed DXC dependency lock does not match its pinned hash.'
}
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot "artifacts/wgpu-native-windows/$Rid" }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if ($ArtifactDirectory) {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    if (Test-Path -LiteralPath $ArtifactDirectory) {
        throw 'Dependency artifact output must be a new directory.'
    }
}
$Target = if ($Rid -eq 'win-arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
$ExpectedMachine = if ($Rid -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
$Source = Join-Path $BuildDirectory 'source'
$WgpuSource = Join-Path $BuildDirectory 'wgpu'
$Verifier = Join-Path $PSScriptRoot 'progpu-wgpu-windows-runtime.py'
$ShaderInputs = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'wgpu-hlsl/build-inputs.json') | ConvertFrom-Json
$TargetDirectory = Join-Path $BuildDirectory 'target'

function Invoke-Checked([string] $Executable, [string[]] $Arguments) {
    # Git/Cargo emit UTF-8; guest consoles may still advertise an OEM code page.
    $PreviousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
    } finally { [Console]::OutputEncoding = $PreviousEncoding }
}

function Set-ReviewedInput([string] $Path, [string] $Original, [string] $Expected) {
    $Current = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").TrimEnd()
    if ($Current -cne $Original.TrimEnd() -and $Current -cne $Expected.TrimEnd()) {
        throw "Refusing to replace modified dependency metadata: $Path"
    }
    [IO.File]::WriteAllText($Path, $Expected.TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
}

$CargoExecutable = (Get-Command $CargoExecutable -CommandType Application).Source
$RustcExecutable = (Get-Command $RustcExecutable -CommandType Application).Source
$RustVersion = (Invoke-Checked $RustcExecutable @('--version') | Out-String).Trim()
$CargoVersion = (Invoke-Checked $CargoExecutable @('--version') | Out-String).Trim()
if ($RustVersion -notmatch "^rustc $([regex]::Escape($Inputs.rustVersion)) " -or
    $CargoVersion -notmatch "^cargo $([regex]::Escape($Inputs.rustVersion)) ") {
    throw "Use Rust and Cargo $($Inputs.rustVersion); found $RustVersion / $CargoVersion."
}
Get-Command cl.exe, link.exe, dumpbin.exe, git.exe, python -CommandType Application -ErrorAction Stop | Out-Null
if (-not $env:LIBCLANG_PATH -or -not (Test-Path -LiteralPath (Join-Path $env:LIBCLANG_PATH 'libclang.dll'))) {
    throw 'LIBCLANG_PATH must identify the installed build-time libclang.dll.'
}
$LibclangPin = Get-Content -Raw -LiteralPath (Join-Path $InputRoot 'libclang-package.json') | ConvertFrom-Json
$HostRid = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
    'Arm64' { 'win-arm64' }
    'X64' { 'win-x64' }
    default { throw 'Unsupported Windows build host architecture.' }
}
$LibclangHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $env:LIBCLANG_PATH 'libclang.dll')).Hash.ToLowerInvariant()
if ($LibclangHash -cne $LibclangPin.runtimes.$HostRid.librarySha256) {
    throw 'Use the pinned build-time libclang; ambient newer Clang can generate incompatible bindings.'
}
New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
if (-not (Test-Path -LiteralPath $Source)) {
    Invoke-Checked 'git.exe' @('clone', '--filter=blob:none', '--no-checkout', $Pin.repository, $Source)
    Invoke-Checked 'git.exe' @('-C', $Source, 'fetch', '--depth', '1', 'origin', $Pin.revision)
    Invoke-Checked 'git.exe' @('-C', $Source, 'checkout', '--detach', $Pin.revision)
}
$Revision = (Invoke-Checked 'git.exe' @('-C', $Source, 'rev-parse', 'HEAD') | Out-String).Trim()
if ($Revision -cne $Pin.revision) { throw 'Refusing a dependency checkout with a different revision.' }
$ChangedPaths = @(Invoke-Checked 'git.exe' @('-C', $Source, 'diff', '--name-only', 'HEAD'))
if (@($ChangedPaths | Where-Object { $_ -notin @('Cargo.toml', 'Cargo.lock') }).Count -ne 0) {
    throw 'Refusing modified dependency implementation or headers.'
}
$Untracked = @(Invoke-Checked 'git.exe' @('-C', $Source, 'ls-files', '--others', '--exclude-standard'))
if ($Untracked.Count -ne 0) { throw 'Refusing untracked files in the dependency checkout.' }
Invoke-Checked 'git.exe' @('-C', $Source, 'submodule', 'update', '--init', '--depth', '1', 'ffi/webgpu-headers')
$Headers = Join-Path $Source 'ffi/webgpu-headers'
$HeaderRevision = (Invoke-Checked 'git.exe' @('-C', $Headers, 'rev-parse', 'HEAD') | Out-String).Trim()
if ($HeaderRevision -cne $Pin.webGpuHeadersRevision -or
    @(Invoke-Checked 'git.exe' @('-C', $Headers, 'status', '--porcelain')).Count -ne 0) {
    throw 'The WebGPU header revision or contents differ from the binding pin.'
}
# Never patch a Cargo cache or a retained dependency checkout. This owned build
# directory contains an independent pinned clone used only by this producer.
if (-not (Test-Path -LiteralPath $WgpuSource)) {
    Invoke-Checked 'git.exe' @('clone', '--filter=blob:none', '--no-checkout', 'https://github.com/gfx-rs/wgpu.git', $WgpuSource)
    Invoke-Checked 'git.exe' @('-C', $WgpuSource, 'fetch', '--depth', '1', 'origin', $Inputs.wgpuRevision)
    Invoke-Checked 'git.exe' @('-C', $WgpuSource, '-c', 'core.autocrlf=false', 'checkout', '--detach', $Inputs.wgpuRevision)
}
Invoke-Checked 'python' @($Verifier, '--prepare-source', $WgpuSource) | Out-Null
$NagaPath = [IO.Path]::GetFullPath((Join-Path $WgpuSource 'naga')).Replace('\', '/')
$NagaTomlPath = ConvertTo-Json -Compress -InputObject $NagaPath
$OriginalManifest = (Invoke-Checked 'git.exe' @('--no-pager', '-C', $Source, 'show', 'HEAD:Cargo.toml') | Out-String).Replace("`r`n", "`n")
$OriginalLock = (Invoke-Checked 'git.exe' @('--no-pager', '-C', $Source, 'show', 'HEAD:Cargo.lock') | Out-String).Replace("`r`n", "`n")
$Overlay = [IO.File]::ReadAllText((Join-Path $InputRoot 'dependency.toml')).Replace("`r`n", "`n")
if (-not $Overlay.Contains($Inputs.wgpuRevision) -or -not $Overlay.Contains($Inputs.feature)) {
    throw 'The reviewed compiler feature and dependency metadata disagree.'
}
$PatchOverlay = '[patch."https://github.com/gfx-rs/wgpu"]' + "`n" + "naga = { path = $NagaTomlPath }"
Set-ReviewedInput (Join-Path $Source 'Cargo.toml') $OriginalManifest ($OriginalManifest.TrimEnd() + "`n`n" + $Overlay.TrimEnd() + "`n`n" + $PatchOverlay)
Set-ReviewedInput (Join-Path $Source 'Cargo.lock') $OriginalLock ([IO.File]::ReadAllText($LockFile))

$PreviousRustc = $env:RUSTC
$PreviousTarget = $env:CARGO_TARGET_DIR
try {
    $env:RUSTC = $RustcExecutable
    $env:CARGO_TARGET_DIR = $TargetDirectory
    Push-Location $Source
    try {
        $Metadata = (Invoke-Checked $CargoExecutable @('metadata', '--locked', '--format-version', '1', '--filter-platform', $Target) | Out-String) | ConvertFrom-Json
        $Hal = @($Metadata.packages | Where-Object { $_.name -eq 'wgpu-hal' })
        if ($Hal.Count -ne 1 -or -not $Hal[0].source.EndsWith("#$($Inputs.wgpuRevision)")) {
            throw 'Expected exactly the pinned wgpu-hal dependency.'
        }
        $HalNode = @($Metadata.resolve.nodes | Where-Object { $_.id -eq $Hal[0].id })
        if ($HalNode.Count -ne 1 -or $Inputs.feature -notin $HalNode[0].features) {
            throw 'The resolved native dependency does not enable DXC.'
        }
        $Naga = @($Metadata.packages | Where-Object { $_.name -eq 'naga' })
        if ($Naga.Count -ne 1 -or $Naga[0].version -cne $ShaderInputs.nagaVersion -or $Naga[0].source -or
            [IO.Path]::GetFullPath($Naga[0].manifest_path) -ine [IO.Path]::GetFullPath((Join-Path $NagaPath 'Cargo.toml'))) {
            throw 'Expected only the reviewed isolated Naga implementation in the resolved graph.'
        }
        Invoke-Checked $CargoExecutable @('build', '--locked', '--release', '--target', $Target, '--jobs', "$Jobs")
    } finally { Pop-Location }
} finally {
    $env:RUSTC = $PreviousRustc
    $env:CARGO_TARGET_DIR = $PreviousTarget
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Source 'Cargo.lock')).Hash -ne $Inputs.lockSha256) {
    throw 'The dependency lock changed during the build.'
}
$Dll = Join-Path $TargetDirectory "$Target/release/wgpu_native.dll"
$Reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Dll))
try {
    if ($Reader.ReadUInt16() -ne 0x5A4D) { throw 'Missing DOS header in the native library.' }
    $Reader.BaseStream.Position = 0x3C
    $PeOffset = $Reader.ReadUInt32()
    $Reader.BaseStream.Position = $PeOffset
    if ($Reader.ReadUInt32() -ne 0x4550 -or $Reader.ReadUInt16() -ne $ExpectedMachine) {
        throw 'The native library does not match the requested Windows architecture.'
    }
} finally { $Reader.Dispose() }

# Recheck source inputs after compilation without repairing any modified bytes.
Invoke-Checked 'python' @($Verifier, '--verify-source', $WgpuSource) | Out-Null
$Exports = @(Invoke-Checked 'dumpbin.exe' @('/nologo', '/exports', $Dll) | ForEach-Object {
    if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([A-Za-z_][A-Za-z0-9_@?$]*)') { $Matches[1] }
} | Sort-Object -Unique)
foreach ($Required in @('wgpuCreateInstance', 'wgpuInstanceRequestAdapter', 'wgpuAdapterRequestDevice', 'wgpuDeviceCreateShaderModule', 'wgpuDeviceCreateComputePipeline', 'wgpuGetVersion')) {
    if ($Required -cnotin $Exports) { throw "The native dependency is missing export $Required." }
}

# Publish into a fresh directory only after successful feature, lock and PE checks.
# No NuGet cache, existing consumer, system DLL or default product asset is replaced.
$Publication = if ($ArtifactDirectory) { $ArtifactDirectory } else {
    Join-Path $BuildDirectory ("dependency-" + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $Publication | Out-Null
Copy-Item -LiteralPath $Dll -Destination $Publication
Copy-Item -LiteralPath (Join-Path $Source 'LICENSE.MIT'), (Join-Path $Source 'LICENSE.APACHE') -Destination $Publication
Copy-Item -LiteralPath (Join-Path $WgpuSource 'LICENSE.MIT') -Destination (Join-Path $Publication 'wgpu-LICENSE.MIT')
Copy-Item -LiteralPath (Join-Path $WgpuSource 'LICENSE.APACHE') -Destination (Join-Path $Publication 'wgpu-LICENSE.APACHE')
Copy-Item -LiteralPath (Join-Path $Headers 'LICENSE') -Destination (Join-Path $Publication 'webgpu-headers-LICENSE')
$FileHashes = [ordered]@{}
foreach ($Name in @('wgpu_native.dll', 'LICENSE.MIT', 'LICENSE.APACHE', 'wgpu-LICENSE.MIT', 'wgpu-LICENSE.APACHE', 'webgpu-headers-LICENSE')) {
    $FileHashes[$Name] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Publication $Name)).Hash.ToLowerInvariant()
}
$Manifest = [ordered]@{
    schemaVersion = 2
    rid = $Rid
    backendAbi = $Pin.backendAbi
    nativeRevision = $Pin.revision
    headersRevision = $HeaderRevision
    wgpuRevision = $Inputs.wgpuRevision
    compilerFeature = $Inputs.feature
    lockSha256 = $Inputs.lockSha256
    rustc = $RustVersion
    cargo = $CargoVersion
    libclangVersion = $LibclangPin.version
    libclangHostRid = $HostRid
    libclangSha256 = $LibclangHash
    librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Dll).Hash.ToLowerInvariant()
    shaderInputs = $ShaderInputs
    exports = $Exports
    files = $FileHashes
    qualification = 'build-only'
}
$Manifest | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM -LiteralPath (Join-Path $Publication 'wgpu-native-build.json')
Invoke-Checked 'python' @($Verifier, '--rid', $Rid, '--verify-dependency', $Publication) | Out-Null
Write-Output "Built unqualified shared FXC/DXC-capable native dependency: $Publication"
