// **書体はアプリに同梱している**（Issue #152）。
//
// **外部から読み込まない。** 回答者の端末から第三者へ要求を出させないためと、
// **インターネットへ出られないイントラでも同じ見た目で動かすため。**
// unicode-range で分割された woff2 なので、受け取るのは使った範囲だけ。
// テーマで選べる 4 書体すべてを持つ（ゴシック体・明朝体・丸ゴシック体・等幅）
import '@fontsource-variable/noto-sans-jp';
import '@fontsource-variable/noto-serif-jp';
import '@fontsource/m-plus-rounded-1c/japanese-400.css';
import '@fontsource/m-plus-rounded-1c/japanese-700.css';
import '@fontsource/m-plus-rounded-1c/latin-400.css';
import '@fontsource/m-plus-rounded-1c/latin-700.css';
import '@fontsource-variable/m-plus-1-code';
import { mount } from 'svelte';
import App from './App.svelte';

const target = document.getElementById('app');
if (!target) {
  throw new Error('#app が見つからない');
}

export default mount(App, { target });
