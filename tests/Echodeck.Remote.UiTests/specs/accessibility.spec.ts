import AxeBuilder from '@axe-core/playwright';
import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

/** Automated WCAG 2.1 AA checks (axe-core) on each screen in its typical states. */
test.describe('Accessibility', () => {
  const scan = (page: import('@playwright/test').Page) =>
    new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();

  const summarise = (violations: Awaited<ReturnType<typeof scan>>['violations']) =>
    violations.map(v => `${v.id} (${v.impact}): ${v.help} — ${v.nodes.map(n => n.target.join(' ')).slice(0, 3).join(', ')}`);

  test('board has no WCAG A/AA violations', async ({ board, backend, page }) => {
    await backend.setProblems([{ severity: 'error', message: "Discord isn't running on your PC." }]);
    await board.openPaired();
    await expect(board.problems.locator('.banner')).toHaveCount(1);
    expect(summarise((await scan(page)).violations)).toEqual([]);
  });

  test('trim editor has no WCAG A/AA violations', async ({ board, editor, page }) => {
    await board.openPaired();
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    expect(summarise((await scan(page)).violations)).toEqual([]);
  });

  test('toasts are announced to screen readers', async ({ board }) => {
    await board.openPaired();
    await expect(board.toast).toHaveAttribute('aria-live', 'polite');
  });

  test('every clip can be played and trimmed with a named button', async ({ board }) => {
    await board.openPaired();
    for (const name of [seed.definitelyB, seed.trustMe, seed.goodNight, seed.latestReplay]) {
      await expect(board.playButton(name)).toBeVisible();
      await expect(board.trimButton(name)).toBeVisible();
    }
  });
});
