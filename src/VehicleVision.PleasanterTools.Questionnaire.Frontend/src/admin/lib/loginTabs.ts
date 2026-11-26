/**
 * 管理画面のログインのタブ切り替え（Issue #1056。親 Overview#60）。
 *
 * **表示の切り替えだけを決める。** 認証・送信・保存・どの方式を使えるかの判断はサーバ側の責務で、
 * ここは「サーバが使えると返した方式」を並べて、どれを開くかを決めるだけ。
 *
 * 契約は DesignReference の「自前ログインのSSO / IDPW / Passkey切り替え」
 * （docs/webui.md、components/app-login-tabs.js、scripts/check-login-tabs.mjs）に合わせる。
 * 部品そのものは取り込まない（CSS 変数が違い、SPA では DOM を後から差し替えるより
 * 描画の側でタブを組む方が素直なため）。**属性名（`data-vv-login-*`）・ARIA・キー操作の契約を揃える。**
 * Passkey のタブは Issue #1068（親 Core#213）。サーバが使えると返したときだけ並べる。
 */

/** 方式の並び順。**初期選択は先頭（SSO）。** */
export const LOGIN_METHODS = ['sso', 'local', 'passkey'] as const;

export type LoginMethod = (typeof LOGIN_METHODS)[number];

/** サーバが使えると返した方式を、決まった順に並べる。 */
export function availableMethods(input: {
  ssoAvailable: boolean;
  passwordAvailable: boolean;
  /** サーバがパスキーを使えると返したか（省略は使えない）。 */
  passkeyAvailable?: boolean;
}): LoginMethod[] {
  const methods: LoginMethod[] = [];
  if (input.ssoAvailable) {
    methods.push('sso');
  }
  if (input.passwordAvailable) {
    methods.push('local');
  }
  if (input.passkeyAvailable === true) {
    methods.push('passkey');
  }
  return methods;
}

/** **2 方式以上のときだけタブにする。** 1 方式なら通常の区画、0 方式なら何も出さない。 */
export function shouldShowTabs(methods: readonly LoginMethod[]): boolean {
  return methods.length >= 2;
}

/**
 * 開く方式。**指定が使えない方式なら先頭**（初期は SSO）。
 *
 * 再描画のあとも、利用者が選んだ方式を保つために使う。
 */
export function resolveSelected(
  methods: readonly LoginMethod[],
  requested: LoginMethod | null | undefined,
): LoginMethod | null {
  if (requested && methods.includes(requested)) {
    return requested;
  }
  return methods[0] ?? null;
}

/**
 * キー操作で移る先の番号。**左右は循環、Home／End は先頭／末尾。** 対象外のキーは `null`。
 */
export function nextTabIndex(key: string, index: number, count: number): number | null {
  if (count <= 0) {
    return null;
  }
  switch (key) {
    case 'ArrowRight':
      return (index + 1) % count;
    case 'ArrowLeft':
      return (index + count - 1) % count;
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return null;
  }
}

/** タブ・区画の id。**文書内で一意**にする（`aria-controls` / `aria-labelledby` が指す）。 */
export function panelId(method: LoginMethod): string {
  return `login-${method}`;
}

export function tabId(method: LoginMethod): string {
  return `${panelId(method)}-tab`;
}
