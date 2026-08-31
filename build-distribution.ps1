#Requires -Version 5.1
<#
.SYNOPSIS
    Creates the framework-dependent x64 ZIP and Inno Setup installer from a validated release stage.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$VfsAssetsPath,

    [string]$InnoSetupPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\managed\Wildpinkler.App\Wildpinkler.App.csproj'
$installerSource = Join-Path $repoRoot 'installer\Wildpinkler.iss'
$vfsAssets = [System.IO.Path]::GetFullPath($VfsAssetsPath)
$distributionRoot = Join-Path $repoRoot "build\distribution\$Version"
$publishDirectory = Join-Path $distributionRoot 'publish'
$stageDirectory = Join-Path $distributionRoot 'stage\Wildpinkler'
$artifactsDirectory = Join-Path $distributionRoot 'artifacts'
$nativeOutputDirectory = Join-Path $repoRoot 'build\bin\x64\Release'

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Copy-DirectoryContent([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Find-InnoSetupCompiler {
    if ($InnoSetupPath) {
        Require-File $InnoSetupPath 'Inno Setup compiler'
        return [System.IO.Path]::GetFullPath($InnoSetupPath)
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) {
        throw 'Inno Setup 6 was not found. Install it or pass -InnoSetupPath with the full path to ISCC.exe.'
    }

    return $compiler
}

if (-not (Test-Path -LiteralPath $vfsAssets -PathType Container)) {
    throw "The VFS asset directory does not exist: $vfsAssets"
}

Require-File (Join-Path $vfsAssets 'uufs64ldr.exe') 'Required VFS loader'
Require-File (Join-Path $vfsAssets 'uufs64.dll') 'Required VFS shim'
Require-File $installerSource 'Installer source'

if (Test-Path -LiteralPath $distributionRoot) {
    Remove-Item -LiteralPath $distributionRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory, $stageDirectory, $artifactsDirectory -Force | Out-Null

& (Join-Path $repoRoot 'build.ps1') -Configuration Release

$queryDll = Join-Path $nativeOutputDirectory 'wildpinkler_vfs.dll'
Require-File $queryDll 'Release VFS query DLL'

& dotnet publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $publishDirectory `
    "-p:Version=$Version" `
    "-p:VfsAssetsPath=$vfsAssets"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-DirectoryContent $publishDirectory $stageDirectory
Copy-Item -LiteralPath $queryDll -Destination $stageDirectory -Force

$requiredFiles = @(
    'Wildpinkler.App.exe',
    'Wildpinkler.App.dll',
    'Wildpinkler.App.deps.json',
    'Wildpinkler.App.runtimeconfig.json',
    'Wildpinkler.Interop.dll',
    'wildpinkler_vfs.dll',
    'uufs64ldr.exe',
    'uufs64.dll',
    'Assets\GameDefinitions',
    'Assets\ToolDefinitions',
    'Assets\AppIcon'
)
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $stageDirectory $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "The release stage is missing required content: $relativePath"
    }
}

$debugArtifacts = Get-ChildItem -LiteralPath $stageDirectory -Recurse -File -Include '*.pdb', '*.lib', '*.exp'
if ($debugArtifacts) {
    $paths = $debugArtifacts | ForEach-Object { $_.FullName } | Out-String
    throw "The release stage contains build-only artifacts:$paths"
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $stageDirectory 'Wildpinkler.App.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.tfm -notlike 'net8.0-windows*') {
    throw 'The staged app is not targeting .NET 8 for Windows.'
}

$zipPath = Join-Path $artifactsDirectory "Wildpinkler-$Version-win-x64.zip"
Compress-Archive -LiteralPath $stageDirectory -DestinationPath $zipPath -CompressionLevel Optimal

$compiler = Find-InnoSetupCompiler
& $compiler "/DAppVersion=$Version" "/DSourceDir=$stageDirectory" "/DOutputDir=$artifactsDirectory" $installerSource
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $artifactsDirectory "Wildpinkler-$Version-win-x64-setup.exe"
Require-File $installerPath 'Installer output'

Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath, $installerPath |
    ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$($_.Path | Split-Path -Leaf)" } |
    Set-Content -LiteralPath (Join-Path $artifactsDirectory "Wildpinkler-$Version-win-x64.sha256") -Encoding Ascii

Write-Host "Stage:     $stageDirectory"
Write-Host "Artifacts: $artifactsDirectory"