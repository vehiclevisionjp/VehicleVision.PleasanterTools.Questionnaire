// **書体とアイコンはアプリに同梱している**（Issue #152）。
//
// **アイコンは管理画面だけに読み込む。** 回答者へ配るものは軽くしておきたい。
//
// **明朝体と丸ゴシック体は読まない。** 回答画面のテーマ用なので、
// プレビューでは端末の書体になる（そこだけ本番と差が出る点に注意）。
import '@fontsource-variable/noto-sans-jp';
import '@fontsource-variable/m-plus-1-code';
import '@fontsource/material-icons';
// **色・余白・角丸などは DesignReference のトークン**（Issue #776）。先に色、次に基本
import '../styles/tokens.css';
import '../styles/base.css';
import { mount } from 'svelte';
import AdminApp from './AdminApp.svelte';

const target = document.getElementById('admin');
if (target === null) {
  throw new Error('#admin が見つかりません');
}

export default mount(AdminApp, { target });
