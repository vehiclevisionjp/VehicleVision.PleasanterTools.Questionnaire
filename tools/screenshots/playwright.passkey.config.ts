import { defineConfig, devices } from '@playwright/test';

/**
 * パスキーのブラウザ試験（Issue #1085）の設定。
 *
 * **取説用のスクリーンショットの設定（playwright.config.ts）とは分ける。**
 * あちらは見た目を揃えるために倍率を 2 にし、トレースも取らない。ここは「落ちた原因が追える」ことが目的で、
 * 失敗したときにスクリーンショットとトレースを残す。
 *
 * **対象のアプリは、パスキーを有効にして起動したもの**（compose.yaml の `DEV_ADMIN_PASSKEY*`。
 * 手順は README.md の「パスキーのブラウザ試験」）。
 */
export default defineConfig({
  testDir: './specs',
  testMatch: /passkey\.spec\.ts/,
  // **並列にしない。** 管理者を 1 人作って順に辿る
  workers: 1,
  fullyParallel: false,
  forbidOnly: true,
  // **再実行で通してしまわない。** 不安定さを隠すと、直す機会を失う
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'report/passkey-html' }]],
  // **ホストへ出る場所（compose の report のマウント）の下に置く。** CI はここを artifact にする
  outputDir: './report/passkey-artifacts',

  use: {
    // **自己署名の https を `questionnaire-app` で開く。** WebAuthn は安全なコンテキストが要る。
    // この構成を選んだ理由は README.md を参照
    baseURL: process.env['QUESTIONNAIRE_PASSKEY_BASE_URL'] ?? 'https://questionnaire-app:8443',
    // **検証環境の自己署名の証明書だけ。** 本番の設定には持ち込まない（Issue #67）
    ignoreHTTPSErrors: true,
    locale: 'ja-JP',
    timezoneId: 'Asia/Tokyo',
    colorScheme: 'light',
    // **失敗したときだけ残す。** 通った回の成果物で artifact を膨らませない
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    // 容器の /dev/shm は既定で 64 MB しかない（playwright.config.ts と同じ理由）
    launchOptions: { args: ['--disable-dev-shm-usage'] },
  },

  projects: [
    {
      // **WebAuthn の仮想認証器は CDP（Chromium）の機能。** 他のブラウザでは試せない
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 900 } },
    },
  ],
});
