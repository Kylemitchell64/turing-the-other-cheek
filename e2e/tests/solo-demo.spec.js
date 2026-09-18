import { test, expect } from '@playwright/test';

// Phase 29: one visitor, no friends. The solo demo seats three bot stand-ins + the AI,
// runs five rounds on the compressed E2E clocks, and lands on the recap screen. Along the
// way: the lobby shows a QR invite, the game shows "/ 5" rounds, and the end screen has the
// recap sections + share button. Screenshots land in test-results/ for eyeballing.

async function guestLogin(page, name) {
  await page.goto('/login');
  await page.getByPlaceholder('pick a name').fill(name);
  await page.getByRole('button', { name: 'PLAY' }).click();
  await page.getByRole('button', { name: 'CONTINUE AS GUEST' }).click();
  const skip = page.getByRole('button', { name: 'skip for now' });
  const createLobby = page.getByRole('button', { name: 'create lobby' });
  await Promise.race([skip.waitFor({ state: 'visible' }), createLobby.waitFor({ state: 'visible' })]);
  if (await skip.isVisible()) await skip.click();
  await expect(createLobby).toBeVisible();
}

test('lobby shows a QR invite and the join link prefills the code', async ({ page, browser }) => {
  await guestLogin(page, 'qr_host');
  await page.getByRole('button', { name: 'create lobby' }).click();
  const qr = page.locator('img.qr');
  await expect(qr).toBeVisible();
  await expect(page.getByRole('button', { name: 'share invite link' })).toBeVisible();
  await page.screenshot({ path: 'test-results/solo-lobby-qr.png', fullPage: true });

  // The code on screen is what the QR encodes: open the join link in a fresh context.
  const code = (await page.locator('.code').innerText()).replace(/\s+/g, '');
  expect(code).toHaveLength(5);
  const ctx = await browser.newContext();
  const p2 = await ctx.newPage();
  await p2.goto(`/login?join=${code}`);
  await expect(p2.getByPlaceholder('lobby code (optional)')).toHaveValue(code);
  await ctx.close();
});

test('solo demo plays five rounds against bots and ends on the recap', async ({ page }) => {
  test.setTimeout(240_000);
  await guestLogin(page, 'solo_visitor');

  await page.getByRole('button', { name: /solo demo/ }).click();
  await expect(page).toHaveURL(/\/game$/);
  await expect(page.getByText(/ROUND 1 \/ 5/)).toBeVisible();

  // answer round 1 like a person, then just watch the bots + AI carry the rest
  const input = page.getByPlaceholder(/type something human/);
  await input.fill('my worst purchase was this game');
  await page.getByRole('button', { name: 'submit', exact: true }).click();

  const ended = page.getByText(/THE AI SURVIVES|DETECTOR WINS/);
  await expect(ended).toBeVisible({ timeout: 200_000 });

  await expect(page.getByText('how the AI played it')).toBeVisible();
  await expect(page.locator('.recap-rounds .recap-row')).toHaveCount(5);
  await expect(page.getByText(/solo demo — nothing saved/).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "share result" })).toBeVisible();
  // bots + the AI answer every round; only the idle human blanks r2-r5
  await expect(page.locator(".line", { hasText: "(no answer)" })).toHaveCount(4);
  await page.screenshot({ path: 'test-results/solo-recap.png', fullPage: true });
});
