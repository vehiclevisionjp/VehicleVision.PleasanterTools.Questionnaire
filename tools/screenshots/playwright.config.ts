import { defineConfig, devices } from '@playwright/test';

/**
 * 取説用の写しを撮るための設定。
 *
 * **画面の見た目を揃える。** 撮るたびに幅や倍率が違うと、差分が読めない。
 */
export default defineConfig({
  testDir: './specs',
  // **並列にしない。** 同じアンケートと同じ管理者を作り直すため、
  // 並べると互いの行を消し合う（結合テストと同じ理由）
  workers: 1,
  fullyParallel: false,
  // **失敗を握り潰さない。** 撮れていない写しがあるなら落とす
  forbidOnly: true,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'report' }]],
  outputDir: './artifacts',

  use: {
    baseURL: process.env['QUESTIONNAIRE_BASE_URL'] ?? 'http://questionnaire-app:8080',
    // **検証環境の証明書は自己署名。** 名前も `localhost` 側しか持っていないので、
    // `questionnaire-app` で開くと必ず証明書の検査で弾かれる（compose.yaml の devcert）。
    // **検査を外すのは検証環境だけ。本番の設定には持ち込まない**（Issue #67）。
    //
    // **外しても https のまま。** 「安全なコンテキスト」は生成元の scheme で決まるので、
    // `crypto.subtle` は使える（specs/secure-context.spec.ts が実際に見張っている）
    ignoreHTTPSErrors: true,
    // **日本語で撮る。** 既定の言語は ja
    locale: process.env['SHOT_LOCALE'] ?? 'ja-JP',
    timezoneId: 'Asia/Tokyo',
    // 取説に載せるので、余計な装飾を出さない
    colorScheme: 'light',
    reducedMotion: 'reduce',
    screenshot: 'off',
    trace: 'off',
    // **容器の /dev/shm は既定で 64 MB しかない。**
    // 画面全体の写しを撮るときに足りず「Unable to capture screenshot」で落ちる
    // （実際に踏んだ）。**写しの内容とは関係のない、容器側の都合。**
    launchOptions: { args: ['--disable-dev-shm-usage'] },

  },

  projects: [
    {
      name: 'desktop',
      // **写しはデスクトップだけで撮る。**
      // 初期設定は「管理者がまだ 1 人も居ない」状態でしか通らないので、
      // 複数の見え方で走らせると 2 回目が必ず落ちる。
      // 携帯の見え方は、回答画面の中で画面の大きさを変えて撮る
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 900 },
        // **倍率を固定する。** 取説の図の大きさを揃えるため。
        //
        // ⚠️ **2 のままでよい。** ここを 1 に下げると
        // `Unable to capture screenshot` が出なくなるが、**原因はこちらではない**
        // （Issue #65）。真因は**検証環境に溜まったアンケートの行**で、
        // 一覧が長くなりすぎてページ全体の写しが撮れなくなる。
        // **DB を空にすれば 2 のまま通る。** 前提は `lib/fresh.ts` が見ている
        deviceScaleFactor: 2,
      },
    },
    {
      // **携帯では書体の確認だけ。** 写しはデスクトップ側で撮る
      name: 'mobile',
      testMatch: /font\.spec\.ts/,
      use: {
        ...devices['Pixel 7'],
      },
    },
  ],
});
