#!/usr/bin/env node
/**
 * Wildpinkler Icon Exporter - Node.js version
 * 
 * Converts Wildpinkler-Icon.svg to PNG at multiple resolutions
 * Prerequisites: npm packages 'sharp' and 'svg2png'
 * 
 * Installation:
 *   npm install sharp svg2png
 * 
 * Usage:
 *   node Export-Icon.js
 *   node Export-Icon.js --svg ./custom-icon.svg
 */

const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const args = process.argv.slice(2);
const svgPath = args.includes('--svg') 
    ? args[args.indexOf('--svg') + 1] 
    : path.join(__dirname, 'Wildpinkler-Icon.svg');

const sizes = [16, 32, 48, 64, 128, 256];
const outputDir = __dirname;

if (!fs.existsSync(svgPath)) {
    console.error(`❌ SVG file not found: ${svgPath}`);
    process.exit(1);
}

console.log(`📦 Exporting Wildpinkler icon...`);
console.log(`📄 Source: ${svgPath}`);
console.log(`📁 Output: ${outputDir}\n`);

(async () => {
    try {
        const svgData = fs.readFileSync(svgPath);
        
        for (const size of sizes) {
            const outputFile = path.join(outputDir, `Wildpinkler-Icon-${size}.png`);
            process.stdout.write(`   Generating ${size}x${size}... `);
            
            await sharp(svgData, { density: 300 })
                .resize(size, size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
                .png({ quality: 100 })
                .toFile(outputFile);
            
            const stats = fs.statSync(outputFile);
            console.log(`✓ (${stats.size} bytes)`);
        }
        
        console.log(`\n✓ Export complete! Generated:`);
        sizes.forEach(size => {
            const file = path.join(outputDir, `Wildpinkler-Icon-${size}.png`);
            if (fs.existsSync(file)) {
                const stats = fs.statSync(file);
                console.log(`   - Wildpinkler-Icon-${size}.png (${stats.size} bytes)`);
            }
        });
    } catch (err) {
        console.error(`❌ Export failed: ${err.message}`);
        process.exit(1);
    }
})();
