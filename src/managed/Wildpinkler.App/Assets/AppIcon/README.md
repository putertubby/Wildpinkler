# Wildpinkler App Icon

Fluent 2-compliant app icon representing layered files and the virtual file system concept.

## Files

- `Wildpinkler-Icon.svg` — Source vector icon (XML/SVG format)
- `Export-Icon.ps1` — PowerShell script to export PNG variants (requires ImageMagick)
- `Export-Icon.js` — Node.js script to export PNG variants (requires npm packages)
- `Wildpinkler-Icon-{16,32,48,64,128,256}.png` — Exported PNG variants (generated)

## Exporting PNG Variants

### Option 1: PowerShell + ImageMagick (Recommended for Windows)

```powershell
# Install ImageMagick (one-time)
winget install ImageMagick

# Run export script
.\Export-Icon.ps1
```

Requires: ImageMagick (https://imagemagick.org/script/download.php#windows)

### Option 2: Node.js

```bash
# Install dependencies (one-time)
npm install sharp svg2png

# Run export
node Export-Icon.js
```

Requires: Node.js and npm

### Option 3: Manual Export (Online)

Use an online SVG to PNG converter:
1. Upload `Wildpinkler-Icon.svg` to https://image.online-convert.com/convert-to-png or similar
2. Set output size to each required dimension (16, 32, 48, 64, 128, 256)
3. Download and save as `Wildpinkler-Icon-{size}.png`

Requires: Web browser, no local tools

### Option 4: Inkscape GUI

1. Open `Wildpinkler-Icon.svg` in Inkscape (free, https://inkscape.org)
2. For each size:
   - File → Export As → Export Image
   - Set size (16px, 32px, etc.) and filename
   - Click "Export"

Requires: Inkscape desktop application

## Design Notes

- **Canvas**: 256×256 px with 16px safe margin
- **Grid**: 4px alignment for crisp rendering at all scales
- **Colors**: Fluent 2 accent blue (#0078D4) with layered opacity
- **Concept**: 3 stacked rectangles representing layers/VFS merging
- **Theme Support**: Clean geometry works in Light/Dark/HighContrast modes

## Integration

The PNG files are automatically copied to the build output via `Wildpinkler.App.csproj`:

```xml
<Content Include="Assets/AppIcon/**/*.png" CopyToOutputDirectory="PreserveNewest" />
```

The app icon is set in `MainWindow.xaml.cs` using the Windows App SDK icon API.

## Quality Checklist

Before committing, verify:
- [ ] SVG renders correctly in browser
- [ ] All 6 PNG sizes generated without errors
- [ ] 16×16 and 32×32 variants remain legible (no blur/distortion)
- [ ] Icon appears in app's taskbar and title bar after build
- [ ] Icon remains visible in Light/Dark/HighContrast themes
- [ ] Contrast ratio ≥ 3:1 against standard backgrounds
