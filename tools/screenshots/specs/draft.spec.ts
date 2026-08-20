import { expect, test } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareDraftSurvey } from '../lib/draft';

/**
 * 回答の下書きが**端末の中だけ**に残ることを確かめる（Issue #59）。
 *
 * **確かめたいのは 4 つ。**
 *
 * 1. **サーバへ送らない。** 下書きの保存で通信が起きないこと
 * 2. **勝手に戻さない。** 共有の端末では、前の人の回答をそのまま見せることになる
 * 3. **許可したアンケートでだけ残る。** 既定は無効
 * 4. **送れたら消える。** 端末へ残し続けない
 *
 * **ここでしか確かめられない。** 下書きの実装は端末側にしかなく、
 * サーバ側の試験では通り道すら無い。
 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

let authFile = '';

/** 下書きを許したアンケート。 */
let allowedPublicId = '';

/** 許していないアンケート。**既定の側。** */
let deniedPublicId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser, baseURL }) => {
  authFile = await ensureAdminStorageState(browser, baseURL ?? '');
});

test.describe('回答の下書き', () => {
  test('見本のアンケートを 2 本公開する', async ({ browser, baseURL }) => {
    const context = await browser.newContext({ baseURL, storageState: authFile });

    try {
      allowedPublicId = (await prepareDraftSurvey(context.request, demoSiteId, true)).publicId;
      deniedPublicId = (await prepareDraftSurvey(context.request, demoSiteId, false)).publicId;
    } finally {
      await context.close();
    }

    expect(allowedPublicId).not.toBe('');
    expect(deniedPublicId).not.toBe('');
  });

  test('書いた内容は端末に残り、サーバへは送らない', async ({ page }) => {
    test.skip(allowedPublicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${allowedPublicId}`);

    // **書いた内容がサーバへ出ないこと。** 出たら「端末の中だけ」が崩れている。
    //
    // **「通信が 0 回」では見られない。** 送信チケットの取得（`/ticket`）が
    // 画面を開いた直後に走るので、数えると必ず引っ掛かる。
    // **見るのは中身**
    const sent: string[] = [];
    page.on('request', (request) => {
      const body = request.postData();
      if (body !== null) sent.push(body);
    });

    await page.getByLabel('ご意見').fill('途中まで書いた内容');
    await page.getByLabel('お名前').fill('山田');

    // **端末に入っていること**を直接見る
    const stored = await page.evaluate(
      (publicId) => localStorage.getItem(`questionnaire.draft.${publicId}`),
      allowedPublicId,
    );

    expect(stored).not.toBeNull();
    expect(stored).toContain('途中まで書いた内容');
    expect(sent.filter((body) => body.includes('途中まで書いた内容'))).toEqual([]);
    expect(sent.filter((body) => body.includes('山田'))).toEqual([]);
  });

  test('開き直しても勝手には戻さない', async ({ page }) => {
    test.skip(allowedPublicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${allowedPublicId}`);
    await page.getByLabel('ご意見').fill('前の人が書いた内容');

    await page.reload();

    // **共有の端末では、前に使った人の回答をそのまま見せることになる**
    await expect(page.getByText('前回の続きがこの端末に残っています。')).toBeVisible();
    await expect(page.getByLabel('ご意見')).toHaveValue('');

    // 押して初めて戻る
    await page.getByRole('button', { name: '続きから再開する' }).click();
    await expect(page.getByLabel('ご意見')).toHaveValue('前の人が書いた内容');
  });

  test('破棄すると端末から消える', async ({ page }) => {
    test.skip(allowedPublicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${allowedPublicId}`);
    await page.getByLabel('ご意見').fill('消したい内容');
    await page.reload();

    await page.getByRole('button', { name: '破棄する' }).click();

    const stored = await page.evaluate(
      (publicId) => localStorage.getItem(`questionnaire.draft.${publicId}`),
      allowedPublicId,
    );

    expect(stored).toBeNull();
    await expect(page.getByLabel('ご意見')).toHaveValue('');
  });

  test('許していないアンケートでは端末に残さない', async ({ page }) => {
    test.skip(deniedPublicId === '', '先の試験でアンケートを公開できていない');

    // **既定は無効。** 端末は共有され得る
    await page.goto(`/f/${deniedPublicId}`);
    await page.getByLabel('ご意見').fill('残ってはいけない内容');

    const stored = await page.evaluate(
      (publicId) => localStorage.getItem(`questionnaire.draft.${publicId}`),
      deniedPublicId,
    );

    expect(stored).toBeNull();

    await page.reload();
    await expect(page.getByText('前回の続きがこの端末に残っています。')).toBeHidden();
  });

  test('送れたら端末から消える', async ({ page }) => {
    test.skip(allowedPublicId === '', '先の試験でアンケートを公開できていない');

    await page.goto(`/f/${allowedPublicId}`);
    await page.getByLabel('ご意見').fill('送る内容');

    // 最短時間の関門を越えるまで待つ
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();

    await expect(page.getByRole('heading', { name: 'ありがとうございました。' })).toBeVisible();

    const stored = await page.evaluate(
      (publicId) => localStorage.getItem(`questionnaire.draft.${publicId}`),
      allowedPublicId,
    );

    // **送れたら要らない。** 端末へ残し続けない
    expect(stored).toBeNull();
  });

  test('期限を過ぎた下書きは戻さない', async ({ page }) => {
    test.skip(allowedPublicId === '', '先の試験でアンケートを公開できていない');

    // **古い回答を端末へ置き続けない。** 8 日前の下書きを自分で置いて確かめる
    await page.goto(`/f/${allowedPublicId}`);

    await page.evaluate((publicId) => {
      const savedAt = new Date(Date.now() - 8 * 24 * 60 * 60 * 1000).toISOString();
      localStorage.setItem(
        `questionnaire.draft.${publicId}`,
        JSON.stringify({ savedAt, answers: { 'q-free': { values: ['古い内容'], otherText: '' } } }),
      );
    }, allowedPublicId);

    await page.reload();

    await expect(page.getByText('前回の続きがこの端末に残っています。')).toBeHidden();

    // **読めない値を端末へ残し続けない。** 読んだ時点で消える
    const stored = await page.evaluate(
      (publicId) => localStorage.getItem(`questionnaire.draft.${publicId}`),
      allowedPublicId,
    );

    expect(stored).toBeNull();
  });
});
