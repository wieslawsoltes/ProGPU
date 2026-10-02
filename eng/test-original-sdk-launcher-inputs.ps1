#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The existing compiler-input tests extract only declared helpers from the AST.
# Do not invoke the launcher, DevShell, a compiler/linker, SDK, UI or renderer.
$Script = Join-Path $PSScriptRoot 'WpfShaderEffectReference/BuildOriginalAxisMath.ps1'
$Tokens = $null
$Errors = $null
$Ast = [Management.Automation.Language.Parser]::ParseFile($Script, [ref]$Tokens, [ref]$Errors)
if ($Errors.Count) { throw ($Errors | Out-String) }
foreach ($Name in @('Get-OriginalSdkToolchain', 'Get-OriginalSdkHeaders')) {
    $Functions = @($Ast.FindAll({
        param($Node)
        $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -ceq $Name
    }, $true))
    if ($Functions.Count -ne 1) { throw "Missing or duplicate SDK launcher selector: $Name" }
    . ([scriptblock]::Create($Functions[0].Extent.Text))
}

function Require-Rejection([scriptblock] $Action, [string] $Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike $Pattern) { throw }
        return
    }
    throw "Expected rejection: $Pattern"
}

$Temporary = [IO.Directory]::CreateTempSubdirectory('progpu-original-sdk-inputs-').FullName
$OriginalPath = $env:PATH
$Passed = 0
try {
    $Installation = Join-Path $Temporary 'selected-vs'
    $Tools = Join-Path $Installation 'VC/Tools/MSVC/14.51.36231'
    $Directories = @{}
    foreach ($Architecture in @('X64', 'Arm64')) {
        $Target = $Architecture.ToLowerInvariant()
        $Directory = Join-Path $Tools "bin/Host$Target/$Target"
        [IO.Directory]::CreateDirectory($Directory) | Out-Null
        $Directories[$Architecture] = $Directory
        foreach ($Name in @('cl.exe', 'link.exe')) {
            # Deliberately non-executable selector fixtures, never SDK payloads.
            [IO.File]::WriteAllText((Join-Path $Directory $Name), 'selector fixture only; never execute')
        }
    }
    foreach ($Architecture in @('X64', 'Arm64')) {
        foreach ($Order in @(@('X64', 'Arm64'), @('Arm64', 'X64'))) {
            $env:PATH = ($Directories[$Order[0]], $Directories[$Order[1]], $OriginalPath) -join [IO.Path]::PathSeparator
            $Selected = Get-OriginalSdkToolchain $Installation $Tools $Architecture
            foreach ($Name in @('Compiler', 'Linker')) {
                $Executable = if ($Name -ceq 'Compiler') { 'cl.exe' } else { 'link.exe' }
                $Expected = [IO.Path]::GetFullPath((Join-Path $Directories[$Architecture] $Executable))
                if ($Selected.$Name -isnot [string] -or $Selected.$Name -cne $Expected) {
                    throw "Selected native $Name depends on ambient tool ordering."
                }
            }
            $Passed++
        }
        foreach ($Name in @('cl.exe', 'link.exe')) {
            $Tool = Join-Path $Directories[$Architecture] $Name
            $Held = "$Tool.held"
            [IO.File]::Move($Tool, $Held)
            try {
                Require-Rejection { Get-OriginalSdkToolchain $Installation $Tools $Architecture } '*Missing selected native C++ tool*'
                $Passed++
            } finally { [IO.File]::Move($Held, $Tool) }
        }
    }
    Require-Rejection {
        Get-OriginalSdkToolchain (Join-Path $Temporary 'other-vs') $Tools 'X64'
    } '*does not belong to the selected Visual Studio installation*'
    $Passed++
    foreach ($Missing in @('', 'relative-tools')) {
        Require-Rejection { Get-OriginalSdkToolchain $Installation $Missing 'Arm64' } '*directory or architecture is unavailable*'
        $Passed++
    }
    Require-Rejection { Get-OriginalSdkToolchain $Installation $Tools 'X86' } '*directory or architecture is unavailable*'
    $Passed++

    $IncludeRoot = Join-Path $Temporary 'selected-sdk/include'
    $Required = @('DirectXMath.h', 'DirectXMathMatrix.inl', 'DirectXMathVector.inl')
    foreach ($Case in @('Original', 'Lower', 'Upper')) {
        $Includes = @($Required | ForEach-Object {
            $Name = if ($Case -ceq 'Lower') { $_.ToLowerInvariant() } elseif ($Case -ceq 'Upper') { $_.ToUpperInvariant() } else { $_ }
            Join-Path $IncludeRoot $Name
        })
        $Actual = @(Get-OriginalSdkHeaders $Includes)
        if ($Actual.Count -ne 3 -or (Compare-Object $Includes $Actual -CaseSensitive)) {
            throw 'Header selection changed the compiler-observed path identity.'
        }
        $Passed++
    }
    $Includes = @($Required | ForEach-Object { Join-Path $IncludeRoot $_.ToLowerInvariant() })
    for ($Index = 0; $Index -lt $Required.Count; $Index++) {
        $Missing = @($Includes | Where-Object { $_ -cne $Includes[$Index] })
        Require-Rejection { Get-OriginalSdkHeaders $Missing } '*Missing or ambiguous consumed SDK header*'
        $Passed++
        # Same filename under a second actual SDK root remains ambiguous even
        # when that spelling differs only in case from the required basename.
        $Duplicate = @($Includes) + (Join-Path (Join-Path $Temporary 'other-sdk/include') $Required[$Index])
        Require-Rejection { Get-OriginalSdkHeaders $Duplicate } '*Missing or ambiguous consumed SDK header*'
        $Passed++
    }
    $Misc = Join-Path $IncludeRoot 'directxmathmisc.inl'
    $Extra = @($Includes) + $Misc + (Join-Path $IncludeRoot 'intrin.h')
    $Actual = @(Get-OriginalSdkHeaders $Extra)
    if ($Actual.Count -ne 4 -or $Actual -cnotcontains $Misc) { throw 'Additional consumed DirectXMath header was lost.' }
    $Passed++
    $WrongBasename = @($Includes[1], $Includes[2], (Join-Path $IncludeRoot 'OtherDirectXMath.h'))
    Require-Rejection { Get-OriginalSdkHeaders $WrongBasename } '*Missing or ambiguous consumed SDK header*'
    $Passed++
    if ($Passed -ne 23) { throw "SDK selector inventory changed: $Passed instead of 23." }
    Write-Output "Passed $Passed offline SDK launcher selector controls; no compiler, SDK or renderer executed."
} finally {
    $env:PATH = $OriginalPath
    Remove-Item -LiteralPath $Temporary -Recurse -Force
}
