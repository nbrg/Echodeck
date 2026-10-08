import { expect, type Locator, type Page } from '@playwright/test';

/** The phone's trim editor: waveform with two handles, listen buttons, save/delete, ★ and categories. */
export class EditorPage {
  readonly heading: Locator;
  readonly back: Locator;
  readonly name: Locator;
  readonly favorite: Locator;
  readonly transcript: Locator;
  readonly whoSaidIt: Locator;
  readonly waveform: Locator;
  readonly selection: Locator;
  readonly listenPhone: Locator;
  readonly listenPc: Locator;
  readonly listenDiscord: Locator;
  readonly save: Locator;
  readonly saveCopy: Locator;
  readonly delete: Locator;

  constructor(readonly page: Page) {
    this.heading = page.locator('#heading');
    this.back = page.getByRole('button', { name: '← Back' });
    this.name = page.getByRole('textbox', { name: 'Clip name' });
    this.favorite = page.getByRole('button', { name: 'Favourite' });
    this.transcript = page.locator('#saidFull');
    this.whoSaidIt = page.getByRole('group', { name: 'Who said it?' });
    this.waveform = page.getByRole('img', { name: /waveform/i });
    this.selection = page.locator('#seltext');
    this.listenPhone = page.locator('#pvPhone');
    this.listenPc = page.locator('#pvPc');
    this.listenDiscord = page.locator('#pvDiscord');
    this.save = page.getByRole('button', { name: '💾 Save' });
    this.saveCopy = page.getByRole('button', { name: 'Save copy' });
    this.delete = page.getByRole('button', { name: '🗑 Delete' });
  }

  async expectOpen(clipName?: string) {
    await expect(this.heading).toHaveText('Trim');
    await expect(this.selection).toHaveText(/^Keep /); // waveform loaded
    if (clipName !== undefined) await expect(this.name).toHaveValue(clipName);
  }

  async expectClosed() { await expect(this.heading).toHaveText('Echodeck'); }

  category(name: string) { return this.whoSaidIt.getByRole('button', { name, exact: true }); }
  newCategory() { return this.whoSaidIt.getByRole('button', { name: '＋ New' }); }

  nudge(handle: 'Start' | 'End', delta: '-0.1' | '+0.1') {
    const label = delta === '-0.1' ? '−0.1 s' : '+0.1 s';
    return this.page.locator(`[data-n="${handle === 'Start' ? 's' : 'e'},${delta === '-0.1' ? '-0.1' : '0.1'}"]`).filter({ hasText: label });
  }

  /** Selected range as shown under the waveform, e.g. { keep: 1.2, start: 0.5, end: 1.7 }. */
  async selectedRange() {
    const text = await this.selection.innerText();
    const m = text.match(/Keep ([\d.]+) s\s+\(([\d.]+) – ([\d.]+) of ([\d.]+) s\)/);
    expect(m, `selection text "${text}"`).toBeTruthy();
    return { keep: Number(m![1]), start: Number(m![2]), end: Number(m![3]), duration: Number(m![4]) };
  }

  /**
   * Drags a handle to a fraction (0–1) of the waveform's width, like a finger would: press on
   * the handle, move in steps, release. Uses mouse events, which the page receives as pointer events.
   */
  async dragHandle(handle: 'start' | 'end', toFraction: number) {
    const box = (await this.waveform.boundingBox())!;
    const range = await this.selectedRange();
    const fromFraction = (handle === 'start' ? range.start : range.end) / range.duration;
    const y = box.y + box.height / 2;
    await this.page.mouse.move(box.x + box.width * fromFraction + (handle === 'start' ? 1 : -1), y);
    await this.page.mouse.down();
    await this.page.mouse.move(box.x + box.width * toFraction, y, { steps: 8 });
    await this.page.mouse.up();
  }
}
