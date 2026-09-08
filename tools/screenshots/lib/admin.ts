import { existsSync } from 'node:fs';
import type { Browser } from '@playwright/test';
import { demoAdmin } from './setup';
import { totp } from './totp';

/**
 * 管理画面へログインした状態を用意する。
 *
 * **初期設定（最初の管理者を登録する）は、まっさらな検証環境で 1 回しか通らない。**
 * 先に走った試験が既に管理者を作っていれば、その cookie を引き継ぐしかない。
 * 逆に、その試験だけを狙って走らせたときは自分で作る必要がある。
 * **どちらでも通るように、控えがあれば使い、無ければ作る。**
 */

/**
 * 既に通っているログインの控え。**先に走る試験が書き出すもの。**
 *
 * - `artifacts/auth.json` … `specs/manual.spec.ts`
 * - `artifacts/branching-auth.json` … `specs/branching.spec.ts`
 * - `artifacts/secure-context-auth.json` … この関数が作るもの
 */
const knownAuthFiles = [
  'artifacts/auth.json',
  'artifacts/branching-auth.json',
  'artifacts/secure-context-auth.json',
];

/** 自分で作ったときの置き場。 */
const ownAuthFile = 'artifacts/secure-context-auth.json';

/**
 * ログイン済みの状態をしまったファイルの場所を返す。
 *
 * **管理者が既に居るのに控えが無い**ときは、初期設定の画面が出ないので作れない。
 * その場合は**黙って進まず落とす。** 401 だけ見せられても原因が分からない。
 */
export async function ensureAdminStorageState(
  browser: Browser,
  baseURL: string,
): Promise<string> {
  const existing = knownAuthFiles.find((file) => existsSync(file));
  if (existing !== undefined) {
    return existing;
  }

  const context = await browser.newContext({ baseURL });

  try {
    const page = await context.newPage();
    await page.goto('/admin');

    // **画面が描かれるまで待つ。** 描き終える前に数えると、
    // 「初期設定の画面が無い」と誤って判断してしまう
    const setupHeading = page.getByRole('heading', { name: '最初の管理者を登録する' });
    const loginHeading = page.getByRole('heading', { name: '管理画面にログイン' });
    await setupHeading.or(loginHeading).first().waitFor();

    if ((await setupHeading.count()) === 0) {
      throw new Error(
        '管理者が既に居るのに、ログインの控え（artifacts/*.json）が無い。'
          + 'まっさらな検証環境で走らせるか、先に manual / branching の試験を通すこと',
      );
    }

    await page.getByLabel('ログイン ID').fill(demoAdmin.loginId);
    await page.getByLabel('パスワード', { exact: true }).fill(demoAdmin.password);
    await page.getByLabel('パスワード（確認）').fill(demoAdmin.password);
    await page.getByRole('button', { name: '登録する' }).click();

    // **2 要素の登録まで通さないと管理画面へ入れない**
    await page.getByRole('heading', { name: '2 要素認証を登録する' }).waitFor();
    const secret = (await page.locator('.secret code').innerText()).replace(/\s/g, '');

    await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
    await page.getByRole('button', { name: '登録する' }).click();

    await page.getByRole('heading', { name: '復旧コードを控えてください' }).waitFor();
    await page.getByLabel('控えました').check();
    await page.getByRole('button', { name: '管理画面へ進む' }).click();

    await page.getByRole('heading', { name: 'アンケート' }).waitFor();
    await context.storageState({ path: ownAuthFile });
  } finally {
    await context.close();
  }

  return ownAuthFile;
}
