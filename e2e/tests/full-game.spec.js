import { test, expect } from '@playwright/test';

// Four human guests. The AI takes a fifth roster seat under a fake human name drawn from
// the server's pool, guaranteed not to collide with any of these — so after the reveal,
// the one roster name that isn't in this list is the machine.
const GUESTS = ['Alice', 'Bravo', 'Cyrus', 'Delta'];

// --- helpers -----------------------------------------------------------------

// Guest login: name + PLAY, dismiss the first-time QUICK PLAY modal, then skip the
// character creator if it shows (brand-new device may or may not route through it).
async function guestLogin(page, name) {
  await page.goto('/login');
  await page.getByPlaceholder('pick a name').fill(name);
  await page.getByRole('button', { name: 'PLAY' }).click();

  // First play on a fresh browser context always shows the QUICK PLAY modal.
  await page.getByRole('button', { name: 'CONTINUE AS GUEST' }).click();

  // Then EITHER the character creator (skip it) OR straight to Home — handle both.
  const skip = page.getByRole('button', { name: 'skip for now' });
  const createLobby = page.getByRole('button', { name: 'create lobby' });
  await Promise.race([
    skip.waitFor({ state: 'visible' }),
    createLobby.waitFor({ state: 'visible' }),
  ]);
  if (await skip.isVisible()) await skip.click();

  await expect(createLobby).toBeVisible();
}

// Answer the current prompting round on every page, in parallel, and confirm each locks.
async function answerCurrentRound(pages, roundNum) {
  await Promise.all(
    pages.map(async (page, i) => {
      const input = page.getByPlaceholder(/type something human/);
      await input.waitFor({ state: 'visible', timeout: 40_000 });
      await input.fill(`r${roundNum} from ${GUESTS[i]} ${Math.random().toString(36).slice(2, 7)}`);
      await page.getByRole('button', { name: 'submit', exact: true }).click();
      await expect(page.getByText(/answer locked in/)).toBeVisible();
    })
  );
}

// Read the revealed answers for a round off the chat scrollback, assert all five roster
// names (four guests + the AI) are present, and return the AI's fake name.
async function revealNamesIncludeAi(page, roundNum) {
  const roundBlock = page.locator('.chat-round', {
    has: page.locator('.chat-r', { hasText: `round ${roundNum}` }),
  });
  await expect
    .poll(async () => roundBlock.locator('.chat-author').count(), { timeout: 40_000 })
    .toBeGreaterThanOrEqual(5);

  const names = (await roundBlock.locator('.chat-author').allInnerTexts()).map((s) => s.trim());
  const unique = [...new Set(names)];
  expect(unique, `round ${roundNum} reveal should show exactly five names`).toHaveLength(5);

  const ai = unique.find((n) => !GUESTS.includes(n));
  expect(ai, 'exactly one revealed name should not belong to a guest — that is the AI').toBeTruthy();
  return ai;
}

async function waitForAccusePhase(page) {
  await expect(page.locator('.accuse-hold').first()).toBeVisible({ timeout: 40_000 });
}

// Long-press-to-accuse: the AccuseButton confirms after an ~800ms hold, so press with the
// real mouse and hold ~1.1s (no fixed sleeps anywhere else — this one is the gesture).
async function longPressAccuse(page, name) {
  const btn = page.getByLabel(`hold to accuse ${name}`);
  await btn.waitFor({ state: 'visible', timeout: 20_000 });
  await btn.scrollIntoViewIfNeeded();
  await btn.hover(); // moves the real cursor over the button (and into view)
  await page.mouse.down();
  await page.waitForTimeout(1100); // deliberate ~1s hold; AccuseButton confirms at ~800ms
  await page.mouse.up();
}

// --- the game ----------------------------------------------------------------

test('four guests play a full game to a detector win', async ({ browser }) => {
  const contexts = [];
  const pages = [];
  for (let i = 0; i < GUESTS.length; i++) {
    const ctx = await browser.newContext();
    contexts.push(ctx);
    pages.push(await ctx.newPage());
  }

  try {
    // 1) Everyone logs in as a guest and lands on Home.
    for (let i = 0; i < pages.length; i++) {
      await guestLogin(pages[i], GUESTS[i]);
    }

    const host = pages[0];

    // 2) Host creates a lobby; read the join code off the screen.
    await host.getByRole('button', { name: 'create lobby' }).click();
    await expect(host).toHaveURL(/\/lobby$/);
    await expect(host.locator('.code')).toBeVisible();
    const code = (await host.locator('.code').innerText()).replace(/[^A-Z0-9]/gi, '');
    expect(code, 'join code should be five chars').toHaveLength(5);

    // 3) The other three join by code.
    for (let i = 1; i < pages.length; i++) {
      await pages[i].getByRole('button', { name: 'join by code' }).click();
      await pages[i].getByPlaceholder('join code').fill(code);
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await expect(pages[i]).toHaveURL(/\/lobby$/);
    }

    // 4) Host sees four seats, then starts the game.
    await expect(host.locator('.roster .seat')).toHaveCount(4);
    await host.getByRole('button', { name: 'start game' }).click();
    for (const page of pages) await expect(page).toHaveURL(/\/game$/);

    // 5) Rounds 1 & 2: everyone answers; the reveal shows all five names incl. the AI.
    await answerCurrentRound(pages, 1);
    const aiName = await revealNamesIncludeAi(host, 1);

    await answerCurrentRound(pages, 2);
    const aiNameAgain = await revealNamesIncludeAi(host, 2);
    expect(aiNameAgain).toBe(aiName); // the AI keeps its identity across rounds

    // 6) Round 3: a wrong accusation — a guest accuses another guest (a known human).
    //    Nobody vetoes, so it resolves wrong and burns the accuser's token (3 -> 2).
    await answerCurrentRound(pages, 3);
    const accuser = pages[1]; // Bravo
    await waitForAccusePhase(accuser);
    await longPressAccuse(accuser, GUESTS[2]); // accuse Cyrus, a human => wrong
    // The accusation registered (banner shows during the veto window)...
    await expect(accuser.getByText(`${GUESTS[1]} accused ${GUESTS[2]}`)).toBeVisible({ timeout: 30_000 });
    // ...and once it resolves unvetoed, the accuser's token pips drop 3 -> 2 (persistent).
    await expect
      .poll(async () => accuser.locator('.topbar .tokens .pip.on').count(), { timeout: 30_000 })
      .toBe(2);

    // 7) Round 4: the correct accusation on the AI (by a guest who never owned its name)
    //    -> unvetoed correct accusation -> Detector win end screen with the AI revealed.
    await answerCurrentRound(pages, 4);
    const catcher = pages[0]; // Alice, still holding all three tokens
    await waitForAccusePhase(catcher);
    await longPressAccuse(catcher, aiName);

    await expect(host.getByText('[ DETECTOR WINS ]')).toBeVisible({ timeout: 40_000 });
    await expect(host.locator('.ai-name')).toHaveText(aiName);
  } finally {
    for (const ctx of contexts) await ctx.close();
  }
});
