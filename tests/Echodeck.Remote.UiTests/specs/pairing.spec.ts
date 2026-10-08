import { test, expect } from '../support/fixtures';
import { token } from '../support/env';

test.describe('Pairing', () => {
  test('without the pairing code the page is locked and says how to pair', async ({ board, backend }) => {
    await board.openWithoutToken();
    await expect(board.page.getByRole('heading', { name: 'Not paired' })).toBeVisible();
    await expect(board.status).toHaveText('Not paired');
    await expect(board.tiles).toHaveCount(0);
    expect(await backend.calls()).toEqual([]);
  });

  test('a wrong pairing code is rejected', async ({ board }) => {
    await board.page.goto('/#t=ffffffffffffffffffffffffffffffff');
    await expect(board.page.getByRole('heading', { name: 'Not paired' })).toBeVisible();
  });

  test('the QR link pairs once and the code is removed from the address bar', async ({ board, page }) => {
    await board.openPaired();
    expect(page.url()).not.toContain(token);
    // Reopening without the fragment (e.g. from the home-screen icon) stays paired.
    await page.goto('/');
    await expect(board.tiles.first()).toBeVisible();
    await expect(board.status).toHaveText(/Connected/);
  });
});
