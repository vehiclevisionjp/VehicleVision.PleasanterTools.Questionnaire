import { expect, type Page } from '@playwright/test';

/**
 * まっさらな検証環境であることを、**撮り始める前に**確かめる（Issue #65）。
 *
 * ⚠️ **汚れた環境で撮ると、分からない形で落ちる。**
 *
 * 実際に起きたこと。結合テストを何度も走らせた検証環境には
 * **アンケートの行が 1367 件**溜まっていた。管理画面の一覧はそれを全部描くので、
 * ページ全体の写しが巨大になり、Chromium が
 * `Protocol error (Page.captureScreenshot): Unable to capture screenshot`
 * を返す。**落ちる場所は走らせるたびに変わる**ので、
 * 特定の画面の問題にも、倍率の問題にも見える（どちらでもない）。
 *
 * **DB を空にすれば、同じ倍率のまま 9 件すべて通る。**
 *
 * **早い段階で、読んで分かる形で落とす。** 撮れないことより、
 * 撮れない理由が分からないことの方が高く付く。
 */

/**
 * 一覧に並んでよいアンケートの本数。
 *
 * **見本で作るのは 1 本。** 桁が違えば残骸だと分かる。
 * 取説の図としても、知らないアンケートが並んでいる一覧は載せられない。
 */
const MaxSurveys = 20;

/**
 * 管理者がまだ 1 人も居ないこと。
 *
 * **初期設定の画面は、この状態でしか出ない。** 取説に要る画面なので、
 * 先に別の spec が管理者を作っていると撮れない
 * （`branching.spec.ts` などが作る。**実際に踏んだ**）。
 */
export async function expectNoAdminYet(page: Page): Promise<void> {
  await page.goto('/admin');

  const setup = page.getByRole('heading', { name: '最初の管理者を登録する' });
  const login = page.getByRole('heading', { name: '管理画面にログイン' });

  // **描き終えるまで待つ。** 描く前に数えると、必ず「無い」になる
  await setup.or(login).first().waitFor();

  expect(
    await setup.count(),
    '管理者が既に居るので、初期設定の画面を撮れない。'
      + 'まっさらな検証環境で走らせること（docker compose down -v）。'
      + '**ほかの spec と混ぜて走らせると、先に走った方が管理者を作ってしまう**',
  ).toBeGreaterThan(0);
}

/**
 * アンケートの残骸が溜まっていないこと。
 *
 * **ログイン済みの状態で呼ぶこと**（一覧の口は認証が要る）。
 */
export async function expectFewSurveys(page: Page): Promise<void> {
  const response = await page.request.get('/api/admin/surveys');

  expect(response.ok(), `アンケートの一覧を読めなかった: ${response.status()}`).toBeTruthy();

  const surveys = (await response.json()) as { surveys?: unknown[] } | unknown[];
  const count = Array.isArray(surveys) ? surveys.length : (surveys.surveys?.length ?? 0);

  expect(
    count,
    `アンケートが ${count} 件ある。検証環境に結合テストの残骸が溜まっている。`
      + '一覧のページが長くなりすぎ、ページ全体の写しが '
      + '「Unable to capture screenshot」で撮れなくなる（Issue #65）。'
      + 'docker compose down -v からやり直すこと',
  ).toBeLessThanOrEqual(MaxSurveys);
}
