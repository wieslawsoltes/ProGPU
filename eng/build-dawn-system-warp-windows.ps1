#requires -Version 7.2
# Optional original-header companion production; never changes renderer gates.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PythonExecutable = 'python',
    [string]$CMakeExecutable = 'cmake',
    [string]$ClangClExecutable = 'clang-cl'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The original Windows header generation and companion require Windows.' }
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$pin = Get-Content (Join-Path $PSScriptRoot 'dawn-system-warp/inputs.json') -Raw | ConvertFrom-Json
$runtime = $pin.runtimes.$Rid
if (-not [IO.Path]::IsPathFullyQualified($BuildDirectory) -or
    -not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use absolute isolated paths.' }
$build = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\','/')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
function Is-Within([string]$Path, [string]$Parent) {
    return $Path.Equals($Parent,[StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Parent.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}
foreach ($path in @($build,$output)) {
    if ($path.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($path).TrimEnd('\','/') -or
        (Is-Within $path $repo) -or (Is-Within $repo $path) -or
        (Test-Path -LiteralPath $path)) { throw "A fresh isolated directory is required: $path" }
    # Do not redirect a supposedly external output through a directory junction.
    $ancestor = [IO.Directory]::GetParent($path)
    while ($null -ne $ancestor) {
        if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.git')) {
            throw "An isolated path must not reside in any source checkout: $path"
        }
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "An isolated path must not traverse a reparse point: $path"
        }
        $ancestor = $ancestor.Parent
    }
}
if ((Is-Within $build $output) -or (Is-Within $output $build)) { throw 'Build and publication paths must not overlap.' }
function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit $LASTEXITCODE" }
}
function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Original input hash mismatch: $Path"
    }
}
function Assert-Pe([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5A4D) { throw 'Invalid PE DOS header.' }
    $offset = [BitConverter]::ToUInt32($bytes,0x3C)
    if ($offset -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes,$offset) -ne 0x4550 -or
        [BitConverter]::ToUInt16($bytes,$offset+4) -ne $runtime.peMachine) { throw 'Actual PE architecture mismatch.' }
}
function Assert-ImportLibrary([string]$Path) {
    # Read Microsoft's documented archive/short-import metadata, not a folder
    # name. Require the exact machine in EVERY original object/import member.
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 8 -or [Text.Encoding]::ASCII.GetString($bytes,0,8) -ne "!<arch>`n") {
        throw 'Invalid original COFF archive signature.'
    }
    $offset = 8
    $objects = 0
    while ($offset -lt $bytes.Length) {
        if ($offset -gt $bytes.Length - 60 -or $bytes[$offset+58] -ne 0x60 -or $bytes[$offset+59] -ne 10) {
            throw 'Truncated original archive member.'
        }
        $name = [Text.Encoding]::ASCII.GetString($bytes,$offset,16).TrimEnd()
        $sizeText = [Text.Encoding]::ASCII.GetString($bytes,$offset+48,10).Trim()
        if ($sizeText -notmatch '^\d+$') { throw 'Invalid original archive member size.' }
        $size = [int]::Parse($sizeText,[Globalization.CultureInfo]::InvariantCulture)
        $data = $offset + 60
        if ($size -gt $bytes.Length - $data) { throw 'Original archive member exceeds file.' }
        if ($name -ne '/' -and $name -ne '//') {
            if ($size -lt 20) { throw 'Truncated original import/object header.' }
            $machine = [BitConverter]::ToUInt16($bytes,$data)
            if ($machine -eq 0 -and [BitConverter]::ToUInt16($bytes,$data+2) -eq 0xFFFF) {
                $machine = [BitConverter]::ToUInt16($bytes,$data+6)
            }
            if ($machine -ne $runtime.peMachine) { throw 'Actual original import-library machine mismatch.' }
            $objects++
        }
        $offset = $data + $size
        if (($offset -band 1) -ne 0) {
            if ($offset -ge $bytes.Length -or $bytes[$offset] -ne 10) { throw 'Invalid original archive alignment.' }
            $offset++
        }
    }
    if ($objects -eq 0) { throw 'Original import library contains no machine-qualified objects.' }
}
function Checkout-Original([string]$Url, [string]$Revision, [string]$Directory) {
    Invoke-Checked git @('init',$Directory)
    Invoke-Checked git @('-C',$Directory,'fetch','--depth','1',$Url,$Revision)
    Invoke-Checked git @('-C',$Directory,'checkout','--detach','FETCH_HEAD')
    $actual = & git -C $Directory rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Revision) { throw 'Original checkout revision mismatch.' }
}
$sourceRevision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Missing companion source revision.' }
$ownedInputs = @('eng/dawn-system-warp','eng/build-dawn-system-warp-windows.ps1','eng/validate-dawn-system-warp-payload.ps1')
$dirty = & git -C $repo status --porcelain --untracked-files=all -- @ownedInputs
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Companion production requires committed, unchanged owned source inputs.' }
New-Item -ItemType Directory -Path $build | Out-Null
$source = Join-Path $build 'dawn-original'
$pythonInputs = Join-Path $build 'original-python'
New-Item -ItemType Directory -Path $pythonInputs | Out-Null
# Chromium's pinned repositories ARE the package roots. Their directory names
# must match imports; the upstream generator inserts their parent into sys.path.
$jinja = Join-Path $pythonInputs 'jinja2'
$markup = Join-Path $pythonInputs 'markupsafe'
Checkout-Original $pin.dawnRepository $pin.dawnRevision $source
Checkout-Original $pin.jinjaRepository $pin.jinjaRevision $jinja
Checkout-Original $pin.markupSafeRepository $pin.markupSafeRevision $markup

$archive = Join-Path $build 'original-release.zip'
Invoke-WebRequest -Uri $runtime.url -OutFile $archive
if ((Get-Item -LiteralPath $archive).Length -ne $runtime.archiveBytes) { throw 'Original ZIP length mismatch.' }
Assert-Hash $archive $runtime.archiveSha256
$release = Join-Path $build 'original-release'
New-Item -ItemType Directory -Path $release | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $names = @('webgpu.h','webgpu_dawn.dll','webgpu_dawn.lib')
    if ($zip.Entries.Count -ne 3) { throw 'Unexpected original release entry inventory.' }
    foreach ($name in $names) {
        $entries = @($zip.Entries | Where-Object FullName -eq $name)
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 64MB) {
            throw "Missing/duplicate/oversized original release entry: $name"
        }
        $input = $entries[0].Open()
        $destination = [IO.File]::Open((Join-Path $release $name),[IO.FileMode]::CreateNew)
        try { $input.CopyTo($destination,65536) } finally { $destination.Dispose(); $input.Dispose() }
    }
} finally { $zip.Dispose() }
Assert-Hash (Join-Path $release 'webgpu.h') $pin.generatedHeaderSha256
Assert-Hash (Join-Path $release 'webgpu_dawn.lib') $runtime.importLibrarySha256
Assert-ImportLibrary (Join-Path $release 'webgpu_dawn.lib')
Assert-Hash (Join-Path $release 'webgpu_dawn.dll') $runtime.providerSha256
if ((Get-Item (Join-Path $release 'webgpu_dawn.dll')).Length -ne $runtime.providerBytes) { throw 'Provider length mismatch.' }
Assert-Pe (Join-Path $release 'webgpu_dawn.dll')

# Invoke only the exact upstream generator; no header/template edits or full
# Dawn build/dependency fetch. Windows newline output must match the release.
$generated = Join-Path $build 'generated-original'
Invoke-Checked $PythonExecutable @((Join-Path $source 'generator/dawn_json_generator.py'),
    '--dawn-json',(Join-Path $source 'src/dawn/dawn.json'),
    '--targets','headers,cpp_headers','--template-dir',(Join-Path $source 'generator/templates'),
    '--root-dir',$source,'--output-dir',$generated,
    '--jinja2-path',$jinja, '--markupsafe-path',$markup)
Assert-Hash (Join-Path $generated 'include/dawn/webgpu.h') $pin.generatedHeaderSha256
foreach ($header in @('include/dawn/webgpu_cpp.h','include/webgpu/webgpu_cpp_chained_struct.h',
    'include/dawn/dawn_proc_table.h')) {
    if (-not (Test-Path -LiteralPath (Join-Path $generated $header))) { throw "Missing original generated header: $header" }
}
$binary = Join-Path $build 'companion-build'
Invoke-Checked $CMakeExecutable @('-S',(Join-Path $PSScriptRoot 'dawn-system-warp'),'-B',$binary,
    '-G','Ninja','-DCMAKE_BUILD_TYPE=Release',"-DCMAKE_CXX_COMPILER=$ClangClExecutable",
    "-DCMAKE_CXX_COMPILER_TARGET=$($runtime.clangTarget)","-DDAWN_SOURCE=$source",
    "-DDAWN_GENERATED=$generated","-DDAWN_IMPORT_LIBRARY=$(Join-Path $release 'webgpu_dawn.lib')")
Invoke-Checked $CMakeExecutable @('--build',$binary,'--config','Release','--target','progpu_dawn_system_warp')
$companion = Join-Path $binary 'progpu_dawn_system_warp.dll'
Assert-Pe $companion
# Only a successful complete companion build publishes a NEW private directory.
# Never stage a renderer artifact from a failed/canceled Build or distribute WARP.
$stage = Join-Path $build 'publication'
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath $companion -Destination $stage
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $stage 'Dawn-LICENSE.txt')
@{
    schemaVersion=1; rid=$Rid; dawnRevision=$pin.dawnRevision
    generatedHeaderSha256=$pin.generatedHeaderSha256; importLibrarySha256=$runtime.importLibrarySha256
    providerSha256=$runtime.providerSha256; originalProducerRun=$pin.producerRun
    companionSha256=(Get-FileHash $companion -Algorithm SHA256).Hash.ToLowerInvariant()
    companionBytes=(Get-Item $companion).Length
    sourceRevision=$sourceRevision; originalBuilderRevision=$pin.builderRevision
    jinjaRevision=$pin.jinjaRevision; markupSafeRevision=$pin.markupSafeRevision
    # Canonical plain-ASCII JSON metadata avoids checkout CRLF/LF differences
    # when a later cross-platform package job verifies the same semantic pin.
    inputPinsSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($pin | ConvertTo-Json -Depth 8 -Compress))))).ToLowerInvariant()
    status='unqualified-original-header-companion'
} | ConvertTo-Json | Set-Content (Join-Path $stage 'progpu-dawn-system-warp.json') -Encoding utf8NoBOM
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.Directory]::Move($stage,$output)
Write-Output "Unqualified original-header companion: $output"
