import { expect, test, type Page } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { prepareSurvey } from '../lib/setup';

/**
 * **`crypto.subtle` の経路が本当に走ることを確かめる**（Issue #67）。
 *
 * proof-of-work の計算は 2 通りある（`Frontend/src/lib/altcha.ts` の `digest`）。
 *
 * 1. `crypto.subtle.digest`（**本番で実際に使われるのはこちら**）
 * 2. 自前の SHA-256（`Frontend/src/lib/sha256.ts`。**控え**）
 *
 * **Web Crypto は「安全なコンテキスト」（https か localhost）でしか使えない。**
 * 検証環境は容器同士を平文の http で繋いでいるので、
 * **これまで端から端まで通す試験は控えしか通していなかった。**
 * 本番で使われる経路が一度も試されていない状態だった（#55 / PR #63）。
 *
 * そこで検証環境のアプリを 8443 で https でも待ち受けるようにし（`compose.yaml`）、
 * **両方の経路を、それぞれの向きから確かめる。**
 *
 * - https … `crypto.subtle.digest` が**実際に呼ばれ**、その解答がサーバに通ること
 * - 平文 … `crypto.subtle` が**無く**、それでも控えの解答がサーバに通ること
 *
 * **「呼ばれた」は数えて確かめる。** `isSecureContext` を見るだけでは、
 * 画面の側が本当にその道を通ったかは分からない。
 */

/** 平文の入口。**既存の試験と同じ既定値。** */
const httpBaseUrl = process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080';

/** https の入口。**自己署名なので `ignoreHTTPSErrors` が要る**（playwright.config.ts）。 */
const httpsBaseUrl = process.env['QUESTIONNAIRE_HTTPS_BASE_URL'] ?? 'https://questionnaire-app:8443';

/** Pleasanter のサイト ID。**見本なので実在しなくてよい**（公開までは通る）。 */
const demoSiteId = Number(process.env['SHOT_SITE_ID'] ?? '1');

/**
 * **localhost は平文でも安全なコンテキストになる。**
 * そこへ向けて走らせると「平文では控えが使われる」が成り立たず、
 * **通ったように見えて何も確かめていない**ことになる。
 */
const httpHost = new URL(httpBaseUrl).hostname;
const httpIsLocalhost = httpHost === 'localhost' || httpHost === '127.0.0.1' || httpHost === '[::1]';

/** 数えた回数を持たせる窓口。**画面側の型を汚さないため、ここで名前を付ける。** */
interface DigestCounter {
  __subtleDigestCalls?: number;
}

/**
 * `crypto.subtle.digest` が呼ばれた回数を数える。
 *
 * **製品のコードへ数える仕掛けを足さない。** 試験のために本番の道を変えると、
 * 確かめたい当のものが変わってしまう。**外側から包んで数える。**
 *
 * `addInitScript` は**ページのスクリプトより先**に走るので、
 * 画面が開いた直後に解き始める課題も取りこぼさない。
 */
async function watchSubtleDigest(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const counter = globalThis as unknown as DigestCounter;
    counter.__subtleDigestCalls = 0;

    // **安全でないコンテキストでは `crypto.subtle` そのものが無い。**
    // 数えるものが無いので、0 のままにしておく
    const subtle: SubtleCrypto | undefined = globalThis.crypto?.subtle;
    if (subtle === undefined) {
      return;
    }

    // `digest` は `SubtleCrypto.prototype` のメソッド。
    // ここでの代入は**実体側に自分のものとして生える**ので、元は壊れない
    const original = subtle.digest.bind(subtle);
    subtle.digest = function counted(algorithm, data) {
      counter.__subtleDigestCalls = (counter.__subtleDigestCalls ?? 0) + 1;
      return original(algorithm, data);
    };
  });
}

/** 数えた回数を読む。 */
async function digestCalls(page: Page): Promise<number> {
  return page.evaluate(() => (globalThis as unknown as DigestCounter).__subtleDigestCalls ?? 0);
}

/** その画面が安全なコンテキストとして扱われているか。 */
async function secureContextState(
  page: Page,
): Promise<{ protocol: string; isSecureContext: boolean; subtle: string }> {
  return page.evaluate(() => ({
    protocol: location.protocol,
    isSecureContext: window.isSecureContext,
    subtle: typeof globalThis.crypto?.subtle,
  }));
}

test.describe.configure({ mode: 'serial' });

test.describe('安全なコンテキスト', () => {
  test('https で開くと安全なコンテキストになり crypto.subtle が使える', async ({ page }) => {
    await page.goto(`${httpsBaseUrl}/`);

    const state = await secureContextState(page);

    expect(state.protocol, 'https で開けていない').toBe('https:');
    expect(
      state.isSecureContext,
      '**https なのに安全なコンテキストになっていない。**'
        + ' 自己署名の証明書を素通しにしただけでは駄目だったということ',
    ).toBe(true);
    expect(
      state.subtle,
      '**安全なコンテキストなのに `crypto.subtle` が無い。** 本番の経路が使えない',
    ).toBe('object');
  });

  test('平文で開くと安全なコンテキストにならない', async ({ page }) => {
    // **localhost 相手では成り立たない。** 通ったように見せない
    test.skip(
      httpIsLocalhost,
      `QUESTIONNAIRE_BASE_URL が localhost（${httpBaseUrl}）。平文でも安全なコンテキストになる`,
    );

    await page.goto(`${httpBaseUrl}/`);

    const state = await secureContextState(page);

    expect(state.protocol, '平文で開けていない').toBe('http:');
    // **これが崩れたら、控え（sha256.ts）はもう要らないことになる。**
    // 消してよいかの判断材料なので、黙って直さずに理由を確かめること
    expect(state.isSecureContext, '平文なのに安全なコンテキストになっている').toBe(false);
    expect(state.subtle, '平文なのに `crypto.subtle` がある').toBe('undefined');
  });
});

test.describe('proof-of-work の経路', () => {
  /** 回答画面を開くための公開 ID。**下ごしらえで作る。** */
  let publicId = '';

  // **下ごしらえはここだけに置く。** 上の 2 件（安全なコンテキストの確認）は
  // 管理画面もアンケートも要らない。**下ごしらえで転んでも、そちらは答えを出せる**
  test.beforeAll(async ({ browser }) => {
    // **アンケートを作るのは平文側の管理画面から。**
    // 確かめたいのは回答画面の計算であって、管理画面の経路ではない
    const authFile = await ensureAdminStorageState(browser, httpBaseUrl);
    const context = await browser.newContext({ baseURL: httpBaseUrl, storageState: authFile });

    try {
      publicId = (await prepareSurvey(context.request, demoSiteId)).publicId;
    } finally {
      await context.close();
    }
  });

  test('https では crypto.subtle が実際に呼ばれ、その解答が通る', async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを作れていない');

    await watchSubtleDigest(page);
    await page.goto(`${httpsBaseUrl}/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '社内アンケート（見本）' })).toBeVisible();

    // **課題は画面を開いた時点で解き始める**（`App.svelte` の `solveAltcha`）。
    // 解き終わるまで待たずに入力できる作りなので、呼ばれるまで待つ
    await expect
      .poll(async () => digestCalls(page), {
        timeout: 30_000,
        message:
          '**`crypto.subtle.digest` が 1 度も呼ばれていない。**'
          + ' https で開いているのに控え（sha256.ts）の側を通っている',
      })
      .toBeGreaterThan(0);

    await page.getByRole('radio', { name: 'とても満足' }).check();

    // **送信までの最短時間を待つ。** 速すぎる送信は bot として断られる
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();

    // **サーバが受け付けて初めて「経路が通った」と言える。**
    // 呼ばれただけでは、答えが正しいかは分からない
    await expect(page.getByRole('heading', { name: 'ご回答ありがとうございました。' })).toBeVisible();
  });

  test('平文では控えが解き、その解答も通る', async ({ page }) => {
    test.skip(publicId === '', '下ごしらえでアンケートを作れていない');
    test.skip(
      httpIsLocalhost,
      `QUESTIONNAIRE_BASE_URL が localhost（${httpBaseUrl}）。控えの側を通らない`,
    );

    await watchSubtleDigest(page);
    await page.goto(`${httpBaseUrl}/f/${publicId}`);
    await expect(page.getByRole('heading', { name: '社内アンケート（見本）' })).toBeVisible();

    await page.getByRole('radio', { name: 'とても満足' }).check();

    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: '送信する' }).click();
    await expect(page.getByRole('heading', { name: 'ご回答ありがとうございました。' })).toBeVisible();

    // **送信が通ったのに 1 度も呼ばれていない＝控えが解いた。**
    // 送信の後に見るのが肝心で、先に見ると「まだ解いていないだけ」と区別できない
    expect(
      await digestCalls(page),
      '平文なのに `crypto.subtle.digest` が呼ばれている',
    ).toBe(0);
  });
});
