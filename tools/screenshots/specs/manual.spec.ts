import { expect, test } from '@playwright/test';
import { expectFewSurveys, expectNoAdminYet } from '../lib/fresh';
import { demoAdmin, prepareSurvey } from '../lib/setup';
import { findTofu, japaneseSamples } from '../lib/tofu';
import { totp } from '../lib/totp';

/**
 * 取説へ載せる画面の写しを撮る。
 *
 * **まっさらな検証環境が前提**（管理者がまだ 1 人も居ない）。
 * 初期設定と 2 要素の登録も取説に要る画面なので、実際の初回の流れをそのまま辿る。
 */
const shots = 'shots';

/**
 * ログイン済みの状態をしまう場所。
 *
 * **試験ごとに入れ物（context）は作り直される。** 前の試験で通したログインは
 * そのままでは引き継がれないので、cookie を書き出して次へ渡す。
 */
const authFile = 'artifacts/auth.json';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/**
 * 秘密の値を見本の文字へ置き換える。
 *
 * **取説に本物の値を載せない。** 使い捨ての検証環境で撮っているとはいえ、
 * 共有鍵や復旧コードが載った図は「載せてよいもの」という誤解を招く。
 */
async function mask(page: import('@playwright/test').Page): Promise<void> {
  await page.evaluate(() => {
    const secret = document.querySelector('.secret code');
    if (secret !== null) {
      secret.textContent = 'ABCD EFGH IJKL MNOP QRST UVWX YZ23 4567';
    }

    document.querySelectorAll('.codes li').forEach((item, index) => {
      item.textContent = `XXXXX-${String(index + 1).padStart(5, '0')}`;
    });
  });
}

/** 撮った写しに豆腐が無いことを、撮るたびに確かめる。 */
async function shoot(
  page: import('@playwright/test').Page,
  name: string,
  // **重ねて出す画面は画面の高さで撮る。** 画面いっぱいに固定して出すものを
  // ページ全体で撮ると、重なっていない部分まで写って図として読めなくなる
  fullPage = true,
): Promise<void> {
  await mask(page);

  const report = await findTofu(page, japaneseSamples);
  expect(
    report.tofu,
    `${name} を撮る前に日本語が描けていない。豆腐: ${report.tofu.join('')} / 書体: ${report.fontFamily}`,
  ).toEqual([]);

  await page.screenshot({ path: `${shots}/${name}.png`, fullPage });
}

test.describe.configure({ mode: 'serial' });


let publicId = '';
let surveyId = '';
let secretBase32 = '';

test.describe('取説用の写し', () => {

  test('管理画面：初期設定から 2 要素の登録まで', async ({ page }) => {
    // **撮り始める前に前提を確かめる**（Issue #65）。
    // 汚れた環境では、写しの途中で分からない形で落ちる
    await expectNoAdminYet(page);

    // **まだ誰も登録されていない状態の入口**
    await expect(page.getByRole('heading', { name: '最初の管理者を登録する' })).toBeVisible();
    await shoot(page, 'admin-01-setup');

    await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
    await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
    await page.getByLabel('パスワード（確認）').fill(demoAdmin.password);
    await page.getByRole('button', { name: '登録する' }).click();

    // **QR と手入力用の文字列が出る画面**
    await expect(page.getByRole('heading', { name: '2 要素認証を登録する' })).toBeVisible();
    await expect(page.locator('img.qr')).toBeVisible();

    // **伏せる前に読む。** 写しでは見本の文字へ置き換えてしまう
    const secret = (await page.locator('.secret code').innerText()).replace(/\s/g, '');
    secretBase32 = secret;

    await shoot(page, 'admin-02-enroll');

    await page.getByLabel('認証アプリに出た 6 桁の数字').fill(totp(secret));
    await page.getByRole('button', { name: '登録する' }).click();

    // **復旧コードはここでしか出せない**
    await expect(page.getByRole('heading', { name: '復旧コードを控えてください' })).toBeVisible();
    await shoot(page, 'admin-03-recovery-codes');

    await page.getByLabel('控えました').check();
    await page.getByRole('button', { name: '管理画面へ進む' }).click();

    await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();

    // **残骸が溜まっていないこと**（Issue #65）。
    // 一覧が長いと、ページ全体の写しが撮れなくなる
    await expectFewSurveys(page);

    await shoot(page, 'admin-04-survey-list-empty');

    // **次の試験へログインを渡す**
    await page.context().storageState({ path: authFile });
  });
});

test.describe('取説用の写し（ログイン済み）', () => {
  test.use({ storageState: authFile });

  test('管理画面：アンケートを作って公開する', async ({ page }) => {
    // **page.request を使う。** request フィクスチャは別の入れ物で、
    // 画面で通したログインの cookie を持っていない（401 になる）
    const survey = await prepareSurvey(page.request, demoSiteId);
    publicId = survey.publicId;
    surveyId = survey.surveyId;

    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: 'アンケート' })).toBeVisible();
    await shoot(page, 'admin-05-survey-list');

    await page.goto(`/admin/surveys/${survey.surveyId}`);
    await expect(page.getByRole('button', { name: '公開する' })).toBeVisible();
    await shoot(page, 'admin-06-survey-editor');
  });

  test('回答画面：入力から送信まで', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを作れていない');

    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '社内アンケート（見本）' })).toBeVisible();
    await shoot(page, 'answer-01-form');

    await page.getByRole('radio', { name: 'とても満足' }).check();
    await page.getByRole('radio', { name: '4' }).check();
    await page
      .getByRole('textbox')
      .last()
      .fill('休憩室が広くなって助かっています。');
    await shoot(page, 'answer-02-filled');

    // **送信までの最短時間を待つ。** 速すぎる送信は bot として断られる
    await page.waitForTimeout(4000);

    await page.getByRole('button', { name: '送信する' }).click();
    await expect(page.getByRole('heading', { name: 'ご回答ありがとうございました。' })).toBeVisible();
    await shoot(page, 'answer-03-completed');
  });

  test('回答画面：携帯の見え方', async ({ page }) => {
    test.skip(publicId === '', '先の試験でアンケートを作れていない');

    // **同じ画面を狭い幅で撮る。** 取説には両方載せたい
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '社内アンケート（見本）' })).toBeVisible();
    await shoot(page, 'answer-05-mobile');
  });

  test('回答画面：受け付けていないとき', async ({ page }) => {
    await page.goto('/f/pub-not-exist');
    await expect(page.getByRole('heading', { name: 'アンケートが見つかりません' })).toBeVisible();
    await shoot(page, 'answer-04-not-found');
  });

  test('管理画面：プレビュー', async ({ page }) => {
    test.skip(surveyId === '', '先の試験でアンケートを作れていない');

    // **公開する前に、回答画面と同じ描き方で確かめる画面**
    await page.goto(`/admin/surveys/${surveyId}`);
    await page.getByRole('button', { name: 'プレビュー' }).click();

    await expect(page.getByRole('dialog', { name: 'プレビュー' })).toBeVisible();
    await shoot(page, 'admin-11-preview', false);
  });

  test('管理画面：操作の記録', async ({ page }) => {
    // **ここまでの試験で記録が溜まっている**（初期設定・2 要素の登録・アンケートの作成）
    await page.goto('/admin/audit-logs');

    await expect(page.getByRole('heading', { name: '操作の記録' })).toBeVisible();

    // **空の表を撮らない。** 取説に載せる図としては何も伝わらない
    await expect(page.locator('tbody tr').first()).toBeVisible();

    await shoot(page, 'admin-09-audit-log');
  });

  test('管理画面：送信状況', async ({ page }) => {
    await page.goto('/admin/outbox');

    await expect(page.getByRole('heading', { name: '送信状況' })).toBeVisible();

    // **滞留が無くても「無い」と読めること。** 空の画面こそ取説に要る
    await shoot(page, 'admin-10-outbox');
  });

  test('管理画面：ログイン', async ({ page }) => {
    test.skip(secretBase32 === '', '先の試験で 2 要素を登録できていない');

    await page.goto('/admin');
    await page.getByRole('button', { name: 'ログアウト' }).click();

    await expect(page.getByRole('heading', { name: '管理画面にログイン' })).toBeVisible();
    await shoot(page, 'admin-07-login');

    await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
    await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
    await page.getByRole('button', { name: '次へ' }).click();

    await expect(page.getByRole('heading', { name: '認証アプリの数字を入力' })).toBeVisible();
    await shoot(page, 'admin-08-totp');
  });
});
