# Export Wildpinkler Icon SVG to PNG at multiple resolutions
# Prerequisites: ImageMagick (magick.exe) installed and in PATH
# Installation: `winget install ImageMagick` or https://imagemagick.org/script/download.php#windows

param(
    [string]$SvgPath = "$PSScriptRoot\Wildpinkler-Icon.svg",
    [string]$OutputDir = $PSScriptRoot,
    [array]$Sizes = @(16, 32, 48, 64, 128, 256)
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
Install via: winget install ImageMagick
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
        & magick convert `
            -background none `
            -density 300 `
            -resize "${size}x${size}" `
            -unsharp 1x1 `
            $SvgPath `
            $outputFile
        
        $fileInfo = Get-Item $outputFile
        Write-Host "✓ ($($fileInfo.Length) bytes)"
    }
    catch {
        Write-Error "Failed to generate ${size}x${size}: $_"
        exit 1
    }
}

Write-Host ""
Write-Host "✓ Export complete! Generated:"
Get-ChildItem $OutputDir -Filter "Wildpinkler-Icon-*.png" | 
    ForEach-Object { Write-Host "  - $($_.Name) ($($_.Length) bytes)" }
