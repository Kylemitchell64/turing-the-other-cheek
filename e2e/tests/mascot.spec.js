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

// The sprite wanders, so aim at its live bounding box every time.
async function poke(page) {
  const box = await page.locator('.home-robot-walker').boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
}

test('poking the mascot annoys it, then it crashes out and recovers', async ({ page }) => {
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

  const box = await walker.boundingBox();
  const sx = box.x + box.width / 2;
  const sy = box.y + box.height / 2;
  await page.mouse.move(sx, sy);
  await page.mouse.down();
  await page.mouse.move(sx + 60, sy - 80, { steps: 8 });
  await page.mouse.move(sx + 160, sy - 120, { steps: 8 });
  await expect(body).toHaveClass(/held/);
  await page.mouse.up();

  // falls, lands, gets annoyed about it
  await expect(body).not.toHaveClass(/held/);
  await expect(body).toHaveClass(/mood-annoyed/);
  const after = await walker.boundingBox();
  expect(after.x).toBeGreaterThan(box.x + 60);
});
