import { test, expect } from '@playwright/test';

// Phase 29: a dropped socket mid-game. We take the browser offline long enough for the
// SignalR connection to die, bring it back, and expect the client to reconnect + Rejoin:
// a banner while it's down, the round still on screen afterwards, and the solo game
// still ticking (a later round eventually shows up).

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

test('a dropped connection mid-game reconnects and resyncs the round', async ({ page, context }) => {
  test.setTimeout(120_000);
  await guestLogin(page, 'flaky_phone');
  await page.getByRole('button', { name: /solo demo/ }).click();
  await expect(page.getByText(/ROUND 1 \/ 5/)).toBeVisible();

  // Kill the network. Long-polling/websocket both notice within the server's keepalive.
  await context.setOffline(true);
  await expect(page.locator('.maint-banner.reconnect')).toBeVisible({ timeout: 45_000 });

  await context.setOffline(false);
  await expect(page.locator('.maint-banner.reconnect')).toBeHidden({ timeout: 45_000 });

  // Still in the game with a live round header, and the game keeps advancing.
  await expect(page.getByText(/ROUND \d \/ 5/)).toBeVisible();
  await expect(page.getByText(/ROUND [2-5] \/ 5|THE AI SURVIVES|DETECTOR WINS/)).toBeVisible({ timeout: 60_000 });
});
