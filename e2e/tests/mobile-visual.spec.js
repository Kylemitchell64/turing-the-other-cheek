import { test, expect } from '@playwright/test';
import { auditScreen, trackErrors, reportFindings } from './visual-audit.js';

// Phase 24: the phone sweep. Walks every screen a player actually sees, on a small
// phone viewport (set by the "mobile" project in playwright.config.js), and runs the
// visual-audit scan on each: sideways overflow, stuff hanging off the edge, chopped
// text, overlapping buttons, tap targets you can't hit. One test collects everything
// and fails at the end with the full grouped report, so a single run surfaces every
// broken screen instead of dying on the first one.

const GUESTS = ['Mona', 'Pixel', 'Rugby'];

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

test('every screen survives the phone-viewport visual sweep', async ({ browser }) => {
  test.setTimeout(300_000);
  const findings = [];

  const contexts = [];
  const pages = [];
  for (let i = 0; i < GUESTS.length; i++) {
    const ctx = await browser.newContext();
    contexts.push(ctx);
    const page = await ctx.newPage();
    trackErrors(page, `p${i}-${GUESTS[i]}`, findings);
    pages.push(page);
  }
  const host = pages[0];

  try {
    // --- login screen, before and after typing -------------------------------
    await host.goto('/login');
    await auditScreen(host, '01-login', findings);

    for (let i = 0; i < pages.length; i++) await guestLogin(pages[i], GUESTS[i]);

    // --- home ----------------------------------------------------------------
    await auditScreen(host, '02-home', findings);

    // music widget open (it overlays the corner — worth checking it fits).
    await host.getByRole('button', { name: 'open music controls' }).click();
    await auditScreen(host, '03-home-music-open', findings);
    await host.getByRole('button', { name: 'collapse' }).click();

    // Everything below navigates by tapping the actual UI, the way a thumb would.
    // (Deliberate: page.goto() is a full reload, and the JWT lives in memory, so a
    // reload dumps you back at login — see AuthContext. Taps keep the session.)

    // --- character creator ----------------------------------------------------
    await host.getByRole('button', { name: 'edit character' }).click();
    await auditScreen(host, '04-character-creator', findings);
    await host.getByRole('button', { name: 'cancel' }).click();

    // --- stats -----------------------------------------------------------------
    await host.getByRole('button', { name: 'my stats' }).click();
    await auditScreen(host, '05-stats', findings);
    await host.getByRole('button', { name: /back/ }).click();

    // --- writing samples -------------------------------------------------------
    await host.getByRole('button', { name: 'writing samples' }).click();
    await auditScreen(host, '06-samples', findings);
    await host.getByRole('button', { name: /back/ }).click();

    // --- lobby: host creates, others join --------------------------------------
    await host.getByRole('button', { name: 'create lobby' }).click();
    await expect(host).toHaveURL(/\/lobby$/);
    await expect(host.locator('.code')).toBeVisible();
    const code = (await host.locator('.code').innerText()).replace(/[^A-Z0-9]/gi, '');

    for (let i = 1; i < pages.length; i++) {
      await pages[i].getByRole('button', { name: 'join by code' }).click();
      await pages[i].getByPlaceholder('join code').fill(code);
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await expect(pages[i]).toHaveURL(/\/lobby$/);
    }
    await expect(host.locator('.roster .seat')).toHaveCount(GUESTS.length);
    await auditScreen(host, '07-lobby-host', findings);
    await auditScreen(pages[1], '08-lobby-guest', findings);

    // --- in-game screens: prompting, locked, reveal, accuse --------------------
    await host.getByRole('button', { name: 'start game' }).click();
    for (const page of pages) await expect(page).toHaveURL(/\/game$/);

    const input = host.getByPlaceholder(/type something human/);
    await input.waitFor({ state: 'visible', timeout: 40_000 });
    await auditScreen(host, '09-game-prompting', findings);

    // Everyone answers; audit the locked-in state on the host.
    await Promise.all(
      pages.map(async (page, i) => {
        const box = page.getByPlaceholder(/type something human/);
        await box.waitFor({ state: 'visible', timeout: 40_000 });
        await box.fill(`sweep answer from ${GUESTS[i]}`);
        await page.getByRole('button', { name: 'submit', exact: true }).click();
        await expect(page.getByText(/answer locked in/)).toBeVisible();
      })
    );
    await auditScreen(host, '10-game-locked', findings);

    // Reveal: wait for the round's authors to land in the scrollback.
    await expect
      .poll(async () => host.locator('.chat-author').count(), { timeout: 40_000 })
      .toBeGreaterThanOrEqual(GUESTS.length + 1);
    await auditScreen(host, '11-game-reveal', findings);

    // Accuse phase (hold buttons + roster) is its own layout.
    await expect(host.locator('.accuse-hold').first()).toBeVisible({ timeout: 40_000 });
    await auditScreen(host, '12-game-accuse', findings);
  } finally {
    // Write the report even when the walk dies mid-way — partial findings are
    // exactly what you want when a screen is broken enough to block navigation.
    reportFindings(findings);
    for (const ctx of contexts) await ctx.close();
  }

  const report = reportFindings(findings);
  expect(findings, report).toEqual([]);
});
