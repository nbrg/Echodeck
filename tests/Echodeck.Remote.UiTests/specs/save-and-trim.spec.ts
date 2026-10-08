import { test, expect } from '../support/fixtures';
import { seed } from '../support/backend';

test.describe('Save and trim', () => {
  test.beforeEach(async ({ board }) => { await board.openPaired(); });

  test('💾 Save last 5 s saves on the PC and opens the new clip in the trim editor', async ({ board, editor, backend }) => {
    await board.saveButton(5).click();
    await backend.expectCall('save:5');
    await editor.expectOpen();
    await expect(editor.name).toHaveValue(/^Replay /);
    const range = await editor.selectedRange();
    expect(range).toMatchObject({ start: 0, end: 5, duration: 5, keep: 5 });
  });

  test('dragging the handles selects part of the clip', async ({ editor, board }) => {
    await board.trimButton(seed.latestReplay).click();
    await editor.expectOpen(seed.latestReplay);

    await editor.dragHandle('start', 0.2);
    await editor.dragHandle('end', 0.6);
    const range = await editor.selectedRange();
    expect(range.start).toBeCloseTo(1.0, 0); // 20 % of 5 s
    expect(range.end).toBeCloseTo(3.0, 0);   // 60 % of 5 s
  });

  test('handles can never cross or leave the clip', async ({ editor, board }) => {
    await board.trimButton(seed.latestReplay).click();
    await editor.expectOpen();
    await editor.dragHandle('start', 1.0); // all the way right
    let range = await editor.selectedRange();
    expect(range.keep).toBeGreaterThanOrEqual(0.05);
    expect(range.end).toBe(5);

    await editor.nudge('End', '+0.1').click(); // already at the end
    range = await editor.selectedRange();
    expect(range.end).toBe(5);
  });

  test('nudge buttons move a handle by 0.1 s', async ({ editor, board }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen(seed.trustMe);
    await editor.nudge('Start', '+0.1').click();
    await editor.nudge('Start', '+0.1').click();
    await editor.nudge('End', '-0.1').click();
    expect(await editor.selectedRange()).toMatchObject({ start: 0.2, end: 2.3 });
  });

  test('💾 Save trims the clip, renames it and returns to the board', async ({ editor, board, backend }) => {
    await board.trimButton(seed.latestReplay).click();
    await editor.expectOpen();
    await editor.nudge('Start', '+0.1').click();
    await editor.nudge('End', '-0.1').click();
    await editor.name.fill('Clutch');
    await editor.save.click();

    await backend.expectCall('trim:0.10-4.90 name=Clutch copy=False');
    await editor.expectClosed();
    await board.expectToast('Saved "Clutch" (4.8 s)');
    await expect(board.playButton('Clutch')).toBeVisible();
    expect((await backend.clip('Clutch')).duration).toBeCloseTo(4.8);
  });

  test('Save copy keeps the original', async ({ editor, board, backend }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    await editor.nudge('End', '-0.1').click();
    await editor.saveCopy.click();
    await backend.expectCall(/^trim:.* copy=True$/);
    await expect(board.playButton('Trust me')).toBeVisible();
    await expect(board.playButton('Trust me (trim)')).toBeVisible();
  });

  test('🗑 Delete asks first; cancelling keeps the clip', async ({ editor, board, backend, page }) => {
    await board.trimButton(seed.goodNight).click();
    await editor.expectOpen();
    page.once('dialog', d => { expect(d.message()).toContain('Hyvää yötä'); d.dismiss(); });
    await editor.delete.click();
    await editor.expectOpen();
    expect(await backend.calls()).not.toContain('delete');

    page.once('dialog', d => d.accept());
    await editor.delete.click();
    await backend.expectCall('delete');
    await editor.expectClosed();
    await expect(board.playButton(seed.goodNight)).toHaveCount(0);
  });

  test('← Back leaves without saving', async ({ editor, board, backend }) => {
    await board.trimButton(seed.trustMe).click();
    await editor.expectOpen();
    await editor.nudge('Start', '+0.1').click();
    await editor.back.click();
    await editor.expectClosed();
    expect((await backend.calls()).some(c => c.startsWith('trim'))).toBe(false);
  });
});
