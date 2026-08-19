import { expect, test } from '@playwright/test';

/**
 * 英語の画面が壊れていないかを見る。
 *
 * **英語は日本語より横に長くなる。** 「送信する」は 4 文字だが `Submit` は 6 文字、
 * 「認証アプリに出た 6 桁の数字」に至っては倍近くになる。
 * **画面からはみ出したり、押せない釦ができたりしていないか**を機械で確かめる。
 *
 * 目で見るより先にここで落とす。**英語を使う人が居ないうちは、誰も気付かない。**
 *
 * **言語はブラウザの設定で決まる**（管理画面。`?lang=` は回答画面の指定）。
 * 走らせるときは `SHOT_LOCALE=en-US` を渡すこと。
 */

/** 英語で走っているか。**日本語のまま走らせても素通りしてしまう。** */
const runningInEnglish = (process.env['SHOT_LOCALE'] ?? '').toLowerCase().startsWith('en');

/** 見に行く画面。**回答画面は公開が要るので、ここでは管理画面だけ見る。** */
const screens = [
  { name: 'admin-setup', path: '/admin' },
];

test.describe.configure({ mode: 'serial' });

test.describe('英語の画面', () => {
  // **日本語で走らせたときに「通った」と誤解させない**
  test.skip(!runningInEnglish, 'SHOT_LOCALE=en-US を渡したときだけ見る');

  for (const screen of screens) {
    test(`${screen.name}：横にはみ出さない`, async ({ page }) => {
      await page.goto(screen.path);
      await page.waitForLoadState('networkidle');

      const overflow = await page.evaluate(() => {
        const results: { text: string; overflowBy: number }[] = [];
        const viewportWidth = document.documentElement.clientWidth;

        for (const element of document.querySelectorAll('button, label, h1, h2, p, a, li')) {
          const rect = element.getBoundingClientRect();
          if (rect.width === 0) {
            continue;
          }

          // **画面の右端を越えているものを拾う**
          if (rect.right > viewportWidth + 1) {
            results.push({
              text: (element.textContent ?? '').trim().slice(0, 40),
              overflowBy: Math.round(rect.right - viewportWidth),
            });
          }
        }

        return results;
      });

      expect(
        overflow,
        `画面の右端からはみ出している: ${overflow.map((o) => `「${o.text}」(+${o.overflowBy}px)`).join(', ')}`,
      ).toEqual([]);
    });

    test(`${screen.name}：文字が入れ物からあふれない`, async ({ page }) => {
      await page.goto(screen.path);
      await page.waitForLoadState('networkidle');

      const clipped = await page.evaluate(() => {
        const results: { text: string; by: number }[] = [];

        for (const element of document.querySelectorAll('button, label')) {
          const style = getComputedStyle(element);

          // **隠す指定がある所だけを見る。** 折り返す所は伸びるだけで問題ない
          if (style.overflow === 'visible' && style.textOverflow !== 'ellipsis') {
            continue;
          }

          if (element.scrollWidth > element.clientWidth + 1) {
            results.push({
              text: (element.textContent ?? '').trim().slice(0, 40),
              by: element.scrollWidth - element.clientWidth,
            });
          }
        }

        return results;
      });

      expect(
        clipped,
        `文字が切れている: ${clipped.map((c) => `「${c.text}」(+${c.by}px)`).join(', ')}`,
      ).toEqual([]);
    });
  }

  test('英語を選ぶと日本語が残らない', async ({ page }) => {
    await page.goto('/admin');
    await page.waitForLoadState('networkidle');

    // **訳し忘れがあると日本語のまま出る。** 画面に日本語が 1 文字でも残っていたら拾う
    const japanese = await page.evaluate(() => {
      const found: string[] = [];
      const pattern = /[぀-ゟ゠-ヿ一-鿿]/;

      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      let node = walker.nextNode();

      while (node !== null) {
        const text = (node.textContent ?? '').trim();
        if (text !== '' && pattern.test(text)) {
          found.push(text.slice(0, 40));
        }

        node = walker.nextNode();
      }

      return found;
    });

    expect(japanese, `英語の画面に日本語が残っている: ${japanese.join(' / ')}`).toEqual([]);

    // **英語の写しも残す。** 後で英語の取説を作るときに要る
    await page.screenshot({ path: 'shots/en-admin-01-setup.png', fullPage: true });
  });
});
