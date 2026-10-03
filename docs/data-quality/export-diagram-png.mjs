// Exports the diagram SVG out of a delivered Archify HTML artifact as a high-resolution
// PNG, for embedding as a figure in RCRAInfo-Data-Quality-Observations-Explained.docx.
//
// Archify exports only from inside the browser, so this drives the same headless Chrome
// the skill's visual-check uses, clips to the diagram SVG, and skips the viewer chrome
// (title bar, theme buttons, zoom controls) that has no place in a Word figure.
//
//   node docs/data-quality/export-diagram-png.mjs
//
// Reads  docs/data-quality/diagrams/*.html   (delivered artifacts, never modified)
// Writes docs/data-quality/img/*.png

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
const skill = path.join(repoRoot, '.claude', 'skills', 'archify');
const { findChrome, ChromeVisualBrowser } = await import(
  pathToFileURL(path.join(skill, 'bin', 'visual-check.mjs')).href
);

const DIAGRAMS = ['01-three-outcomes', '02-writers-and-readers', '03-triage'];
const SCALE = 3;            // 3x for print-quality figures at Word's 96 dpi page scale
const VIEWPORT = { width: 1600, height: 1000 };

const outDir = path.join(here, 'img');
fs.mkdirSync(outDir, { recursive: true });

const found = findChrome();
const chromePath = typeof found === 'string' ? found : found?.executable || found?.path;
if (!chromePath) {
  console.error('Chrome not found; cannot export PNG.');
  process.exit(2);
}

const browser = new ChromeVisualBrowser(chromePath);
const { cdp } = browser;
const sessionId = await browser.sessionPromise;

const evaluate = async (expression) => {
  const response = await cdp.send('Runtime.evaluate', {
    expression, awaitPromise: true, returnByValue: true,
  }, sessionId);
  if (response.exceptionDetails) {
    throw new Error(response.exceptionDetails.exception?.description || 'evaluate failed');
  }
  return response.result?.value;
};

try {
  for (const name of DIAGRAMS) {
    const artifact = path.join(here, 'diagrams', `${name}.html`);
    await cdp.send('Emulation.setDeviceMetricsOverride', {
      ...VIEWPORT, deviceScaleFactor: 1, mobile: false,
    }, sessionId);

    const url = new URL(pathToFileURL(artifact).href);
    url.searchParams.set('theme', 'light');
    const loaded = cdp.waitFor('Page.loadEventFired', sessionId);
    const navigation = await cdp.send('Page.navigate', { url: url.href }, sessionId);
    if (navigation.errorText) throw new Error(`navigation failed: ${navigation.errorText}`);
    await loaded;

    // Freeze motion, ask for the readable detail level, drop the viewer's own controls
    // (Archify marks them `no-print`), and wait for webfonts so the text in the PNG
    // matches what validate measured. Then crop to the drawn content rather than the
    // whole viewBox, whose spare margin exists to satisfy the one-screen HTML layout.
    const rect = await evaluate(`(async function () {
      document.documentElement.setAttribute('data-motion', 'still');
      var panel = document.querySelector('.diagram-container');
      if (panel) panel.setAttribute('data-detail-level', 'read');
      document.querySelectorAll('.no-print').forEach(function (el) { el.style.display = 'none'; });
      if (document.fonts && document.fonts.ready) { try { await document.fonts.ready; } catch (e) {} }
      await new Promise(function (r) { requestAnimationFrame(function () { requestAnimationFrame(r); }); });

      var svg = null;
      document.querySelectorAll('svg').forEach(function (candidate) {
        var r = candidate.getBoundingClientRect();
        if (!svg || r.width * r.height > svg.getBoundingClientRect().width * svg.getBoundingClientRect().height) svg = candidate;
      });
      if (!svg) return null;

      var pad = 10;                     // user units of breathing room around the content
      var box = null;
      Array.prototype.forEach.call(svg.children, function (child) {
        var tag = child.tagName.toLowerCase();
        if (tag === 'defs' || tag === 'title' || tag === 'desc') return;
        if (tag === 'rect' && child.getAttribute('width') === '100%') return;  // background grid
        var b;
        try { b = child.getBBox(); } catch (e) { return; }
        if (!b || (!b.width && !b.height)) return;
        if (!box) { box = { x1: b.x, y1: b.y, x2: b.x + b.width, y2: b.y + b.height }; return; }
        box.x1 = Math.min(box.x1, b.x); box.y1 = Math.min(box.y1, b.y);
        box.x2 = Math.max(box.x2, b.x + b.width); box.y2 = Math.max(box.y2, b.y + b.height);
      });
      if (!box) return null;

      var ctm = svg.getScreenCTM();
      var pt = svg.createSVGPoint();
      pt.x = box.x1 - pad; pt.y = box.y1 - pad;
      var topLeft = pt.matrixTransform(ctm);
      pt.x = box.x2 + pad; pt.y = box.y2 + pad;
      var bottomRight = pt.matrixTransform(ctm);
      return {
        x: topLeft.x + window.scrollX,
        y: topLeft.y + window.scrollY,
        width: bottomRight.x - topLeft.x,
        height: bottomRight.y - topLeft.y,
      };
    })()`);
    if (!rect) throw new Error(`no svg content found in ${name}.html`);

    const capture = await cdp.send('Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: true,
      clip: {
        x: Math.round(rect.x), y: Math.round(rect.y),
        width: Math.round(rect.width), height: Math.round(rect.height),
        scale: SCALE,
      },
    }, sessionId);

    const target = path.join(outDir, `${name}.png`);
    fs.writeFileSync(target, Buffer.from(capture.data, 'base64'));
    const px = `${Math.round(rect.width * SCALE)}x${Math.round(rect.height * SCALE)}`;
    console.log(`${path.relative(repoRoot, target)}  ${px}  ${fs.statSync(target).size} bytes`);
  }
} finally {
  await browser.close();
}
