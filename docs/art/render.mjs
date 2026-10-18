// Снимает картинки из screens.html в docs/images. Нужен Playwright (npm i -D playwright).
// node docs/art/render.mjs
import { chromium } from 'playwright';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.join(here, '..', 'images');
const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
// social — превью репозитория для GitHub (1280×640, 1x); остальное — 2x для чёткости.
for (const [ids, scale] of [[['hero', 'tab-record', 'tab-replay', 'tab-clips'], 2], [['social'], 1]]) {
  const page = await browser.newPage({ viewport: { width: 1800, height: 1000 }, deviceScaleFactor: scale });
  await page.goto(pathToFileURL(path.join(here, 'screens.html')).href);
  await page.evaluate(() => document.fonts.ready);
  for (const id of ids) {
    await page.locator('#' + id).screenshot({ path: path.join(out, id + '.png'), omitBackground: true });
    console.log(id);
  }
  await page.close();
}
await browser.close();
