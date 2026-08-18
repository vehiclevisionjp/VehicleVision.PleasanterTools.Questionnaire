import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// ビルド成果物は .Web の wwwroot へ出す。**単一のアプリとして配信するため**
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../VehicleVision.PleasanterTools.Questionnaire.Web/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // 開発時は BFF へ中継する
    proxy: {
      '/api': 'http://localhost:8081',
    },
  },
});
