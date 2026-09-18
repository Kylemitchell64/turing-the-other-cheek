import { test } from '@playwright/test';
import { createHmac } from 'node:crypto';

// Screenshot pass: every screen at phone + desktop widths into test-results/pass/. Not an
// assertion suite — a way to eyeball the whole app at once. Run:
//   npx playwright test tests/visual-pass.spec.js --project=chromium
test.describe.configure({ mode: 'serial' });

// The E2E API signs with this key (playwright.config.js); mint an admin JWT the same way
// JwtTokenService does so the admin console can be screenshotted without Google.
const E2E_KEY = 'e2e-only-signing-key-that-is-at-least-64-characters-long-000000000';
function adminJwt() {
  const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const now = Math.floor(Date.now() / 1000);
  const payload = {
    'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': 'pass-admin-' + now,
    'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name': 'pass_admin',
    displayName: 'pass_admin', isGuest: 'false', needsUsername: 'false',
    externalProvider: 'Google', isAdmin: 'true',
    nbf: now, exp: now + 3600, iat: now, iss: 'turing-api', aud: 'turing-app',
  };
  const signing = `${b64({ alg: 'HS256', typ: 'JWT' })}.${b64(payload)}`;
  const sig = createHmac('sha256', E2E_KEY).update(signing).digest('base64url');
  return `${signing}.${sig}`;
}

async function guestLogin(page, name) {
  await page.goto('/login');
  await page.getByPlaceholder('pick a name').fill(name);
  await page.getByRole('button', { name: 'PLAY' }).click();
  await page.getByRole('button', { name: 'CONTINUE AS GUEST' }).click();
  const skip = page.getByRole('button', { name: 'skip for now' });
  const createLobby = page.getByRole('button', { name: 'create lobby' });
  await Promise.race([skip.waitFor({ state: 'visible' }), createLobby.waitFor({ state: 'visible' })]);
  if (await skip.isVisible()) {
    await page.screenshot({ path: `test-results/pass/${page.viewportSize().width}-creator.png`, fullPage: true });
    await skip.click();
  }
}

for (const vp of [{ width: 390, height: 844 }, { width: 1400, height: 900 }]) {
  test(`screens at ${vp.width}px`, async ({ browser }) => {
    test.setTimeout(300_000);
    const ctx = await browser.newContext({ viewport: vp, hasTouch: vp.width < 600, isMobile: vp.width < 600 });
    const page = await ctx.newPage();
    const shot = (n) => page.screenshot({ path: `test-results/pass/${vp.width}-${n}.png`, fullPage: true });

    await page.goto('/login');
    await page.waitForTimeout(800);
    await shot('login');
    await guestLogin(page, `pass_${vp.width}`);
    await page.waitForTimeout(500);
    await shot('home');
    await page.getByRole('button', { name: 'join', exact: true }).click();
    await page.waitForTimeout(300);
    await shot('join');
    await page.getByRole('button', { name: 'back' }).click();

    await page.getByRole('button', { name: 'create lobby' }).click();
    await page.waitForTimeout(1200);
    await shot('lobby');

    await page.getByRole('button', { name: /solo demo/ }).click();
    await page.waitForSelector('.crt-head');
    await page.waitForTimeout(1500);
    await shot('game-prompting');
    await page.getByPlaceholder(/type something human/).fill('a very normal human answer');
    await page.getByRole('button', { name: 'submit', exact: true }).click();
    await page.waitForTimeout(600);
    await shot('game-submitted');
    await page.waitForSelector('.answers-list', { timeout: 30_000 }).catch(() => {});
    await page.waitForTimeout(400);
    await shot('game-reveal');
    await page.waitForSelector('text=/hold to accuse|out of tokens|no tokens/', { timeout: 30_000 }).catch(() => {});
    await page.waitForTimeout(800);
    await shot('game-accuse');
    // a scripted bot accusation + fake-out may fire from round 2; grab it if it does
    await page.waitForSelector('text=/FAKE-OUT WINDOW|overrule them/', { timeout: 40_000 }).then(async () => {
      await page.waitForTimeout(300);
      await shot('game-veto');
    }).catch(() => {});
    await page.waitForSelector('text=/THE AI SURVIVES|DETECTOR WINS/', { timeout: 200_000 });
    await page.waitForTimeout(500);
    await shot('game-end');
    await page.getByRole('button', { name: /see every answer/ }).click();
    await page.waitForTimeout(300);
    await shot('game-end-transcript');

    await page.getByRole('button', { name: 'back home' }).click();
    await page.waitForTimeout(500);
    await page.getByRole('button', { name: 'my stats' }).click();
    await page.waitForTimeout(800);
    await shot('stats');
    await page.goto('/samples');
    await page.waitForTimeout(800);
    await shot('samples');
    await page.goto('/character');
    await page.waitForTimeout(800);
    await shot('character');

    // admin console with a minted admin token
    await page.goto('/login');
    await page.evaluate((t) => sessionStorage.setItem('ttoc-token', t), adminJwt());
    await page.goto('/admin');
    await page.waitForSelector('text=[ SELF-CHECK ]', { timeout: 20_000 });
    await page.waitForTimeout(1200);
    await shot('admin');
    await ctx.close();
  });
}
