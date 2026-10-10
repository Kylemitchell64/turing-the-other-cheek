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

// The current round's answer cards (the answers list doubles as the accuse selection).
async function answerCards(page, roundNum) {
  const cards = page
    .locator('.round-panel', { has: page.locator('.chat-r', { hasText: `round ${roundNum}` }) })
    .locator('.answer-card');
  await expect.poll(async () => cards.count(), { timeout: 40_000 }).toBeGreaterThanOrEqual(5);
  return cards;
}

// The reveal must be anonymous: five cards, and the only seat label on any of them is the
// viewer's own "(you)". The podium row still names every seat, so the AI's fake name is
// read from there — the one roster name that isn't a guest's.
async function revealIsAnonymous(page, roundNum) {
  const cards = await answerCards(page, roundNum);
  expect(await cards.count(), `round ${roundNum} reveal should show five answers`).toBe(5);
  expect(await cards.locator('.answer-card-head').allInnerTexts()).toEqual(['(you)']);

  const names = (await page.locator('.podium-row .podium-name').allInnerTexts())
    .map((s) => s.replace(/\s*\(you\)\s*$/, '').trim());
  expect(new Set(names).size, 'the podium row should name all five seats').toBe(5);
  const ai = names.find((n) => !GUESTS.includes(n));
  expect(ai, 'exactly one podium name should not belong to a guest — that is the AI').toBeTruthy();
  return ai;
}

// Cards carry no names, but every guest types "r<round> from <name> …", so a card is found
// by its text. guest = null finds the AI's card: the one no guest wrote.
async function answerCardBy(page, roundNum, guest) {
  const cards = await answerCards(page, roundNum);
  const texts = await cards.locator('.answer-card-text').allInnerTexts();
  const idx = texts.findIndex((t) => {
    const author = t.match(new RegExp(`^r${roundNum} from (\\w+)`))?.[1];
    return guest ? author === guest : !GUESTS.includes(author);
  });
  expect(idx, `no round ${roundNum} card for ${guest ?? 'the AI'}`).toBeGreaterThanOrEqual(0);
  return cards.nth(idx);
}

async function waitForAccusePhase(page) {
  await expect(page.locator('.accuse-hold').first()).toBeVisible({ timeout: 40_000 });
}

// Long-press-to-accuse: the AccuseButton confirms after an ~800ms hold, so press with the
// real mouse and hold ~1.1s (no fixed sleeps anywhere else — this one is the gesture).
async function longPressAccuse(page, card) {
  const btn = card.locator('.accuse-hold');
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
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await pages[i].getByLabel('lobby code').fill(code);
      await pages[i].getByRole('button', { name: 'join', exact: true }).click();
      await expect(pages[i]).toHaveURL(/\/lobby$/);
    }

    // 4) Host sees four seats, then starts the game.
    await expect(host.locator('.roster .seat')).toHaveCount(4);
    await host.getByRole('button', { name: 'start game' }).click();
    for (const page of pages) await expect(page).toHaveURL(/\/game$/);

    // 5) Rounds 1 & 2: everyone answers; the reveal shows five unlabeled answers, and the
    //    podium row names the AI's seat.
    await answerCurrentRound(pages, 1);
    const aiName = await revealIsAnonymous(host, 1);

    await answerCurrentRound(pages, 2);
    const aiNameAgain = await revealIsAnonymous(host, 2);
    expect(aiNameAgain).toBe(aiName); // the AI keeps its identity across rounds

    // 6) Round 3: a wrong accusation — a guest accuses another guest (a known human).
    //    Nobody vetoes, so it resolves wrong and burns the accuser's token (3 -> 2).
    await answerCurrentRound(pages, 3);
    const accuser = pages[1]; // Bravo
    await waitForAccusePhase(accuser);
    await longPressAccuse(accuser, await answerCardBy(accuser, 3, GUESTS[2])); // Cyrus's card => wrong
    // The accusation registered (banner shows during the veto window)...
    await expect(accuser.getByText('you accused an answer', { exact: false })).toBeVisible({ timeout: 30_000 });
    // ...and once it resolves unvetoed, the accuser's token pips drop 3 -> 2 (persistent).
    await expect
      .poll(async () => accuser.locator('.topbar .tokens .pip.on').count(), { timeout: 30_000 })
      .toBe(2);

    // 7) Round 4: the correct accusation on the AI (by a guest who never owned its name)
    //    -> unvetoed correct accusation -> Detector win end screen with the AI revealed.
    await answerCurrentRound(pages, 4);
    const catcher = pages[0]; // Alice, still holding all three tokens
    await waitForAccusePhase(catcher);
    await longPressAccuse(catcher, await answerCardBy(catcher, 4, null)); // the card no guest wrote

    await expect(host.getByText('[ DETECTOR WINS ]')).toBeVisible({ timeout: 40_000 });
    await expect(host.locator('.ai-name')).toHaveText(aiName);
  } finally {
    for (const ctx of contexts) await ctx.close();
  }
});

// Music is on by default (phase 27): browsers forbid audio before a gesture, so the closest
// legal thing to autoplay is to start on the user's FIRST interaction ANYWHERE — not only
// inside the widget. Audio can't be "heard" in CI, so we assert the engine's
// window.__chiptuneStarted flag: still false before any gesture (autoplay policy respected),
// true after a generic first click, and the widget can still switch moods afterward.
test('music autostarts on the first click anywhere, widget still switches moods', async ({ page }) => {
  const pageErrors = [];
  page.on('pageerror', (e) => pageErrors.push(String(e)));

  await page.goto('/login');

  // Nothing should sound before any interaction at all.
  expect(await page.evaluate(() => window.__chiptuneStarted === true)).toBe(false);

  // A generic first click (top-left corner, NOT the bottom-right music widget) is the gesture
  // that unlocks + starts the default mood.
  await page.mouse.click(3, 3);
  await expect
    .poll(() => page.evaluate(() => window.__chiptuneStarted === true), { timeout: 10_000 })
    .toBe(true);

  // The widget still works: open it and switch to a specific mood without throwing.
  await page.getByRole('button', { name: 'open music controls' }).click();
  await page.getByRole('button', { name: 'ARCADE', exact: true }).click();
  expect(await page.evaluate(() => window.__chiptuneStarted === true)).toBe(true);

  // The widget wiring must never throw (covers the AudioContext-guard paths too).
  expect(pageErrors, `unexpected page errors: ${pageErrors.join('\n')}`).toEqual([]);
});
