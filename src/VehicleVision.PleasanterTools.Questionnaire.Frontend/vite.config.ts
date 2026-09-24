import { resolve } from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
// **`vite` ではなく `vitest/config` から取る。** `test` の設定を書けるのはこちら
import { defineConfig } from 'vitest/config';

// ビルド成果物は .Web の wwwroot へ出す。**単一のアプリとして配信するため**
export default defineConfig({
  plugins: [svelte()],
  // **配置先のサブパスを組み立て時に焼き込まない**（Issue #465）。
  //   - JS と CSS からの参照は**自分の位置からの相対**にする（`./`）。
  //     サブパスがどこでも、資産同士は同じ `assets/` の中で見つかる
  //   - HTML からの参照だけは、開いている URL（`/f/{publicId}` など）によって
  //     相対の基準が変わるので**埋め込み先の印**にしておき、サーバが配信時に
  //     `QUESTIONNAIRE_PATH_BASE` へ差し替える（`HtmlShell.cs`）
  // **組み立て直さずに、同じ成果物を `/` にもサブパスにも置ける。**
  base: './',
  experimental: {
    renderBuiltUrl(filename, { hostType }) {
      return hostType === 'html' ? `__QUESTIONNAIRE_BASE_PATH__/${filename}` : { relative: true };
    },
  },
  build: {
    outDir: '../VehicleVision.PleasanterTools.Questionnaire.Web/wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      // **回答画面と管理画面を別の束にする。**
      // 回答者へ管理画面のコードを配らないため
      input: {
        index: resolve(import.meta.dirname, 'index.html'),
        admin: resolve(import.meta.dirname, 'admin.html'),
      },
    },
  },
  server: {
    // 開発時は BFF へ中継する
    proxy: {
      '/api': 'http://localhost:8081',
    },
  },
  test: {
    // **`lib/` の純粋な関数だけを見る**（Issue #110）。
    // コンポーネントの描画試験まで広げると、依存が増える割に
    // 壊れやすく直すのに時間を取られる試験が量産される
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
