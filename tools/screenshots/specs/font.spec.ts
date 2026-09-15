import { expect, test } from '@playwright/test';
import { findTofu, japaneseSamples } from '../lib/tofu';

/**
 * **写しを撮る前に、日本語が描けることを確かめる。**
 *
 * Linux のコンテナには日本語フォントが無いのが普通で、入れ忘れれば
 * 文字がすべて豆腐（□）になる。**撮った画像を目で見るまで気付かない**ので、
 * ここで落として先へ進ませない。
 */
test.describe('日本語が描けること', () => {
  test('回答画面で豆腐にならない', async ({ page }) => {
    await page.goto('/');

    const report = await findTofu(page, japaneseSamples);

    expect(
      report.tofu,
      `日本語フォントが無い。豆腐になった文字: ${report.tofu.join('')} / `
        + `使われた書体: ${report.fontFamily}`,
    ).toEqual([]);
  });

  test('管理画面で豆腐にならない', async ({ page }) => {
    await page.goto('/admin');

    const report = await findTofu(page, japaneseSamples);

    expect(
      report.tofu,
      `日本語フォントが無い。豆腐になった文字: ${report.tofu.join('')} / `
        + `使われた書体: ${report.fontFamily}`,
    ).toEqual([]);
  });

  /**
   * **検出器そのものが働くことを確かめる。**
   *
   * 「豆腐が出ていない」という結果は、**検出器が壊れていても同じように出る。**
   * 字形を持たない文字を渡して、ちゃんと拾えることを見ておく。
   */
  test('検出器は字形の無い文字を拾う', async ({ page }) => {
    await page.goto('/');

    // U+E000 は私用領域。**どの書体にも字形が無い**
    const report = await findTofu(page, ['']);

    expect(
      report.tofu,
      '字形の無い文字を拾えていない。**検出器が働いていない**',
    ).toEqual(['']);
  });
});
