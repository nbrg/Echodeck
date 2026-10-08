import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

test.describe('Problems are explained, not hidden', () => {
  test('a problem banner shows the worst problem first; tapping shows the rest', async ({ board, backend }) => {
    await backend.setProblems([
      { severity: 'warning', message: 'Your microphone is muted in Echodeck.' },
      { severity: 'error', message: "Discord isn't running on your PC." },
    ]);
    await board.openPaired();
    const banners = board.problems.locator('.banner');
    await expect(banners).toHaveCount(1);
    await expect(banners.first()).toContainText("Discord isn't running");
    await expect(banners.first()).toHaveClass(/error/);
    await expect(board.statusDot).toHaveClass(/off/);
    await expect(board.problems).toContainText('+1 more');

    await board.problems.click();
    await expect(banners).toHaveCount(2);
  });

  test('warnings make the status dot amber; no problems make it green', async ({ board, backend }) => {
    await backend.setProblems([{ severity: 'warning', message: "Discord isn't in a voice channel." }]);
    await board.openPaired();
    await expect(board.statusDot).toHaveClass(/warn/);
    await backend.setProblems([]);
    await expect(board.statusDot).toHaveClass(/on/);
    await expect(board.problems.locator('.banner')).toHaveCount(0);
  });

  test('a refused save shows the reason in red and does not open the editor', async ({ board, editor, backend }) => {
    await backend.fail('save', 'Not saved: the replay buffer is paused.');
    await board.openPaired();
    await board.saveButton(5).click();
    await board.expectToast('Not saved: the replay buffer is paused.', 'error');
    await editor.expectClosed();
  });

  test('a clip that plays but nobody hears gives an amber warning', async ({ board, backend }) => {
    await backend.warn('play', "Played, but Discord isn't in a voice channel, so nobody heard it.");
    await board.openPaired();
    await board.playButton(seed.trustMe).click();
    await board.expectToast(/nobody heard it/, 'warning');
  });

  test('when the PC stops answering, the page says so and recovers by itself', async ({ board, page }) => {
    await board.openPaired();
    await page.route('**/api/**', route => route.abort('connectionrefused'));
    await expect(board.status).toHaveText(/Can't reach Echodeck/, { timeout: 8_000 });
    await expect(board.statusDot).toHaveClass(/off/);

    await board.playButton(seed.trustMe).click();
    await board.expectToast(/Can't reach Echodeck on your PC/, 'error');

    await page.unroute('**/api/**');
    await expect(board.status).toHaveText(/Connected/, { timeout: 8_000 });
  });

  test('a server error is shown with its message', async ({ board, page }) => {
    await board.openPaired();
    await page.route('**/api/play-last', route => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ ok: false, message: 'Disk full' }) }));
    await board.playLastButton.click();
    await board.expectToast('Echodeck error: Disk full', 'error');
  });

  test('a re-paired PC makes the old phone show "Not paired"', async ({ board, page }) => {
    await board.openPaired();
    await page.route('**/api/**', route => route.fulfill({ status: 401, contentType: 'application/json', body: '{"ok":false,"message":"Not paired"}' }));
    await expect(page.getByRole('heading', { name: 'Not paired' })).toBeVisible({ timeout: 8_000 });
  });
});
