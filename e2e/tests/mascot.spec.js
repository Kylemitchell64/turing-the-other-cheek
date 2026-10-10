import { test, expect } from '@playwright/test';

// Phase 28: the menu mascot has a temper. Pokes and drags are "nudges"; the 11th nudge
// inside 30s makes it crash out (glitch -> sprint off the nearest edge -> fall in from the
// top -> faceplant -> dust off -> back to wandering, calm). This drives the real pointer
// path with the real physics (rAF gravity), which the unit-less browser pane can't.

async function guestLogin(page, name) {
  await page.goto('/login');
  await page.getByPlaceholder('pick a name').fill(name);
  await page.getByRole('button', { name: 'PLAY' }).click();
  await page.getByRole('button', { name: 'CONTINUE AS GUEST' }).click();
  const skip = page.getByRole('button', { name: 'skip for now' });
  const createLobby = page.getByRole('button', { name: 'create lobby' });
  await Promise.race([
    skip.waitFor({ state: 'visible' }),
    createLobby.waitFor({ state: 'visible' }),
  ]);
  if (await skip.isVisible()) await skip.click();
  await expect(createLobby).toBeVisible();
}

// The sprite wanders, so aim at its live bounding box every time. A tap with a button
// under it belongs to the button (phase 31), so only tap when the spot is clear — otherwise
// a stray poke could open "join" or similar mid-test.
async function poke(page) {
  for (let i = 0; i < 200; i++) {
    const spot = await clearSpot(page);
    if (spot) { await page.mouse.click(spot.x, spot.y); return; }
    await page.waitForTimeout(100); // it'll wander off the button soon
  }
  throw new Error('the mascot never left the buttons alone');
}
function clearSpot(page) {
  return page.evaluate(() => {
    const r = document.querySelector('.home-robot-walker').getBoundingClientRect();
    const x = r.left + r.width / 2;
    const y = r.top + r.height / 2;
    const under = document.elementFromPoint(x, y);
    if (under && under.closest('button, a, input, textarea, select, label, [role="button"], .music-widget')) return null;
    return { x, y };
  });
}

// Press on the mascot and lift it a little. It wanders, so the box read and the press can
// race on a slow runner; a missed press touches nothing, so just try again.
async function grab(page) {
  const body = page.locator('.wanderer-body');
  for (let i = 0; i < 4; i++) {
    const box = await page.locator('.home-robot-walker').boundingBox();
    const sx = box.x + box.width / 2;
    const sy = box.y + box.height / 2;
    await page.mouse.move(sx, sy);
    await page.mouse.down();
    await page.mouse.move(sx + 20, sy - 30, { steps: 3 });
    if (await body.evaluate((el) => el.classList.contains('held'))) return { box, sx, sy };
    await page.mouse.up();
    await page.waitForTimeout(200);
  }
  throw new Error('could not pick the mascot up');
}

// Phase 32: the crash-out ending is random; the page reads window.__mascotTest to force one.
async function forceEnding(page, ending, extra = {}) {
  await page.addInitScript((hook) => { window.__mascotTest = hook; }, { ending, ...extra });
}

// eleven pokes in a row = crash-out. It wanders between the box read and the click, so a
// poke can miss; keep going until it actually snaps (it never needs more than eleven hits).
async function crashIt(page) {
  const body = page.locator('.wanderer-body');
  for (let i = 0; i < 30; i++) {
    if (await body.evaluate((el) => /crash-/.test(el.className))) return;
    await poke(page);
    await page.waitForTimeout(150);
  }
}

test('poking the mascot annoys it, then it crashes out and recovers', async ({ page }) => {
  await forceEnding(page, 'portal');
  await guestLogin(page, 'mascot_poker');
  const body = page.locator('.wanderer-body');
  await expect(body).toHaveClass(/mood-calm/);

  await poke(page);
  await expect(body).toHaveClass(/mood-annoyed/);
  await expect(page.locator('.wanderer-mutter')).toHaveText('?');

  for (let i = 0; i < 3; i++) { await poke(page); await page.waitForTimeout(150); }
  await expect(body).toHaveClass(/mood-irritated/);

  for (let i = 0; i < 3; i++) { await poke(page); await page.waitForTimeout(150); }
  await expect(body).toHaveClass(/mood-angry/);

  // three more brings the window count to 10; the eleventh is one too many
  for (let i = 0; i < 3; i++) { await poke(page); await page.waitForTimeout(150); }
  await poke(page);

  await expect(body).toHaveClass(/crash-glitch/);
  await expect(page.locator('.wfx-glitch .wfx-p').first()).toBeVisible();
  await expect(body).toHaveClass(/crash-flee/);
  await expect(page.locator('.home-robot')).toHaveClass(/crashing/);
  await expect(body).toHaveClass(/crash-fall/);
  await expect(body).toHaveClass(/crash-down/);
  await expect(page.locator('.wfx-stars .wfx-p').first()).toBeVisible();
  await expect(body).toHaveClass(/crash-dust/);
  await expect(body).not.toHaveClass(/crash-/);
  await expect(body).toHaveClass(/mood-calm/);
  await expect(page.locator('.home-robot')).not.toHaveClass(/crashing/);

  // it landed back on the floor, upright, inside the screen
  const box = await page.locator('.home-robot-walker').boundingBox();
  const vw = page.viewportSize().width;
  expect(box.x).toBeGreaterThan(-5);
  expect(box.x + box.width).toBeLessThan(vw + 5);
});

test('dragging the mascot picks it up and dropping it counts as a nudge', async ({ page }) => {
  await guestLogin(page, 'mascot_lifter');
  const walker = page.locator('.home-robot-walker');
  const body = page.locator('.wanderer-body');

  const { box, sx, sy } = await grab(page);
  await page.mouse.move(sx + 60, sy - 80, { steps: 8 });
  await page.mouse.move(sx + 160, sy - 120, { steps: 8 });
  await expect(body).toHaveClass(/held/);
  // hold still before letting go: since phase 32 a release mid-swing is a throw
  await page.waitForTimeout(250);
  await page.mouse.up();

  // falls, lands, gets annoyed about it
  await expect(body).not.toHaveClass(/held/);
  await expect(body).toHaveClass(/mood-annoyed/);
  const after = await walker.boundingBox();
  expect(after.x).toBeGreaterThan(box.x + 60);
});

// ---- phase 32 ----

test('throwing the mascot sends it flying, and a hard throw counts double', async ({ page }) => {
  await guestLogin(page, 'mascot_thrower');
  const walker = page.locator('.home-robot-walker');
  const body = page.locator('.wanderer-body');

  const { box, sx, sy } = await grab(page);
  await expect(body).toHaveClass(/held/);
  // a fast flick up and to the right, released mid-swing
  await page.mouse.move(sx + 320, sy - 330);
  await page.mouse.up();

  await expect(body).toHaveClass(/thrown/);
  // it really flies: well above where it was standing (it may bounce off a wall, so the
  // sideways distance isn't a fixed number)
  const floorY = box.y;
  await expect.poll(async () => (await body.boundingBox()).y, { timeout: 3000, intervals: [30] })
    .toBeLessThan(floorY - 80);
  await expect(body).not.toHaveClass(/thrown/);
  await expect(page.locator('.wanderer-mutter')).toHaveText('WHOA');
  const landed = await body.boundingBox();
  expect(landed.y).toBeGreaterThan(floorY - 20); // and came back down to the floor

  // two nudges already, so one poke makes three ("hm."), not two ("hey")
  await page.waitForTimeout(1400);
  await poke(page);
  await expect(page.locator('.wanderer-mutter')).toHaveText('hm.');
});

test('petting the mascot calms it back down', async ({ page }) => {
  await guestLogin(page, 'mascot_petter');
  const walker = page.locator('.home-robot-walker');
  const body = page.locator('.wanderer-body');

  for (let i = 0; i < 5; i++) { await poke(page); await page.waitForTimeout(150); }
  await expect(body).toHaveClass(/mood-irritated/);

  // press, wait, then rub slowly side to side across it
  const box = await walker.boundingBox();
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  await page.mouse.move(cx, cy);
  await page.mouse.down();
  await page.waitForTimeout(300);
  let x = cx;
  for (let pass = 0; pass < 8; pass++) {
    const dir = pass % 2 ? -1 : 1;
    for (let i = 0; i < 6; i++) { x += dir * 4; await page.mouse.move(x, cy); await page.waitForTimeout(20); }
  }
  await expect(body).toHaveClass(/act-pet/);
  await expect(page.locator('.wfx-hearts .wfx-p').first()).toBeAttached();
  await page.mouse.up();
  await expect(body).toHaveClass(/mood-calm/);
  await expect(body).not.toHaveClass(/act-pet/);
});

test('tapping the empty floor drops a snack it walks over and eats', async ({ page }) => {
  await guestLogin(page, 'mascot_feeder');
  const body = page.locator('.wanderer-body');
  await poke(page);
  await expect(body).toHaveClass(/mood-annoyed/);

  // find a spot on the floor band that is bare (no panel, nothing clickable) and not the sprite
  const spot = await page.evaluate(() => {
    const band = document.querySelector('.home-robot').getBoundingClientRect();
    const sprite = document.querySelector('.home-robot-walker').getBoundingClientRect();
    const y = band.bottom - 12;
    for (let x = window.innerWidth - 30; x > 30; x -= 25) {
      if (x > sprite.left - 60 && x < sprite.right + 60) continue;
      const el = document.elementFromPoint(x, y);
      if (el && !el.closest('button, a, input, textarea, select, label, [role="button"], .panel, .music-widget')) return { x, y };
    }
    return null;
  });
  expect(spot).not.toBeNull();
  await page.mouse.click(spot.x, spot.y);

  await expect(page.locator('.wanderer-snack')).toBeVisible();
  await expect(body).toHaveClass(/act-eat/);
  await expect(page.locator('.wanderer-snack')).toHaveCount(0);
  await expect(body).not.toHaveClass(/act-eat/);
  await expect(body).toHaveClass(/mood-calm/);
});

test('each crash-out picks an ending and leaves a scar for the session', async ({ page }) => {
  await forceEnding(page, 'ragequit');
  await guestLogin(page, 'mascot_scarred');
  const layer = page.locator('.home-robot');
  const body = page.locator('.wanderer-body');
  await expect(body).toHaveAttribute('data-scars', '0');

  // (b) rage-quit: the sign, a blink-out, a sheepish return
  await crashIt(page);
  await expect(body).toHaveClass(/crash-ragequit/);
  await expect(page.locator('.wanderer-sign')).toHaveText('CONNECTION LOST');
  await expect(body).toHaveClass(/crash-offline/);
  await expect(body).toHaveClass(/crash-sheepish/);
  await expect(body).not.toHaveClass(/crash-/);
  await expect(layer).toHaveAttribute('data-last-ending', 'ragequit');
  await expect(body).toHaveAttribute('data-scars', '1');
  await expect(page.locator('.scar-bandage')).toBeAttached();

  // (d) a second robot comes to tell YOU off
  await page.evaluate(() => { window.__mascotTest = { ending: 'scold' }; });
  await crashIt(page);
  await expect(body).toHaveClass(/crash-sulk/);
  await expect(page.locator('.wanderer-scolder')).toBeVisible();
  await expect(page.locator('.wanderer-mutter.scold')).toContainText('mascot_scarred');
  await expect(page.locator('.wanderer-scolder')).toHaveCount(0, { timeout: 15_000 });
  await expect(body).toHaveAttribute('data-scars', '2');
  await expect(page.locator('.scar-blackeye')).toBeAttached();

  // (c) packs a suitcase, leaves, comes back (away time shortened by the hook)
  await page.evaluate(() => { window.__mascotTest = { ending: 'suitcase', awayMs: 600 }; });
  await crashIt(page);
  await expect(body).toHaveClass(/crash-pack/);
  await expect(page.locator('.wanderer-suitcase')).toBeVisible();
  await expect(body).toHaveClass(/crash-away/);
  await expect(body).toHaveClass(/crash-return/);
  await expect(body).not.toHaveClass(/crash-/, { timeout: 30_000 });
  await expect(body).toHaveAttribute('data-scars', '3');

  // unforced: it still picks one of the four
  await page.evaluate(() => { window.__mascotTest = {}; });
  await crashIt(page);
  await expect(layer).toHaveAttribute('data-last-ending', /^(portal|ragequit|suitcase|scold)$/);
  await expect(body).not.toHaveClass(/crash-/, { timeout: 40_000 });
  await expect(body).toHaveAttribute('data-scars', '4');

  // scars are session-only: a reload wipes them
  await page.reload();
  await expect(page.locator('.wanderer-body')).toHaveAttribute('data-scars', '0');
});

// ---- phase 34: "most hated by the mascot", counted per phone ----

const CRASH_KEY = 'turing.mascotCrashOuts';

test('crash-outs are counted on this phone, survive a reload, and earn a hard hat', async ({ page }) => {
  await forceEnding(page, 'ragequit');
  await guestLogin(page, 'mascot_hardhat');
  const body = page.locator('.wanderer-body');

  // one short of the hard hat (seeded the way a phone that's been at it a while would be)
  await page.evaluate((k) => localStorage.setItem(k, '9'), CRASH_KEY);
  await page.reload();
  await expect(page.getByRole('button', { name: 'create lobby' })).toBeVisible();
  await expect(page.locator('.wanderer-hardhat')).toHaveCount(0);

  await crashIt(page);
  await expect(body).toHaveClass(/crash-ragequit/);
  await expect(body).not.toHaveClass(/crash-/);
  await expect(page.locator('.wanderer-hardhat')).toBeAttached();
  await expect(page.locator('.home-robot .wanderer-mutter')).toContainText('hard hat');
  expect(await page.evaluate((k) => localStorage.getItem(k), CRASH_KEY)).toBe('10');

  // unlike the scars, the count and the hat survive a reload
  await page.reload();
  await expect(page.locator('.wanderer-body')).toHaveAttribute('data-scars', '0');
  await expect(page.locator('.wanderer-hardhat')).toBeAttached();

  // the stats page shows it, clearly as a this-phone number
  await page.goto('/stats');
  await expect(page.locator('.mascot-hate')).toContainText('most hated by the mascot: 10 crash-outs (this phone)');
});

test('the per-phone counter degrades quietly when storage is blocked', async ({ page }) => {
  await page.addInitScript(() => {
    Storage.prototype.getItem = function (k) { if (String(k).startsWith('turing.mascot')) throw new Error('blocked'); return null; };
    Storage.prototype.setItem = function (k) { if (String(k).startsWith('turing.mascot')) throw new Error('blocked'); };
  });
  await forceEnding(page, 'ragequit');
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e)));
  await guestLogin(page, 'mascot_nostore');
  await crashIt(page);
  await expect(page.locator('.wanderer-body')).toHaveClass(/crash-ragequit/);
  await expect(page.locator('.wanderer-body')).not.toHaveClass(/crash-/);
  expect(errors).toEqual([]);
});
