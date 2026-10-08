# Phone UI tests (Playwright)

End-to-end tests for Echodeck's **phone/tablet remote**: the touch web page friends never see,
but which you use mid-game to play, save, trim and organise clips on your PC.

[![phone-ui-tests](https://github.com/nbrg/Echodeck/actions/workflows/phone-ui-tests.yml/badge.svg)](https://github.com/nbrg/Echodeck/actions/workflows/phone-ui-tests.yml)

## What runs

```
Playwright (TypeScript)                          TestHost (.NET 8)
┌──────────────────────────┐   HTTP + token    ┌───────────────────────────────────────┐
│ Pixel 7  · Chromium      │ ───────────────▶  │ RemoteServer  (the real one: Kestrel, │
│ iPhone 13 · WebKit       │   the real page   │   pairing-token auth, embedded page)  │
│ iPad     · WebKit        │ ◀───────────────  │        │                              │
│                          │                   │        ▼                              │
│ specs → page objects ────┼── control API ──▶ │ FakeBackend (in-memory "PC": clips,   │
│        → Backend client  │  reset / inject / │   categories, playback, failures)     │
└──────────────────────────┘  read calls       └───────────────────────────────────────┘
```

* **Nothing about the page is mocked.** The tests load the same HTML/JS the app ships, served by
  the same server with the same authentication. Only the Windows PC behind it (audio engine,
  Discord, files) is replaced by `TestHost/FakeBackend.cs`. That fake behaves like the real PC
  wherever the page can tell the difference: saved clips appear, trims change durations,
  playback lasts as long as the clip, Stop stops it.
* **Every test starts from the same state.** A fixture resets the fake PC to a seeded library
  before each test. Tests then set up their scenario through a separate control API (port 5898),
  for example "show these problems", "make Save fail with this message" or "add a transcript".
  Afterwards they assert on what the page shows *and* on what the PC was asked to do.
* **Three real browser engines**: Android Chrome (Chromium), and iPhone and iPad Safari (WebKit),
  with touch, viewport and device-scale emulation.
* **Any uncaught JavaScript error fails the test**, even when the screen looks fine.

**Status:** 147 of 147 passing on CI (49 tests × 3 devices, about 2.5 minutes). Locally, the suite
ran three times in a row (147 runs) with no flaky failures.

## Coverage (49 tests per device)

| Spec | What it proves |
|---|---|
| `pairing` | The page is locked without the QR pairing code. A wrong code is rejected. The code leaves the address bar and pairing survives reopening. |
| `board` | Recent order, playing a clip, ▶ Last, Stop, mute state, a filter for every category (including empty ones), "No category", the filter remembered across reloads, transcripts on tiles, live updates from the PC. |
| `search` | Finds clips by what was said, ignoring case and accents (`HYVAA` → "Hyvää yötä"), by name or category, across all filters. Shows an empty state. |
| `save-and-trim` | Save last 5 s opens the editor. Dragging the handles (pointer events) and nudging work, and the handles can't cross. Save sends the exact range, Save copy keeps the original. Delete asks first, and cancelling keeps the clip. Back discards changes. |
| `library` | "Who said it?" category chips: assign, unassign, create with ＋ New (prompt), cancel. Favourite toggle. All changes show up in the board filters. |
| `listen` | The three listen buttons: one active at a time, each with Stop, and they revert when playback ends. Phone preview really starts the `<audio>` element at the selection start without calling the PC. |
| `errors` | The problem banner shows the worst problem first and expands on tap, and the status dot changes colour. A refused save gives a red toast and doesn't open the editor. Warnings show an amber toast. The PC being unreachable is detected and recovered from (network faults injected with `page.route`). Server errors and un-pairing are handled. |
| `accessibility` | axe-core finds no WCAG 2.1 A/AA violations on the board or the editor. Every clip has named Play and Trim buttons, and toasts are announced to screen readers. |

## Bugs these tests found (and fixed)

* **Nested interactive controls:** each clip tile was a `<button>` containing a fake `✂` button.
  Screen readers couldn't reach the trim action. Tiles are now two real, labelled buttons.
* **Colour contrast:** the white text on the blue **Save** and red **Delete** buttons, and on the
  amber warning toast, was below WCAG AA's 4.5:1. The colours were darkened.
* **Problem ordering relied on the PC:** the page only showed the worst problem first if the PC
  had already sorted the list. The page now sorts it itself.
* **Pinch-zoom was disabled** (`user-scalable=no`, a WCAG failure). It's allowed again.
  Double-tap zoom stays off through `touch-action`.
* **Missing accessible names:** the search box, clip-name field, ★ toggle and waveform had none.
  Toggle buttons now expose `aria-pressed`.

## Running locally

Requires the .NET 8 SDK and Node 20+.

```bash
cd tests/Echodeck.Remote.UiTests
npm ci
npx playwright install --with-deps chromium webkit
npx playwright test                       # all devices
npm run test:chrome                       # Android Chrome only
npx playwright test --ui                  # interactive mode
npm run report                            # last HTML report (traces, videos, screenshots on failure)
```

Playwright starts the test host itself (`dotnet run --project TestHost`). To explore the page by
hand, run `dotnet run --project TestHost` and open
`http://127.0.0.1:5899/#t=0123456789abcdef0123456789abcdef`.

## Design notes

* **Page objects** (`pages/`) use role- and label-based locators (`getByRole('button', { name:
  'Trim or edit Trust me' })`). This keeps the specs readable as user actions and checks
  accessibility at the same time: if a control has no accessible name, the test can't find it.
* **Waiting:** assertions use Playwright's auto-retrying `expect` and `expect.poll`; there are no
  fixed sleeps. Playback that ends on the PC is observed through the page's normal state polling.
* **Isolation over speed:** one shared fake PC runs tests serially (about 35 s per device). On CI,
  traces, videos and screenshots are kept for failures and the HTML report is uploaded as an
  artifact.
* **Versions are pinned** (`@playwright/test` 1.56.1) so browser builds match across machines.
