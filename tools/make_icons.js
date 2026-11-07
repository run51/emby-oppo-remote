// Renders tools/icon.svg to the Android launcher icons and the web remote icons.
// Needs Node.js and Playwright with Chromium (require('playwright')). Usage: node tools/make_icons.js
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const root = path.join(__dirname, '..');
const svg = fs.readFileSync(path.join(__dirname, 'icon.svg'), 'utf8');
const targets = [
  ['android/res/mipmap-mdpi/ic_launcher.png', 48],
  ['android/res/mipmap-hdpi/ic_launcher.png', 72],
  ['android/res/mipmap-xhdpi/ic_launcher.png', 96],
  ['android/res/mipmap-xxhdpi/ic_launcher.png', 144],
  ['android/res/mipmap-xxxhdpi/ic_launcher.png', 192],
  ['src/Emby.Oppo/Remote/icon192.png', 192],
  ['src/Emby.Oppo/Remote/icon512.png', 512],
];

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  for (const [file, size] of targets) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('<svg ', `<svg width="${size}" height="${size}" `)}</body></html>`);
    await page.screenshot({ path: path.join(root, file), omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
    console.log(file, size);
  }
  await browser.close();
})();
