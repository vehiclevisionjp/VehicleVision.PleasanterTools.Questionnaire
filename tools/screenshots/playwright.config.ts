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
    // **日本語で撮る。** 既定の言語は ja
    locale: process.env['SHOT_LOCALE'] ?? 'ja-JP',
    timezoneId: 'Asia/Tokyo',
    // 取説に載せるので、余計な装飾を出さない
    colorScheme: 'light',
    reducedMotion: 'reduce',
    screenshot: 'off',
    trace: 'off',

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
        // **倍率を固定する。** 取説の図の大きさを揃えるため
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
