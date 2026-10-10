// Playwright config for Claude Code cloud sessions (driven from a phone). The container
// ships a preinstalled Chromium that doesn't match this repo's pinned Playwright build, and
// `playwright install` can't download one through the session proxy, so every project is
// pointed at the preinstalled binary. CI and local machines keep using playwright.config.js.
//
// Fresh session setup (dot.net downloads are blocked; Ubuntu's package works):
//   apt-get install -y dotnet-sdk-8.0
//   (cd game-client && npm ci) && (cd e2e && npm ci)
//   cd e2e && npx playwright test -c playwright.cloud.config.mjs
import { existsSync } from 'node:fs';
import base from './playwright.config.js';

const CHROMIUM = process.env.PW_CHROMIUM_PATH || '/opt/pw-browsers/chromium';
const launchOptions = existsSync(CHROMIUM) ? { executablePath: CHROMIUM } : {};

export default {
  ...base,
  use: { ...base.use, launchOptions },
  projects: base.projects.map((p) => ({ ...p, use: { ...p.use, launchOptions } })),
};
