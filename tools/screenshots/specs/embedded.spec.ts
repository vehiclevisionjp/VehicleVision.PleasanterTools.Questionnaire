import { expect, test, type FrameLocator, type Locator, type Page } from '@playwright/test';
import type { Server } from 'node:https';
import { ensureAdminStorageState } from '../lib/admin';
import { startEmbedParent, stopEmbedParent } from '../lib/embed-parent';
import { prepareEmbeddedSurvey } from '../lib/embedded';

const applicationUrl =
  process.env['QUESTIONNAIRE_HTTPS_BASE_URL'] ?? 'https://questionnaire-app:8443';
const allowedParentUrl = 'https://localhost:9443';
const deniedParentUrl = 'https://localhost:9444';
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');
const submissionMinimumElapsedMilliseconds = 4000;

let allowedParent: Server;
let deniedParent: Server;
let embeddedPublicId = '';
let embeddedAssetId = '';
let blockedPublicId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ browser }) => {
  test.setTimeout(120_000);
  [allowedParent, deniedParent] = await Promise.all([
    startEmbedParent(9443),
    startEmbedParent(9444),
  ]);

  const storageState = await ensureAdminStorageState(browser, applicationUrl);
  const context = await browser.newContext({
    baseURL: applicationUrl,
    storageState,
    ignoreHTTPSErrors: true,
  });
  try {
    const allowed = await prepareEmbeddedSurvey(context.request, demoSiteId, true);
    const blocked = await prepareEmbeddedSurvey(context.request, demoSiteId, false);
    embeddedPublicId = allowed.publicId;
    embeddedAssetId = allowed.assetId;
    blockedPublicId = blocked.publicId;
  } finally {
    await context.close();
  }
});

test.afterAll(async () => {
  await Promise.all([
    allowedParent ? stopEmbedParent(allowedParent) : Promise.resolve(),
    deniedParent ? stopEmbedParent(deniedParent) : Promise.resolve(),
  ]);
});

test('許した別生成元の枠内で高さが増減する', async ({ page }) => {
  const { iframe, form } = await openEmbeddedForm(page, allowedParentUrl, embeddedPublicId);
  await expect(form.getByRole('heading', { name: '埋め込み回答の見本' })).toBeVisible();
  const firstHeight = await settledHeight(iframe);

  await form.getByLabel('お名前').fill('埋め込み 検証');
  await form.getByRole('button', { name: '次へ' }).click();
  await expect(form.getByRole('heading', { name: '長い説明ページ' })).toBeVisible();
  await expect.poll(() => heightOf(iframe)).toBeGreaterThan(firstHeight);
  const secondHeight = await settledHeight(iframe);

  await form.getByRole('button', { name: '次へ' }).click();
  await expect(form.getByRole('heading', { name: '短い確認ページ' })).toBeVisible();
  await expect.poll(() => heightOf(iframe)).toBeLessThan(secondHeight);
});

test('許した別生成元の枠内から回答し、Cookie 付きで配布資産を受け取れる', async ({
  page,
}) => {
  const { form } = await openEmbeddedForm(page, allowedParentUrl, embeddedPublicId);
  await expect(form.getByRole('heading', { name: '埋め込み回答の見本' })).toBeVisible();
  await form.getByLabel('お名前').fill('埋め込み 検証');
  await form.getByRole('button', { name: '次へ' }).click();
  await form.getByRole('button', { name: '次へ' }).click();
  await page.waitForTimeout(submissionMinimumElapsedMilliseconds);
  await form.getByRole('button', { name: '送信する' }).click();

  await expect(form.getByRole('heading', { name: '回答を受け付けました' })).toBeVisible();
  const assetPath = `/api/forms/${embeddedPublicId}/assets/${embeddedAssetId}`;
  await expect
    .poll(async () => (await page.context().cookies(`${applicationUrl}${assetPath}`))
      .find((cookie) => cookie.name === 'q.asset'))
    .toMatchObject({ secure: true, sameSite: 'None' });

  // この URL は q.asset Cookie が無ければ 404 を返す。同じ iframe 内から 200 を得ることで、
  // third-party の HTTPS 要求にも Cookie が実際に送られたことを確かめる。
  const assetStatus = await form.locator('body').evaluate(
    async (_body, path) => (await fetch(path)).status,
    assetPath,
  );
  expect(assetStatus).toBe(200);

  const downloadPromise = page.waitForEvent('download');
  await form.getByRole('link', { name: '検証用資料' }).click();
  expect((await downloadPromise).suggestedFilename()).toBe('embedded-e2e.pdf');
});

test('許していない別生成元では frame-ancestors が表示を止める', async ({ page }) => {
  const formUrl = `${applicationUrl}/f/${embeddedPublicId}`;
  await page.goto(parentPageUrl(deniedParentUrl, formUrl, embeddedPublicId));
  await expect(page.getByTestId('embed-parent-ready')).toBeVisible();

  // 親ページの組み立て完了後も、回答画面のフレームが現れないことを観測する。
  // 先に枠を表示できたか待つ許可側と対になる。固定時間だけ待つと、遅い環境で
  // frame-ancestors の壊れを見逃すため、途中で 1 回でも現れたらその場で失敗させる。
  const observeUntil = Date.now() + 3000;
  await expect.poll(
    () => {
      if (page.frames().some((frame) => frame.url() === formUrl)) {
        return '回答画面を表示した';
      }
      return Date.now() >= observeUntil ? '表示されなかった' : '観測中';
    },
    { timeout: 3500, intervals: [100, 250, 500] },
  ).toBe('表示されなかった');
});

test('埋め込みを許していないアンケートは枠内で断る', async ({ page }) => {
  const { form } = await openEmbeddedForm(page, allowedParentUrl, blockedPublicId);

  await expect(form.getByRole('heading', { name: '埋め込みでは回答できません' })).toBeVisible();
  await expect(form.getByText(
    'このアンケートは埋め込みでは回答できません。別の画面で開いてください。',
  )).toBeVisible();
});

async function openEmbeddedForm(
  page: Page,
  parentUrl: string,
  publicId: string,
): Promise<{ iframe: Locator; form: FrameLocator }> {
  const formUrl = `${applicationUrl}/f/${publicId}`;
  await page.goto(parentPageUrl(parentUrl, formUrl, publicId));
  const iframe = page.locator('iframe');
  const form = page.frameLocator('iframe');
  await expect(page.getByTestId('embed-parent-ready')).toBeVisible();
  await expect(iframe).toBeVisible();
  return { iframe, form };
}

function parentPageUrl(parentUrl: string, formUrl: string, publicId: string): string {
  const url = new URL(parentUrl);
  url.searchParams.set('src', formUrl);
  url.searchParams.set('publicId', publicId);
  return url.toString();
}

async function heightOf(iframe: Locator): Promise<number> {
  return iframe.evaluate((element) => element.getBoundingClientRect().height);
}

async function settledHeight(iframe: Locator): Promise<number> {
  let previous = -1;
  await expect.poll(async () => {
    const current = await heightOf(iframe);
    const stable = current === previous;
    previous = current;
    return stable;
  }).toBe(true);
  return previous;
}
