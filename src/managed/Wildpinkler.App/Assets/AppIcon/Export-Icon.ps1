# Export Wildpinkler Icon SVG to PNG at multiple resolutions
# Prerequisites: ImageMagick (magick.exe) installed and in PATH
# Installation: `winget install ImageMagick` or https://imagemagick.org/script/download.php#windows

param(
    [string]$SvgPath = "$PSScriptRoot\Wildpinkler-Icon.svg",
    [string]$OutputDir = $PSScriptRoot,
    [array]$Sizes = @(16, 32, 48, 64, 128, 256),
    # Sizes to bundle into Wildpinkler-Icon.ico. The largest embedded image
    # must be 256 or smaller (the ICO format limit), which is already the
    # case for the default sizes.
    [array]$IcoSizes = @(16, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = "Stop"

# Verify SVG exists
if (-not (Test-Path $SvgPath)) {
    Write-Error "SVG file not found: $SvgPath"
    exit 1
}

# Check for ImageMagick
$magick = Get-Command magick -ErrorAction SilentlyContinue
if (-not $magick) {
    Write-Error @"
ImageMagick is not installed or not in PATH.
Install via: winget install ImageMagick.ImageMagick
Or download: https://imagemagick.org/script/download.php#windows
"@
    exit 1
}

Write-Host "Exporting Wildpinkler icon from SVG..."
Write-Host "Source: $SvgPath"
Write-Host "Output: $OutputDir"
Write-Host ""

# Export at each size
foreach ($size in $Sizes) {
    $outputFile = Join-Path $OutputDir "Wildpinkler-Icon-${size}.png"
    Write-Host "Generating ${size}x${size}... " -NoNewline
    
    try {
        # Use ImageMagick to convert SVG to PNG with anti-aliasing
        # -background none: preserve transparency
        # -density 300: high resolution for clean conversion
        & magick `
            -background none `
            -density 300 `
            -resize "${size}x${size}" `
            -unsharp 1x1 `
            $SvgPath `
            $outputFile
        
        $fileInfo = Get-Item $outputFile
        Write-Host "($($fileInfo.Length) bytes)"
    }
    catch {
        Write-Error "Failed to generate ${size}x${size}: $_"
        exit 1
    }
}

# Bundle the exported PNGs into a multi-size .ico for Windows.
# AppWindow.SetIcon() requires a .ico (PNG is silently ignored), and the
# csproj embeds this as the exe application icon.
$icoFile = Join-Path $OutputDir "Wildpinkler-Icon.ico"
$icoSources = $IcoSizes | ForEach-Object { Join-Path $OutputDir "Wildpinkler-Icon-${_}.png" }

$missing = $icoSources | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Warning "Skipping ICO: missing PNG source(s): $($missing -join ', ')"
}
else {
    Write-Host ""
    Write-Host "Generating Wildpinkler-Icon.ico..."
    try {
        & magick $icoSources $icoFile
        $icoInfo = Get-Item $icoFile
        Write-Host "  - $icoFile ($($icoInfo.Length) bytes)"
    }
    catch {
        Write-Error "Failed to generate ${icoFile}: $_"
        exit 1
    }
}

Write-Host ""
Write-Host "Export complete! Generated:"
Get-ChildItem $OutputDir -Filter "Wildpinkler-Icon-*.png" | 
    ForEach-Object { Write-Host "  - $($_.Name) ($($_.Length) bytes)" }
if (Test-Path $icoFile) {
    Write-Host "  - $((Get-Item $icoFile).Name) ($((Get-Item $icoFile).Length) bytes)"
}
