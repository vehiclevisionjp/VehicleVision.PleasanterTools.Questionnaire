import { expect, test, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { choiceLabels, noteHeading, prepareShuffleSurvey, questionTitles } from '../lib/shuffle';

/**
 * **選択肢と設問の並べ替えが実機で効くことを確かめる**（Issue #103 / #126）。
 *
 * **確かめたいのは 6 つ。**
 *
 * 1. **選択肢の並びが回答者ごとに変わる**
 * 2. **「その他」は動かず末尾に残る**
 * 3. **1 回の回答の中では並びが変わらない**（戻っても位置が同じ）
 * 4. **設問の並びが回答者ごとに変わる**
 * 5. **説明文ブロックは動かない**
 * 6. **指定が無ければ元の並びのまま**
 *
 * **単体試験では 3 が確かめられない。** `applyShuffle` は「同じ種なら同じ並び」までしか
 * 見ておらず、**画面が種を引き直していないか**は実機を通さないと分からない。
 * 種は `$state` の初期化 1 回きりで作るので、**再描画のたびに引き直す書き方に
 * 変わっても単体試験は通ってしまう。**
 */
const baseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/**
 * 読み直す回数。
 *
 * ⚠️ **1 回では確かめられない。** 並べ替えた結果がたまたま元と同じになることはある。
 * 選択肢は 8 つ（40320 通り）あるので、**6 回読み直して全部同じなら、
 * 並べ替えが効いていないと言い切ってよい。**
 */
const attempts = 6;

/** 画面に出ている選択肢を、出ている順に読む。 */
async function choiceOrder(page: Page): Promise<string[]> {
  return page.locator('label.choice span').allInnerTexts();
}

/**
 * 画面に出ている設問と説明文を、出ている順に読む。
 *
 * **説明文ブロックも混ぜて読む。** 混ぜないと「動いていない」ことが見えない。
 */
async function questionOrder(page: Page): Promise<string[]> {
  const titles = await page.locator('fieldset.field > legend, section.note > h3').allInnerTexts();
  // 必須の `*` が混ざるので落とす（この見本に必須は無いが、書き換えに備える）
  return titles.map((title) => title.replace(/\s+/g, ' ').replace(/ \*$/, '').trim());
}

test.describe.configure({ mode: 'serial' });

test.describe('並べ替え', () => {
  /** 並べ替えを効かせた見本。 */
  let shuffledId = '';

  /** 対照。**並べ替えを指定していない見本。** */
  let plainId = '';

  test.beforeAll(async ({ browser }) => {
    // **初期設定から通すと 30 秒では足りない。** 管理者の登録・2 要素・復旧コードを
    // 順に踏むため、**まっさらな環境で単独に走らせたときだけ**時間がかかる
    test.setTimeout(120_000);

    const authFile = await ensureAdminStorageState(browser, baseUrl);
    const context = await browser.newContext({ baseURL: baseUrl, storageState: authFile });

    try {
      shuffledId = (
        await prepareShuffleSurvey(context.request, demoSiteId, true, '並べ替えの見本')
      ).publicId;

      plainId = (
        await prepareShuffleSurvey(context.request, demoSiteId, false, '並べ替えなしの見本')
      ).publicId;
    } finally {
      await context.close();
    }
  });

  test('選択肢の並びが回答者ごとに変わる', async ({ page }) => {
    test.skip(shuffledId === '', '下ごしらえでアンケートを公開できていない');

    const seen = new Set<string>();

    for (let attempt = 0; attempt < attempts; attempt += 1) {
      // **読み直すたびに新しい種を引く。** 回答者が変わったのと同じ状態
      await page.goto(`/f/${shuffledId}`);
      await expect(page.getByText('好きな色')).toBeVisible();

      const order = await choiceOrder(page);
      // **数は変わらない。** 並べ替えは足しも引きもしない
      expect(order).toHaveLength(choiceLabels.length + 1);
      expect([...order].sort()).toEqual([...choiceLabels, 'その他'].sort());

      seen.add(order.join(','));
    }

    expect(seen.size).toBeGreaterThan(1);
  });

  test('「その他」は動かず末尾に残る', async ({ page }) => {
    test.skip(shuffledId === '', '下ごしらえでアンケートを公開できていない');

    for (let attempt = 0; attempt < attempts; attempt += 1) {
      await page.goto(`/f/${shuffledId}`);
      await expect(page.getByText('好きな色')).toBeVisible();

      // **自由記述の欄が並びの真ん中に現れると読みにくい**（Issue #103）
      expect((await choiceOrder(page)).at(-1)).toBe('その他');
    }
  });

  test('1 回の回答の中では並びが変わらない', async ({ page }) => {
    test.skip(shuffledId === '', '下ごしらえでアンケートを公開できていない');

    await page.goto(`/f/${shuffledId}`);
    await expect(page.getByText('好きな色')).toBeVisible();

    const before = await choiceOrder(page);

    // **ページを行き来しても位置が動かないこと。**
    // 都度並べ替えると、戻っただけで選び直しを誘発する
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByText(noteHeading)).toBeVisible();
    await page.getByRole('button', { name: '戻る' }).click();
    await expect(page.getByText('好きな色')).toBeVisible();

    expect(await choiceOrder(page)).toEqual(before);
  });

  test('設問の並びが回答者ごとに変わり、説明文は動かない', async ({ page }) => {
    test.skip(shuffledId === '', '下ごしらえでアンケートを公開できていない');

    const seen = new Set<string>();

    for (let attempt = 0; attempt < attempts; attempt += 1) {
      await page.goto(`/f/${shuffledId}`);
      await page.getByRole('button', { name: '次へ' }).click();
      await expect(page.getByText(noteHeading)).toBeVisible();

      const order = await questionOrder(page);
      expect(order).toHaveLength(questionTitles.length + 1);

      // **説明文ブロックは元の位置（先頭）に留まる。**
      // 「以下の設問について」が説明する対象から離れてしまうため
      expect(order[0]).toBe(noteHeading);
      expect([...order.slice(1)].sort()).toEqual([...questionTitles].sort());

      seen.add(order.join(','));
    }

    expect(seen.size).toBeGreaterThan(1);
  });

  test('指定が無ければ選択肢も設問も元の並びのまま', async ({ page }) => {
    test.skip(plainId === '', '下ごしらえでアンケートを公開できていない');

    for (let attempt = 0; attempt < attempts; attempt += 1) {
      await page.goto(`/f/${plainId}`);
      await expect(page.getByText('好きな色')).toBeVisible();

      // **対照が動いてしまっては、上の試験が何を見たのか分からなくなる**
      expect(await choiceOrder(page)).toEqual([...choiceLabels, 'その他']);

      await page.getByRole('button', { name: '次へ' }).click();
      await expect(page.getByText(noteHeading)).toBeVisible();

      expect(await questionOrder(page)).toEqual([noteHeading, ...questionTitles]);
    }
  });
});
