// **書体とアイコンはアプリに同梱している**（Issue #152）。
//
// **アイコンは管理画面だけに読み込む。** 回答者へ配るものは軽くしておきたい。
import '@fontsource-variable/noto-sans-jp';
import '@fontsource-variable/m-plus-1-code';
import '@fontsource/material-icons';
import { mount } from 'svelte';
import AdminApp from './AdminApp.svelte';

const target = document.getElementById('admin');
if (target === null) {
  throw new Error('#admin が見つかりません');
}

export default mount(AdminApp, { target });
