// Снимает картинки из screens.html в docs/images через Microsoft Edge, который уже есть в Windows (без Playwright).
// node docs/art/render-edge.mjs   (нужен Node 22+: встроенные fetch и WebSocket)
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.join(here, '..', 'images');
const edge = process.env.EDGE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const port = 9339, profile = mkdtempSync(path.join(tmpdir(), 'vanta-art-'));
const browser = spawn(edge, ['--headless=new', '--disable-gpu', '--hide-scrollbars', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));

let target;
for (let i = 0; i < 50 && !target; i++) { try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page'); } catch { await sleep(200); } }
if (!target) throw new Error('Edge не ответил');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail; });
let seq = 0; const waiting = new Map(), events = [];
ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && waiting.has(d.id)) { const [ok, fail] = waiting.get(d.id); waiting.delete(d.id); d.error ? fail(new Error(d.error.message)) : ok(d.result); } else if (d.method) events.push(d.method); };
const send = (method, params = {}) => new Promise((ok, fail) => { const id = ++seq; waiting.set(id, [ok, fail]); ws.send(JSON.stringify({ id, method, params })); });
const evalJs = async expr => (await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })).result.value;

try {
  await send('Page.enable');
  await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } }); // прозрачный фон, как omitBackground
  // social — превью репозитория для GitHub (1280×640, 1x); остальное — 2x для чёткости.
  for (const [ids, scale] of [[['hero', 'tab-record', 'tab-replay', 'tab-clips'], 2], [['social'], 1]]) {
    await send('Emulation.setDeviceMetricsOverride', { width: 1800, height: 1000, deviceScaleFactor: scale, mobile: false });
    events.length = 0;
    await send('Page.navigate', { url: pathToFileURL(path.join(here, 'screens.html')).href });
    for (let i = 0; i < 100 && !events.includes('Page.loadEventFired'); i++) await sleep(100);
    await evalJs('document.fonts.ready.then(() => true)');
    for (const id of ids) {
      const r = await evalJs(`(() => { const b = document.getElementById(${JSON.stringify(id)}).getBoundingClientRect(); return { x: b.left + scrollX, y: b.top + scrollY, width: b.width, height: b.height }; })()`);
      const shot = await send('Page.captureScreenshot', { format: 'png', clip: { ...r, scale: 1 }, captureBeyondViewport: true });
      writeFileSync(path.join(out, id + '.png'), Buffer.from(shot.data, 'base64'));
      console.log(id, Math.round(r.width * scale) + '×' + Math.round(r.height * scale));
    }
  }
} finally {
  try { await send('Browser.close'); } catch {}
  ws.close(); await sleep(500); browser.kill();
  try { rmSync(profile, { recursive: true, force: true }); } catch {}
}
