import { test as base, expect } from '@playwright/test';
import { Backend } from './backend';
import { BoardPage } from '../pages/BoardPage';
import { EditorPage } from '../pages/EditorPage';

type Fixtures = {
  /** Control of the fake PC. Reset to the seeded library before every test. */
  backend: Backend;
  board: BoardPage;
  editor: EditorPage;
};

export const test = base.extend<Fixtures>({
  backend: async ({}, use) => {
    const backend = new Backend();
    await backend.reset();
    await use(backend);
  },
  board: async ({ page, backend: _ }, use) => {
    // Every page error fails the test: a broken script would otherwise just look "empty".
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(e.message));
    await use(new BoardPage(page));
    expect(errors, 'uncaught errors in the page').toEqual([]);
  },
  editor: async ({ page }, use) => { await use(new EditorPage(page)); },
});

export { expect };
