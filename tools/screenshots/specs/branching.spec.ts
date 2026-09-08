import { expect, test } from '@playwright/test';
import { prepareBranchingSurvey } from '../lib/branching';
import { demoAdmin } from '../lib/setup';
import { totp } from '../lib/totp';

/**
 * 分岐が回答画面で本当に効くことを確かめる。
 *
 * **画面の中の判定はサーバ側の写し**（`lib/flow.ts` と `Core/Flow/SurveyFlow.cs`）。
 * **写しが食い違っていないことは、実際に通してみないと分からない。**
 * 画面が出した設問がサーバに落とされる／隠したはずの答えが保存される、
 * のどちらも起き得る。
 *
 * **まっさらな検証環境が前提**（管理者がまだ 1 人も居ない）。
 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

const authFile = 'artifacts/branching-auth.json';

let publicId = '';

test.describe.configure({ mode: 'serial' });

test.describe('分岐（下ごしらえ）', () => {
  test('管理者を作る', async ({ page }) => {
    await page.goto('/admin');

    await expect(page.getByRole('heading', { name: '最初の管理者を登録する' })).toBeVisible();

    await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
    await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
    await page.getByLabel('パスワード（確認）').fill(demoAdmin.password);
    await page.getByRole('button', { name: '登録する' }).click();

    await expect(page.getByRole('heading', { name: '2 要素認証を登録する' })).toBeVisible();
    const secret = (await page.locator('.secret code').innerText()).replace(/\s/g, '');

    await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
    await page.getByRole('button', { name: '登録する' }).click();

    await expect(page.getByRole('heading', { name: '復旧コードを控えてください' })).toBeVisible();
    await page.getByLabel('控えました').check();
    await page.getByRole('button', { name: '管理画面へ進む' }).click();

    await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();
    await page.context().storageState({ path: authFile });
  });
});

test.describe('分岐', () => {
  test.use({ storageState: authFile });

  test('見本のアンケートを公開する', async ({ page }) => {
    // **page.request を使う。** request フィクスチャは別の入れ物で cookie を持たない
    const survey = await prepareBranchingSurvey(page.request, demoSiteId);
    publicId = survey.publicId;

    expect(publicId).not.toBe('');
  });

  test('条件を満たすと設問が現れ、外すと消える', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '分岐の見本' })).toBeVisible();

    // **はじめは出ていない**
    await expect(page.getByText('サービス名')).toBeHidden();

    await page.getByRole('radio', { name: 'はい' }).check();
    await expect(page.getByText('サービス名')).toBeVisible();

    // **外すと消える**
    await page.getByRole('radio', { name: 'いいえ' }).check();
    await expect(page.getByText('サービス名')).toBeHidden();
  });

  test('選んだ選択肢でページを飛ばす', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    // 「いいえ」→ ページ2 を飛ばしてページ3 へ
    await page.getByRole('radio', { name: 'いいえ' }).check();
    await page.getByRole('button', { name: '次へ' }).click();

    await expect(page.getByRole('heading', { name: 'ご意見' })).toBeVisible();
    await expect(page.getByRole('heading', { name: '良かった点' })).toBeHidden();

    // **進みは経路の長さで測る。** 飛ばした先が最後になる
    await expect(page.getByText('2 / 2')).toBeVisible();
  });

  test('飛ばしたページの必須では止まらない', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    await page.getByRole('radio', { name: 'いいえ' }).check();
    await page.getByRole('button', { name: '次へ' }).click();

    // **q-reason（必須）は通っていないので求められない**
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(page.getByRole('heading', { name: 'ありがとうございました。' })).toBeVisible();
  });

  test('通った側では必須で止まる', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    await page.getByRole('radio', { name: 'はい' }).check();
    await page.getByRole('button', { name: '次へ' }).click();

    await expect(page.getByRole('heading', { name: '良かった点' })).toBeVisible();

    // **必須が空のままでは先へ進めない。**
    // 「いいえ」側では同じ設問が求められないことと対になっている
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByRole('heading', { name: '良かった点' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'ご意見' })).toBeHidden();

    // 埋めれば進める
    await page.getByRole('textbox').first().fill('対応が早いところ');
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByRole('heading', { name: 'ご意見' })).toBeVisible();
  });

  test('1 問 1 ページ表示では設問が 1 つずつ出る', async ({ page }) => {
    // **アンケート全体の表示モード**（`_documents/画面設計.md` 1 章）。
    // **分岐はページ単位のまま**で、その中を 1 問ずつ見せる
    const survey = await prepareBranchingSurvey(page.request, demoSiteId, 'OneQuestionPerPage');

    await page.goto(`/f/${survey.publicId}`);

    // 1 区切り目は q-use だけ
    await expect(page.getByText('利用していますか')).toBeVisible();
    await expect(page.getByText('サービス名')).toBeHidden();

    await page.getByRole('radio', { name: 'はい' }).check();

    // **条件を満たすと区切りが 1 つ増える。** まだ同じ区切りには出さない
    await expect(page.getByText('サービス名')).toBeHidden();

    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByText('サービス名')).toBeVisible();
    await expect(page.getByText('利用していますか')).toBeHidden();
  });

  test('プレビューでも分岐を辿れる', async ({ page }) => {
    // **プレビューは回答画面と同じ描き方でなければ意味がない**
    // （`_documents/画面設計.md` 2 章）。別に描くと
    // 「プレビューでは出たのに本番では出ない」が起きる
    const survey = await prepareBranchingSurvey(page.request, demoSiteId);

    await page.goto(`/admin/surveys/${survey.surveyId}`);
    await page.getByRole('button', { name: 'プレビュー' }).click();

    const sheet = page.getByRole('dialog', { name: 'プレビュー' });
    await expect(sheet).toBeVisible();

    // **保存されないことが画面に出ている**
    await expect(sheet.getByText('保存・送信はされません')).toBeVisible();

    // 条件つきの設問が出し分けられる
    await expect(sheet.getByText('サービス名')).toBeHidden();
    await sheet.getByRole('radio', { name: 'はい' }).check();
    await expect(sheet.getByText('サービス名')).toBeVisible();

    // 「いいえ」でページを飛ばす
    await sheet.getByRole('radio', { name: 'いいえ' }).check();
    await sheet.getByRole('button', { name: '次へ' }).click();

    await expect(sheet.getByRole('heading', { name: 'ご意見' })).toBeVisible();
    await expect(sheet.getByRole('heading', { name: '良かった点' })).toBeHidden();

    // **送信の釦は出さない。** 押せる釦があると、押した人は送れたと思う
    await expect(sheet.getByRole('button', { name: '送信する' })).toBeHidden();
    await expect(sheet.getByText('ここが最後のページです')).toBeVisible();

    // **下敷きは巻き取らない。** どちらを操作しているのか分からなくなる
    await expect(page.locator('body')).toHaveCSS('overflow', 'hidden');

    // **Esc で閉じられる**
    await page.keyboard.press('Escape');
    await expect(sheet).toBeHidden();
    await expect(page.locator('body')).not.toHaveCSS('overflow', 'hidden');

    // 釦でも閉じられる
    await page.getByRole('button', { name: 'プレビュー' }).click();
    await expect(sheet).toBeVisible();
    await sheet.getByRole('button', { name: '編集へ戻る' }).click();
    await expect(sheet).toBeHidden();
  });

  test('プレビューでは回答が保存されない', async ({ page }) => {
    const survey = await prepareBranchingSurvey(page.request, demoSiteId);

    const before = await page.request.get('/api/admin/outbox/status');
    const pendingBefore = ((await before.json()) as { pendingCount: number }).pendingCount;

    await page.goto(`/admin/surveys/${survey.surveyId}`);
    await page.getByRole('button', { name: 'プレビュー' }).click();

    const sheet = page.getByRole('dialog', { name: 'プレビュー' });
    await sheet.getByRole('radio', { name: 'いいえ' }).check();
    await sheet.getByRole('button', { name: '次へ' }).click();
    await sheet.getByRole('textbox').first().fill('これは保存されてはいけない');

    // **送信待ちが増えていないこと。** 増えていたら、プレビューが本物を作っている
    const after = await page.request.get('/api/admin/outbox/status');
    const pendingAfter = ((await after.json()) as { pendingCount: number }).pendingCount;

    expect(pendingAfter).toBe(pendingBefore);
  });

  test('前へ戻って答えを変えると経路も変わる', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${publicId}`);

    await page.getByRole('radio', { name: 'はい' }).check();
    await page.getByRole('button', { name: '次へ' }).click();
    await expect(page.getByRole('heading', { name: '良かった点' })).toBeVisible();

    await page.getByRole('button', { name: '戻る' }).click();
    await page.getByRole('radio', { name: 'いいえ' }).check();
    await page.getByRole('button', { name: '次へ' }).click();

    // **行き先が変わる。** 前の答えのままページ2 へ行かない
    await expect(page.getByRole('heading', { name: 'ご意見' })).toBeVisible();
  });
});
