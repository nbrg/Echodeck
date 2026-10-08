import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

/**
 * The three "listen" buttons in the editor. Exactly one can be active: it turns into "■ Stop",
 * and goes back by itself when playback ends (the PC reports that through /api/state).
 */
test.describe('Listening to a selection', () => {
  test.beforeEach(async ({ board, editor, page }) => {
    // Observe the page's audio element (it isn't in the DOM): record play/pause and the start time.
    await page.addInitScript(() => {
      const media = { playing: false, startedAt: -1 };
      (window as any).__media = media;
      const play = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function () {
        media.startedAt = this.currentTime;
        return play.call(this).then(() => { media.playing = true; });
      };
      const pause = HTMLMediaElement.prototype.pause;
      HTMLMediaElement.prototype.pause = function () { media.playing = false; return pause.call(this); };
    });
    await board.openPaired();
    await board.trimButton(seed.definitelyB).click(); // 1.8 s long
    await editor.expectOpen();
  });

  test('🎧 PC headset plays the selection on the PC and reverts when it ends', async ({ editor, backend }) => {
    await editor.nudge('Start', '+0.1').click();
    await editor.listenPc.click();
    await backend.expectCall('preview-range:0.10-1.80');
    await expect(editor.listenPc).toHaveText('■ Stop');
    await expect(editor.listenDiscord).toHaveText('📢 Into Discord');
    await expect(editor.listenPc).toHaveText('🎧 PC headset', { timeout: 6_000 }); // after ~1.7 s
  });

  test('📢 Into Discord plays only the selection, and its Stop button stops it', async ({ editor, backend }) => {
    await editor.nudge('End', '-0.1').click();
    await editor.listenDiscord.click();
    await backend.expectCall('play-range:0.00-1.70');
    await expect(editor.listenDiscord).toHaveText('■ Stop');
    await editor.listenDiscord.click();
    await backend.expectCall('stop');
    await expect(editor.listenDiscord).toHaveText('📢 Into Discord');
  });

  test('🔈 This phone plays the selection locally, without asking the PC', async ({ editor, backend, page }) => {
    await editor.nudge('Start', '+0.1').click();
    await editor.listenPhone.click();
    await expect(editor.listenPhone).toHaveText('■ Stop');
    // The page's <audio> element really started, from the selection's start.
    await expect.poll(() => page.evaluate(() => (window as any).__media?.playing)).toBe(true);
    expect(await page.evaluate(() => (window as any).__media.startedAt)).toBeCloseTo(0.1, 1);
    const calls = await backend.calls();
    expect(calls.some(c => c.startsWith('preview-range') || c.startsWith('play-range'))).toBe(false);

    await editor.listenPhone.click();
    await expect(editor.listenPhone).toHaveText('🔈 This phone');
    expect(await page.evaluate(() => (window as any).__media.playing)).toBe(false);
  });

  test('starting one preview stops the other', async ({ editor, backend }) => {
    await editor.listenDiscord.click();
    await expect(editor.listenDiscord).toHaveText('■ Stop');
    await editor.listenPc.click();
    await expect(editor.listenPc).toHaveText('■ Stop');
    await expect(editor.listenDiscord).toHaveText('📢 Into Discord');
    await backend.expectCall(/^preview-range/);
  });
});
