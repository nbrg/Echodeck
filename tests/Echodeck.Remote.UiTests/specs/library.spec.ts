import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

test.describe('Organising clips from the phone', () => {
  test.beforeEach(async ({ board }) => { await board.openPaired(); });

  test('"Who said it?" lists every category and marks the current one', async ({ board, editor }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    await expect(editor.whoSaidIt.getByRole('button')).toHaveText([...seed.categories, '＋ New']);
    await expect(editor.category('Bob')).toHaveAttribute('aria-pressed', 'true');
    await expect(editor.transcript).toHaveText('“Trust me bro, it\'s fine”');
  });

  test('tapping a friend moves the clip into their category', async ({ board, editor, backend }) => {
    await board.saveButton(5).click();
    await editor.expectOpen();
    await editor.category('Alice').click();
    await backend.expectCall('category:Alice');
    await expect(editor.category('Alice')).toHaveAttribute('aria-pressed', 'true');

    await editor.back.click();
    await board.filter('Alice').click();
    await expect(board.tiles).toHaveCount(1);
  });

  test('tapping the selected category again removes it', async ({ board, editor, backend }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    await editor.category('Bob').click();
    await backend.expectCall('category:<none>');
    await expect(editor.whoSaidIt.getByRole('button', { pressed: true })).toHaveCount(0);
    expect((await backend.clip(seed.trustMe)).category).toBeNull();
  });

  test('＋ New creates a category and puts the clip in it', async ({ board, editor, backend, page }) => {
    await board.trimButton(seed.latestReplay).click();
    await editor.expectOpen();
    page.once('dialog', d => d.accept('Charlie'));
    await editor.newCategory().click();

    await backend.expectCall('category:Charlie');
    await expect(editor.category('Charlie')).toHaveAttribute('aria-pressed', 'true');
    expect(await backend.categories()).toContain('Charlie');
    await editor.back.click();
    await expect(board.filter('Charlie')).toBeVisible();
  });

  test('cancelling ＋ New changes nothing', async ({ board, editor, backend, page }) => {
    await board.trimButton(seed.latestReplay).click();
    await editor.expectOpen();
    page.once('dialog', d => d.dismiss());
    await editor.newCategory().click();
    await expect(editor.whoSaidIt.getByRole('button')).toHaveCount(seed.categories.length + 1);
    expect((await backend.calls()).some(c => c.startsWith('category'))).toBe(false);
  });

  test('☆ toggles favourite, and the clip shows under ★ Favourites', async ({ board, editor, backend }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    await expect(editor.favorite).toHaveAttribute('aria-pressed', 'false');
    await editor.favorite.click();
    await backend.expectCall('favorite:true');
    await expect(editor.favorite).toHaveText('★');
    await expect(editor.favorite).toHaveAttribute('aria-pressed', 'true');

    await editor.back.click();
    await board.filter('★ Favourites').click();
    expect(await board.tileNames()).toEqual(expect.arrayContaining([seed.definitelyB, seed.trustMe]));
  });
});
