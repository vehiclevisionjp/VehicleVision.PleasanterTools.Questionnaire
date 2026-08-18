import { resolve } from 'node:path';
import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// ビルド成果物は .Web の wwwroot へ出す。**単一のアプリとして配信するため**
export default defineConfig({
  plugins: [svelte()],
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
});
