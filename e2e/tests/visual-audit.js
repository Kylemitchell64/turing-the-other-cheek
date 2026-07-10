// Shared visual-audit toolkit for the mobile E2E (phase 24). The idea: instead of
// eyeballing screenshots, every screen gets swept by in-page checks that flag the
// stuff that actually looks broken on a phone — sideways scroll, things poking off
// the edge, text getting chopped, buttons sitting on top of each other, tap targets
// you need tweezers for. Each check returns findings as plain objects so the spec
// can collect them across the whole walk and dump one readable report at the end.

import fs from 'fs';
import path from 'path';

export const SHOT_DIR = path.resolve('visual-report');

// Runs inside the page. Returns an array of {kind, detail} findings for this screen.
// Tolerances are deliberate: a 1px overhang is subpixel rounding, not a bug.
function scanForVisualIssues() {
  const findings = [];
  const vw = window.innerWidth;
  const vh = window.innerHeight;

  // Short human-readable locator for an offending element.
  const describe = (el) => {
    const tag = el.tagName.toLowerCase();
    const id = el.id ? `#${el.id}` : '';
    const cls = el.classList.length ? '.' + [...el.classList].slice(0, 3).join('.') : '';
    const text = (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40);
    return `<${tag}${id}${cls}> "${text}"`;
  };

  const isVisible = (el) => {
    const s = getComputedStyle(el);
    if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity === 0) return false;
    const r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1;
  };

  // 1) Whole-page horizontal overflow — the classic "page wiggles sideways" bug.
  const docWidth = document.documentElement.scrollWidth;
  if (docWidth > vw + 1) {
    findings.push({
      kind: 'page-overflow-x',
      detail: `document scrollWidth ${docWidth}px > viewport ${vw}px (page scrolls sideways)`,
    });
  }

  const all = [...document.querySelectorAll('body *')].filter(isVisible);

  // 2) Elements sticking out past the viewport's right/left edge.
  for (const el of all) {
    const r = el.getBoundingClientRect();
    // Ignore things that are *entirely* offscreen (animation homes, transitions).
    if (r.right <= 0 || r.left >= vw) continue;
    if (r.right > vw + 2 || r.left < -2) {
      // Skip if a scrollable ancestor intentionally contains it.
      let scrollable = false;
      for (let p = el.parentElement; p; p = p.parentElement) {
        const s = getComputedStyle(p);
        if (/(auto|scroll)/.test(s.overflowX)) { scrollable = true; break; }
      }
      if (!scrollable) {
        findings.push({
          kind: 'element-past-edge',
          detail: `${describe(el)} spans ${Math.round(r.left)}..${Math.round(r.right)}px (viewport 0..${vw})`,
        });
      }
    }
  }

  // 3) Clipped text — content wider than its box with no ellipsis and no way to scroll.
  for (const el of all) {
    if (!el.childNodes.length) continue;
    const hasText = [...el.childNodes].some(
      (n) => n.nodeType === 3 && n.textContent.trim().length > 2
    );
    if (!hasText) continue;
    const s = getComputedStyle(el);
    if (s.textOverflow === 'ellipsis') continue; // intentional truncation
    if (!/(hidden|clip)/.test(s.overflowX) && !/(hidden|clip)/.test(s.overflow)) continue;
    if (el.scrollWidth > el.clientWidth + 4) {
      findings.push({
        kind: 'clipped-text',
        detail: `${describe(el)} text needs ${el.scrollWidth}px but box is ${el.clientWidth}px`,
      });
    }
  }

  // 4) Interactive elements overlapping each other (mis-taps waiting to happen).
  const clickables = all.filter(
    (el) =>
      (el.matches('button, a, input, select, textarea, [role="button"]')) &&
      !el.disabled
  );
  for (let i = 0; i < clickables.length; i++) {
    for (let j = i + 1; j < clickables.length; j++) {
      const a = clickables[i], b = clickables[j];
      if (a.contains(b) || b.contains(a)) continue;
      const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
      const ox = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
      const oy = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
      if (ox <= 0 || oy <= 0) continue;
      const overlap = ox * oy;
      const smaller = Math.min(ra.width * ra.height, rb.width * rb.height);
      if (smaller > 0 && overlap / smaller > 0.25) {
        findings.push({
          kind: 'overlapping-controls',
          detail: `${describe(a)} overlaps ${describe(b)} by ${Math.round((overlap / smaller) * 100)}%`,
        });
      }
    }
  }

  // 5) Tap targets smaller than ~36px in either dimension (guideline is 44, but the
  // retro UI runs compact — 36 catches the genuinely hostile ones without drowning
  // the report). Inputs get a pass on width since they stretch with the layout.
  for (const el of clickables) {
    const r = el.getBoundingClientRect();
    if (r.bottom < 0 || r.top > vh) continue; // offscreen rows in scrollback etc.
    const tooNarrow = r.width < 36 && !el.matches('input, textarea');
    const tooShort = r.height < 28;
    if (tooNarrow || tooShort) {
      findings.push({
        kind: 'tiny-tap-target',
        detail: `${describe(el)} is ${Math.round(r.width)}x${Math.round(r.height)}px`,
      });
    }
  }

  return findings;
}

// Sweep one screen: run the in-page scan, screenshot it, and fold results into the
// shared findings list keyed by the screen label.
export async function auditScreen(page, label, findings) {
  // Let layout/animations settle so we don't flag mid-transition positions.
  await page.waitForTimeout(400);
  const issues = await page.evaluate(scanForVisualIssues);

  fs.mkdirSync(SHOT_DIR, { recursive: true });
  const shot = path.join(SHOT_DIR, `${label.replace(/[^a-z0-9-]/gi, '_')}.png`);
  await page.screenshot({ path: shot, fullPage: false });

  for (const issue of issues) findings.push({ screen: label, ...issue });
}

// Hook console + page errors for a page; they land in the same findings list.
export function trackErrors(page, label, findings) {
  page.on('pageerror', (e) =>
    findings.push({ screen: label, kind: 'page-error', detail: String(e).slice(0, 200) })
  );
  page.on('console', (msg) => {
    if (msg.type() !== 'error') return;
    const text = msg.text();
    // Vite HMR/network noise isn't a UI bug.
    if (/favicon|ERR_ABORTED|WebSocket closed/i.test(text)) return;
    findings.push({ screen: label, kind: 'console-error', detail: text.slice(0, 200) });
  });
}

// Pretty-print everything we found, grouped by screen, and write the JSON alongside
// the screenshots so a human (or the next session) can pick through it.
export function reportFindings(findings) {
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  fs.writeFileSync(
    path.join(SHOT_DIR, 'findings.json'),
    JSON.stringify(findings, null, 2)
  );
  if (!findings.length) return 'no visual issues found';
  const byScreen = {};
  for (const f of findings) (byScreen[f.screen] ??= []).push(f);
  const lines = [`${findings.length} visual issue(s) across ${Object.keys(byScreen).length} screen(s):`];
  for (const [screen, list] of Object.entries(byScreen)) {
    lines.push(`\n  [${screen}]`);
    for (const f of list) lines.push(`    - ${f.kind}: ${f.detail}`);
  }
  return lines.join('\n');
}
