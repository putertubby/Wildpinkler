#Requires -Version 5.1
<#
.SYNOPSIS
    Restores and builds the Wildpinkler solution (x64) using the MSBuild from the installed Visual Studio.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('Build', 'Rebuild', 'Clean')]
    [string]$Target = 'Build'
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2022 - see the 'Build Environment Installation' section in spec.md."
}

$msbuild = & $vswhere -latest -products * `
    -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1

if (-not $msbuild) {
    throw "No Visual Studio 2022 installation with the C++ and MSBuild components was found. See the 'Build Environment Installation' section in spec.md."
}

$solution = Join-Path $PSScriptRoot 'Wildpinkler.sln'

Write-Host "MSBuild:       $msbuild"
Write-Host "Solution:      $solution"
Write-Host "Configuration: $Configuration|x64"

& $msbuild $solution "/t:$Target" "/p:Configuration=$Configuration" '/p:Platform=x64' '/restore' '/m' '/nologo' '/v:minimal'
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Host "Output: $(Join-Path $PSScriptRoot "build\bin\x64\$Configuration")"
