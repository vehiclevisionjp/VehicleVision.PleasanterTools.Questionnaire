import { expect, test, type APIRequestContext } from '@playwright/test';
import { ensureAdminStorageState } from '../lib/admin';
import { readEnrollmentSecret, totp } from '../lib/totp';

/**
 * 所属制限を実際の Pleasanter とアプリで確かめる（Issue #505）。
 * 専用の空 DB と 03_sso_users.sql の投入が必要。通常の写しの一式とは別に実行する。
 * SSO_MEMBERSHIP_E2E=1、QUESTIONNAIRE_BASE_URL、SSO_MEMBERSHIP_PLEASANTER_URL、
 * SSO_MEMBERSHIP_API_KEY（検証環境のキー）を指定する。
 */
test('所属制限で登録・ログイン・既存セッションを保護する @standalone', async ({ browser, baseURL, playwright }) => {
  test.skip(process.env['SSO_MEMBERSHIP_E2E'] !== '1', '専用の所属制限検証環境で実行する');
  test.setTimeout(180_000);
  const pleasanterUrl = process.env['SSO_MEMBERSHIP_PLEASANTER_URL']!;
  const apiKey = process.env['SSO_MEMBERSHIP_API_KEY']!;
  expect(pleasanterUrl).toBeTruthy();
  expect(apiKey).toBeTruthy();
  const state = await ensureAdminStorageState(browser, baseURL!);
  const admin = await browser.newContext({ baseURL, storageState: state });
  const member = await browser.newContext({ baseURL });
  const stranger = await browser.newContext({ baseURL });
  const api = await playwright.request.newContext({ baseURL: pleasanterUrl });
  const settingsPath = '/api/admin/pleasanter-sso/settings';
  const original = await (await admin.request.get(settingsPath)).json();

  async function save(changes: Record<string, unknown>) {
    const current = await (await admin.request.get(settingsPath)).json();
    const response = await admin.request.put(settingsPath, { data: { ...current, ...changes } });
    expect(response.ok(), await response.text()).toBe(true);
  }
  async function check(request: APIRequestContext, status: number) {
    const response = await request.post('/api/admin/pleasanter-sso/check', { data: {} });
    expect(response.status(), await response.text()).toBe(status);
    return response.json();
  }
  async function users() {
    return (await (await admin.request.get('/api/admin/users')).json()).users as { loginId: string }[];
  }
  async function groupPost(path: string, data: Record<string, unknown>) {
    const response = await api.post(path, { data: { ApiVersion: 1.1, ApiKey: apiKey, ...data } });
    const body = await response.json();
    expect(body.StatusCode).toBe(200);
    return body;
  }

  let groupId: number | undefined;
  try {
    await save({ enabled: true, internalBaseUrl: pleasanterUrl, loginUrl: `${pleasanterUrl}/users/login`,
      unknownUser: 'Register', allowedDeptIds: '', allowedGroupIds: '', revalidateMinutes: '1' });
    for (const [context, loginId, password] of [
      [member, 'sso-e2e-plain', 'SsoE2e#Plain1'],
      [stranger, 'sso-e2e-stranger', 'SsoE2e#Stranger1'],
    ] as const) {
      const page = await context.newPage();
      await page.goto(`${pleasanterUrl}/users/login`);
      await page.locator('#Users_LoginId').fill(loginId);
      await page.locator('#Users_Password').fill(password);
      await page.locator('#Login').click();
      await expect(page.locator('#Users_LoginId')).not.toBeVisible();
      await page.close();
    }

    // 許可先が空の JIT は、誰も作らず拒否する。
    expect((await check(member.request, 403)).code).toBe('not-allowed');
    expect((await users()).some(user => user.loginId === 'sso-e2e-plain')).toBe(false);

    // 設定欄から保存し、リロード後も値が残る。対象外の人は登録されない。
    const settings = await admin.newPage();
    await settings.goto('/admin/pleasanter-sso-settings');
    await settings.getByLabel('許可する組織 ID').fill('5051');
    await settings.getByRole('button', { name: '保存する', exact: true }).click();
    await expect(settings.getByText('Pleasanter ログイン設定を保存しました。')).toBeVisible();
    await settings.reload();
    await expect(settings.getByLabel('許可する組織 ID')).toHaveValue('5051');
    expect((await check(stranger.request, 403)).code).toBe('not-allowed');
    expect((await users()).some(user => user.loginId === 'sso-e2e-stranger')).toBe(false);

    // 許可された人だけ作られ、本アプリの 2 要素登録も省略されない。
    const page = await member.newPage();
    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: '2 要素認証を登録する' })).toBeVisible();
    const secret = await readEnrollmentSecret(page);
    await page.getByLabel('認証アプリに表示された 6 桁のコード').fill(totp(secret));
    await page.getByRole('button', { name: '登録する' }).click();
    await page.getByRole('heading', { name: '復旧コードを控えてください' }).waitFor();
    await page.getByLabel('控えました').check();
    await page.getByRole('button', { name: '管理画面へ進む' }).click();
    await expect(page.getByRole('heading', { name: 'アンケート', exact: true })).toBeVisible();
    expect((await users()).some(user => user.loginId === 'sso-e2e-plain')).toBe(true);

    // 組織が不一致でも、実際に作ったグループの組織メンバーなら OR 条件で許可される。
    const created = await groupPost('/api/groups/create', { GroupName: `SSO membership ${Date.now()}`, GroupMembers: ['Dept,5051,False'] });
    groupId = created.Id;
    await save({ allowedDeptIds: '999999', allowedGroupIds: String(groupId) });
    const preview = async () => member.request.post(`${settingsPath}/test`, { data: {} });
    // 編集者には設定試験の権限が無い。ログインの入口で判定する。
    expect((await preview()).status()).toBe(403);
    expect((await check(member.request, 200)).next).toBe('totp');
    // 上の再ログインは pending cookie だけ追加する。既存の認証済みセッションは残る。
    const before = await member.request.get('/api/admin/session');
    expect((await before.json()).authenticated).toBe(true);

    await groupPost(`/api/groups/${groupId}/update`, { GroupMembers: [] });
    expect((await check(member.request, 403)).code).toBe('not-allowed');

    // 実時間を進めるのは E2E だけ。再検証で認証済みセッションも失効する。
    await expect.poll(async () => {
      const response = await member.request.get('/api/admin/session');
      if (response.status() === 401) return false;
      expect(response.ok(), `Unexpected session status: ${response.status()}`).toBe(true);
      return (await response.json()).authenticated;
    },
      { timeout: 75_000, intervals: [10_000] }).toBe(false);
    // ローカルの管理者は所属制限の対象外なので、設定を直せる。
    expect((await admin.request.get(settingsPath)).ok()).toBe(true);
  } finally {
    await admin.request.put(settingsPath, { data: original });
    if (groupId !== undefined) await groupPost(`/api/groups/${groupId}/delete`, {});
    await Promise.all([admin.close(), member.close(), stranger.close(), api.dispose()]);
  }
});
