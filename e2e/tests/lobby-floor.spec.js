import { test, expect } from '@playwright/test';

// Phase 33: on the lobby page everyone's characters share the floor. A crash-out on one
// phone makes the other phones' mascots flinch ("did you see that?"), poking someone
// else's character makes it stomp at you, and none of it follows anyone into the game.

const NAMES = ['FloorAmy', 'FloorBen', 'FloorCal'];

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

// Tap the middle of a wandering sprite, but only when no real UI is underneath it: on the
// lobby page the floor overlaps the option buttons, and the game rightly ignores (and the
// test must not click) a tap that belongs to a button. Returns whether it tapped.
async function tapSprite(page, selector) {
  const spot = await page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const x = r.left + r.width / 2;
    const y = r.top + r.height / 2;
    const under = document.elementFromPoint(x, y);
    if (under && under.closest('button, a, input, textarea, select, label, [role="button"], .music-widget')) return null;
    return { x, y };
  }, selector);
  if (!spot) return false;
  await page.mouse.click(spot.x, spot.y);
  return true;
}

// my own mascot (the floor-mates are a separate layer with their own classes)
const poke = (page) => tapSprite(page, '.home-robot-walker');

test('the lobby floor is shared: crashes make others flinch, pokes make them stomp', async ({ browser }) => {
  const contexts = [];
  const pages = [];
  for (let i = 0; i < NAMES.length; i++) {
    const ctx = await browser.newContext();
    contexts.push(ctx);
    const page = await ctx.newPage();
    // keep the crash-out short and predictable
    await page.addInitScript(() => { window.__mascotTest = { ending: 'ragequit' }; });
    pages.push(page);
  }
  try {
    for (let i = 0; i < pages.length; i++) await guestLogin(pages[i], NAMES[i]);
    const [amy, ben] = pages;

    await amy.getByRole('button', { name: 'create lobby' }).click();
    await expect(amy).toHaveURL(/\/lobby$/);
    const code = (await amy.locator('.code').innerText()).replace(/[^A-Z0-9]/gi, '');
    for (let i = 1; i < pages.length; i++) {
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await pages[i].getByLabel('lobby code').fill(code);
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await expect(pages[i]).toHaveURL(/\/lobby$/);
    }
    await expect(amy.locator('.roster .seat')).toHaveCount(3);

    // everyone else is on my floor (and I'm not on it twice)
    await expect(amy.locator('.floormate')).toHaveCount(2);
    await expect(amy.locator('.floormate[data-name="FloorBen"]')).toBeVisible();
    await expect(amy.locator('.floormate[data-name="FloorAmy"]')).toHaveCount(0);

    // Amy's mascot crashes out -> Ben's flinches and looks over
    // (keep poking until it snaps: a poke only goes in when no button is under it)
    for (let i = 0; i < 80; i++) {
      if (await amy.locator('.wanderer-body').evaluate((el) => /crash-/.test(el.className))) break;
      await poke(amy);
      await amy.waitForTimeout(150);
    }
    await expect(amy.locator('.wanderer-body')).toHaveClass(/crash-ragequit/);
    await expect(ben.locator('.wanderer-body')).toHaveClass(/act-flinch/);
    await expect(ben.locator('.home-robot .wanderer-mutter')).toHaveText('did you see that?');
    await expect(amy.locator('.wanderer-body')).not.toHaveClass(/crash-/);

    // Ben pokes Amy's character on his floor -> on Amy's phone her mascot stomps at him,
    // and on Ben's phone her floor-mate does the same. (Retry: it wanders, and a tap that
    // lands on Ben's own mascot is his, not a poke.)
    const amyOnBen = ben.locator('.floormate[data-name="FloorAmy"]');
    for (let i = 0; i < 60; i++) {
      if (await tapSprite(ben, '.floormate[data-name="FloorAmy"]')) {
        await ben.waitForTimeout(400);
        if (await amyOnBen.evaluate((el) => el.classList.contains('stomping'))) break;
      }
      await ben.waitForTimeout(300);
    }
    await expect(amyOnBen).toHaveClass(/stomping/);
    await expect(amyOnBen.locator('.floormate-mutter')).toHaveText('FloorBen!!');
    await expect(amy.locator('.wanderer-body')).toHaveClass(/act-stomp/);
    await expect(amy.locator('.home-robot .wanderer-mutter')).toContainText('FloorBen');

    // ...and none of it comes into the game
    await amy.getByRole('button', { name: 'start game' }).click();
    for (const page of pages) {
      await expect(page).toHaveURL(/\/game$/);
      await expect(page.locator('.lobby-floor')).toHaveCount(0);
      await expect(page.locator('.floormate')).toHaveCount(0);
      await expect(page.locator('.home-robot')).toHaveCount(0);
    }
  } finally {
    for (const ctx of contexts) await ctx.close();
  }
});
