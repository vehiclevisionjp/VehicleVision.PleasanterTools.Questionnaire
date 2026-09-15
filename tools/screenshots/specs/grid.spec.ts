import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareGridSurvey } from '../lib/grid';

/**
 * グリッドとランキングが回答画面で本当に使えることを確かめる（Issue #74）。
 *
 * **確かめたいのは 3 つ。**
 *
 * 1. 行ごとの回答が送信まで届くこと。**グリッドは `values` を通らない**ので、
 *    経路のどこかで落ちても、画面上は普通に動いて見える
 * 2. 必須のグリッドが**行の全部**を求めること
 * 3. **ランキングをキーボードだけで並べ替えられる**こと。
 *    掴んで動かすだけだと、その人はこの設問に答えられない
 *
 * **まっさらな検証環境が前提**（管理者がまだ 1 人も居ない）。
 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

let publicId = '';

let authFile = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  // **先に走った試験が管理者を作っていれば、その控えを使う。**
  // まっさらな検証環境で単独に走らせたときは自分で作る
  authFile = await ensureAdminStorageState(browser, baseURL ?? '');
});

test.describe('グリッドとランキング', () => {
  test('見本のアンケートを公開する', async ({ browser, baseURL }) => {
    // **管理の口は cookie が要る。** `test.use` では beforeAll より先に評価されるので、
    // 控えの場所が決まってから入れ物を作る
    const context = await browser.newContext({ baseURL, storageState: authFile });

    try {
      const survey = await prepareGridSurvey(context.request, demoSiteId);
      publicId = survey.publicId;
    } finally {
      await context.close();
    }

    expect(publicId).not.toBe('');
  });

  test('行と列の交点に名前が付いている', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: 'グリッドの見本' })).toBeVisible();

    // **表を線形に読むと「どの行のどの列か」が失われる。**
    // 入力そのものに名前が付いていないと、読み上げでは答えられない
    await expect(page.getByRole('radio', { name: '価格: 良い' })).toBeVisible();
    await expect(page.getByRole('radio', { name: '品質: 悪い' })).toBeVisible();
  });

  test('必須のグリッドは行が全部埋まるまで送れない', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    // 1 行だけ答えて送ろうとする
    await page.getByRole('radio', { name: '価格: 良い' }).check();
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(page.getByText('すべての行に回答してください')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'ありがとうございました。' })).toBeHidden();

    // 残りを埋めれば送れる
    await page.getByRole('radio', { name: '品質: 悪い' }).check();
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(page.getByRole('heading', { name: 'ありがとうございました。' })).toBeVisible();
  });

  test('ランキングをキーボードだけで並べ替えられる', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    // **押した順に順位が付く**
    await page.getByRole('button', { name: /速さ/ }).click();
    await page.getByRole('button', { name: /安さ/ }).click();

    await expect(page.getByRole('button', { name: /1 位.*速さ/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /2 位.*安さ/ })).toBeVisible();

    // **掴まずに動かせること。** ここが無いと、キーボードだけの人は順位を変えられない
    await page.getByRole('button', { name: '安さ: 順位を上げる' }).click();

    await expect(page.getByRole('button', { name: /1 位.*安さ/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /2 位.*速さ/ })).toBeVisible();

    // **一番上では上げられない。** 押せる釦が何もしないのが一番たちが悪い
    await expect(page.getByRole('button', { name: '安さ: 順位を上げる' })).toBeDisabled();
  });

  test('行ごとの回答が送信まで届く', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    // **グリッドは values を通らない。**
    // 経路のどこかで落ちても、画面上は普通に動いて見える
    await page.goto(`/f/${publicId}`);

    // 送信の口は `PUT /api/forms/{publicId}/responses/{responseToken}`
    const submitted = page.waitForRequest(
      (request) => request.url().includes('/responses/') && request.method() === 'PUT',
    );

    await page.getByRole('radio', { name: '価格: 良い' }).check();
    await page.getByRole('radio', { name: '品質: 悪い' }).check();
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();

    const body = (await submitted).postDataJSON() as {
      answers: { questionId: string; rows?: Record<string, string[]> }[];
    };

    const grid = body.answers.find((answer) => answer.questionId === 'q-grid');

    expect(grid?.rows).toEqual({ price: ['good'], quality: ['bad'] });
    await expect(page.getByRole('heading', { name: 'ありがとうございました。' })).toBeVisible();
  });
});
