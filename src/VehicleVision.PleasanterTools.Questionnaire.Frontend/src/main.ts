// **書体はアプリに同梱している**（Issue #152）。
//
// **外部から読み込まない。** 回答者の端末から第三者へ要求を出させないためと、
// **インターネットへ出られないイントラでも同じ見た目で動かすため。**
// unicode-range で分割された woff2 なので、受け取るのは使った範囲だけ。
// テーマで選べる 4 書体すべてを持つ（ゴシック体・明朝体・丸ゴシック体・等幅）。
// **ゴシック体は DesignReference の Zen Kaku Gothic New**（管理画面と同じ woff2 を共用する。Issue #776・#789）。
// **等幅は DesignReference の Myrica M**（半角:全角 = 1:2。日本語も等幅になる。Issue #778・#789）。
// 明朝体・丸ゴシック体は、作成者が選べるテーマのために維持する
import './styles/app-fonts.css';
import '@fontsource-variable/noto-serif-jp';
import '@fontsource/m-plus-rounded-1c/japanese-400.css';
import '@fontsource/m-plus-rounded-1c/japanese-700.css';
import '@fontsource/m-plus-rounded-1c/latin-400.css';
import '@fontsource/m-plus-rounded-1c/latin-700.css';
// **色・余白・角丸などは DesignReference のトークン**（Issue #776）。先に色、次に基本
import './styles/tokens.css';
import './styles/base.css';
import { mount } from 'svelte';
import App from './App.svelte';

const target = document.getElementById('app');
if (!target) {
  throw new Error('#app が見つからない');
}

export default mount(App, { target });
