import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

test.describe('Soundboard', () => {
  test.beforeEach(async ({ board }) => { await board.openPaired(); });

  test('shows recent clips first, newest on top', async ({ board }) => {
    await expect(board.filter('🕒 Recent')).toHaveAttribute('aria-pressed', 'true');
    expect(await board.tileNames()).toEqual([seed.latestReplay, seed.goodNight, seed.trustMe, seed.definitelyB]);
  });

  test('tapping a tile plays the clip into Discord', async ({ board, backend }) => {
    await board.playButton(seed.trustMe).click();
    await board.expectToast('Playing "Trust me" into Discord.');
    await backend.expectCall('play');
    await expect(board.status).toHaveText('Playing into Discord…');
  });

  test('▶ Last plays the newest clip', async ({ board, backend }) => {
    await board.playLastButton.click();
    await board.expectToast(`Playing "${seed.latestReplay}" into Discord.`);
    await backend.expectCall('play-last');
  });

  test('Stop stops playback', async ({ board, backend }) => {
    await board.playButton(seed.goodNight).click();
    await expect(board.status).toHaveText('Playing into Discord…');
    await board.stopButton.click();
    await backend.expectCall('stop');
    await expect(board.status).not.toHaveText('Playing into Discord…');
  });

  test('mute toggles and shows its state', async ({ board, backend }) => {
    await expect(board.muteButton).toHaveText('🎤 Mic on');
    await board.muteButton.click();
    await expect(board.muteButton).toHaveText('🔇 Muted');
    await expect(board.muteButton).toHaveAttribute('aria-pressed', 'true');
    await board.muteButton.click();
    await expect(board.muteButton).toHaveText('🎤 Mic on');
    expect((await backend.calls()).filter(c => c === 'mute')).toHaveLength(2);
  });

  test('has a filter per category, including empty ones, plus No category', async ({ board }) => {
    const names = await board.filters.getByRole('button').allInnerTexts();
    expect(names).toEqual(['🕒 Recent', '★ Favourites', 'All', ...seed.categories, 'No category']);
  });

  test('filters by favourites, category and no category', async ({ board }) => {
    await board.filter('★ Favourites').click();
    expect(await board.tileNames()).toEqual([seed.definitelyB]);

    await board.filter('Bob').click();
    expect(await board.tileNames()).toEqual([seed.trustMe]);
    await expect(board.filter('Bob')).toHaveAttribute('aria-pressed', 'true');

    await board.filter('No category').click();
    expect(await board.tileNames()).toEqual([seed.latestReplay]);

    await board.filter('All').click();
    await expect(board.tiles).toHaveCount(4);
  });

  test('an empty category explains how to fill it', async ({ board }) => {
    await board.filter('Alice').click();
    await expect(board.empty).toContainText('No clips in “Alice” yet');
    await expect(board.empty).toContainText('Who said it?');
  });

  test('the chosen filter is remembered after reopening', async ({ board, page }) => {
    await board.filter('Matti').click();
    await page.reload();
    await expect(board.filter('Matti')).toHaveAttribute('aria-pressed', 'true');
    expect(await board.tileNames()).toEqual([seed.goodNight]);
  });

  test('tiles show what was said in the clip', async ({ board }) => {
    const tile = board.tiles.filter({ has: board.playButton(seed.trustMe) });
    await expect(tile.locator('.said')).toHaveText('“Trust me bro, it\'s fine”');
    await expect(tile.locator('.meta')).toHaveText('2.4 s · Bob');
  });

  test('new clips saved on the PC appear without reloading', async ({ board, backend }) => {
    await backend.setTranscript(seed.latestReplay, 'Did you see that?');
    await expect(board.page.getByText('“Did you see that?”')).toBeVisible();
  });
});
