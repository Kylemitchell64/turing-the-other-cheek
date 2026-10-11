# 0012 — installable PWA instead of app-store builds (amends 0004)

## context

Players asked for "the app". The store route costs money every year (Apple $99/yr, Google
$25 once), a Mac or a cloud macOS runner for iOS builds, Google's 14-day/12-tester closed
test for new personal accounts, and review rules that ask for Sign in with Apple, in-app
account deletion, and report/block before a chat-style game ships. ADR 0001 keeps this
project on free tiers. Everything the game does (SignalR, typing, audio) already runs in a
mobile browser.

## decision

**Ship as a Progressive Web App from the same client, same deploy, same API.** No second
repo and no wrapper: `vite-plugin-pwa` adds a manifest, a Workbox service worker, and icons
at build time. A second "mobile" codebase would duplicate every screen and split every fix.

**Caching is deliberately narrow.**

- Precached: hashed JS/CSS and the icons. Not `og.png` (link previews only) and not the
  lazy `AdminPage` chunk (players never download it — same reason it is split out).
- The HTML shell is **not** precached. Navigations go network-first with a 4s timeout and
  fall back to the last cached copy, so a deploy shows up on the next launch rather than
  hiding behind a stale shell, and the app still opens on a dead connection.
- `/api/*` and `/hubs/*` are never touched. They share the origin in the Docker image, and
  OAuth login is a top-level navigation to `/api/auth/*`. On Vercel the API is cross-origin
  and Workbox ignores it anyway. SignalR's WebSocket never passes through a service worker.
- `registerType: autoUpdate` without the `virtual:pwa-register` hook: a new worker takes
  over on its own, but nothing force-reloads a page mid-game.

**Token storage depends on display mode (amends ADR 0004).** In a browser tab the JWT stays
in `sessionStorage`, unchanged. In the installed app it goes to `localStorage`: iOS kills a
backgrounded home-screen app outright, which wipes `sessionStorage`, so every relaunch would
land on the login screen. 0004's argument against `localStorage` was the shared phone or
borrowed laptop; an installed app is on the player's own phone. Expired tokens are still
rejected on load, and logout clears both stores.

**Installing.** Android/Chrome fires `beforeinstallprompt`; we hold it and show an
`[ install ]` button. iOS Safari has no install API, so the same banner says
Share → Add to Home Screen. Hidden inside the installed app, and for good once dismissed.

**Edge to edge.** `viewport-fit=cover` plus `env(safe-area-inset-*)` padding on `.screen`
and the music widget keeps the UI clear of the notch and home bar.

## consequences

- $0, nothing new to host, and every web fix ships to the installed app at the same time.
- Not in the App Store or Play Store, so no store discovery. If that ever matters, a
  Capacitor wrapper can reuse this same build; the store-only work (Sign in with Apple,
  account deletion, report/block, Gemini consent) is listed in the discussion that led here.
- iOS limits: no install prompt (users have to find the Share sheet), and web push only
  works once the app is installed (iOS 16.4+).
- OAuth inside an installed iOS app is the one path that can't be verified headlessly:
  the provider opens out of the app's scope. Guest play and the solo demo don't touch it.
  If the token ends up in Safari instead of the app, the fix is a popup-style callback.
- CI asserts the build emitted `sw.js` and `manifest.webmanifest` and that `index.html` is
  not precached.
