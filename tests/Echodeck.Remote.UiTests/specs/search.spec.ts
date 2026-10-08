import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

test.describe('Search', () => {
  test.beforeEach(async ({ board }) => { await board.openPaired(); });

  test('finds clips by what was said', async ({ board }) => {
    await board.searchFor('stairs');
    expect(await board.tileNames()).toEqual([seed.definitelyB]);
  });

  test('is case-insensitive and ignores accents (ä = a)', async ({ board }) => {
    await board.searchFor('HYVAA');
    expect(await board.tileNames()).toEqual([seed.goodNight]);
  });

  test('matches names and categories', async ({ board }) => {
    await board.searchFor('bob');
    expect(await board.tileNames()).toEqual([seed.trustMe]);
    await board.searchFor('replay');
    expect(await board.tileNames()).toEqual([seed.latestReplay]);
  });

  test('searches every clip, whatever filter is selected', async ({ board }) => {
    await board.filter('Alice').click();
    await board.searchFor('trust');
    expect(await board.tileNames()).toEqual([seed.trustMe]);
  });

  test('says when nothing matches, and clearing restores the list', async ({ board }) => {
    await board.searchFor('zzz');
    await expect(board.empty).toHaveText('No clip matches “zzz”.');
    await board.searchFor('');
    await expect(board.tiles).toHaveCount(4);
  });

  test('a search result can be played straight away', async ({ board, backend }) => {
    await board.searchFor('kaikille');
    await board.playButton(seed.goodNight).click();
    await backend.expectCall('play');
  });
});
