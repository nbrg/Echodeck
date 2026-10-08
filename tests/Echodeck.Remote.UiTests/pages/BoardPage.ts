import { expect, type Locator, type Page } from '@playwright/test';
import { token } from '../support/env';

/** The phone's main screen: status, save buttons, search, filter chips and clip tiles. */
export class BoardPage {
  readonly status: Locator;
  readonly statusDot: Locator;
  readonly problems: Locator;
  readonly toast: Locator;
  readonly search: Locator;
  readonly filters: Locator;
  readonly tiles: Locator;
  readonly empty: Locator;
  readonly muteButton: Locator;
  readonly stopButton: Locator;
  readonly playLastButton: Locator;

  constructor(readonly page: Page) {
    this.status = page.locator('#status');
    this.statusDot = page.locator('#dot');
    this.problems = page.locator('#problems');
    this.toast = page.getByRole('status');
    this.search = page.getByRole('searchbox', { name: /search clips/i });
    this.filters = page.getByRole('group', { name: 'Show' });
    this.tiles = page.locator('#main .tile');
    this.empty = page.locator('#main .empty');
    this.muteButton = page.locator('#mute');
    this.stopButton = page.getByRole('button', { name: '■ Stop' });
    this.playLastButton = page.getByRole('button', { name: 'Play the newest clip into Discord' });
  }

  /** Opens the page the way the QR code does: the pairing token is in the URL fragment. */
  async openPaired() {
    await this.page.goto(`/#t=${token}`);
    await expect(this.tiles.first()).toBeVisible();
  }

  async openWithoutToken() { await this.page.goto('/'); }

  saveButton(seconds: number) { return this.page.getByRole('button', { name: `💾 Save last ${seconds} s` }); }
  filter(name: string | RegExp) { return this.filters.getByRole('button', { name, exact: typeof name === 'string' }); }
  playButton(clipName: string) { return this.page.getByRole('button', { name: `Play ${clipName} into Discord`, exact: true }); }
  trimButton(clipName: string) { return this.page.getByRole('button', { name: `Trim or edit ${clipName}`, exact: true }); }

  /** Clip names in the order the tiles are shown (★ prefix removed). */
  async tileNames(): Promise<string[]> {
    const names = await this.tiles.locator('.name').allInnerTexts();
    return names.map(n => n.replace(/^★\s*/, ''));
  }

  async expectToast(text: string | RegExp, kind: 'ok' | 'warning' | 'error' = 'ok') {
    await expect(this.toast).toHaveText(text);
    if (kind === 'ok') await expect(this.toast).not.toHaveClass(/warning|error/);
    else await expect(this.toast).toHaveClass(new RegExp(kind));
  }

  async searchFor(text: string) { await this.search.fill(text); }
}
