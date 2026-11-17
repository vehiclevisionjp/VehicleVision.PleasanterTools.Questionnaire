/**
 * 検証に失敗したとき、最初の誤りのある設問の最初の入力へフォーカスを移す（Issue #865）。
 *
 * **誤りは `role="alert"` で読み上げられるが、フォーカスが押したボタンに残ると、
 * キーボード・スクリーンリーダーの利用者は誤りの場所へ自分で戻るしかない。**
 * 誤りのある設問が無い、または入力が見つからないときは何もしない。
 */
export function focusFirstError(root: ParentNode = document): boolean {
  const field = root.querySelector('fieldset.has-error');
  const target = field?.querySelector<HTMLElement>('input:not([disabled]), select, textarea, button');
  if (!target) {
    return false;
  }

  target.focus();
  return true;
}

interface FocusableLike {
  focus(options?: { preventScroll?: boolean }): void;
  setAttribute?(name: string, value: string): void;
}

/** フォーカスの探索に使う、`document` の代役でも足りる最小の形。 */
export interface FocusRoot {
  querySelector(selector: string): unknown;
  activeElement: unknown;
  body: unknown;
}

/**
 * ページが変わったあと、そのページの見出しへフォーカスを移す（Issue #900）。
 *
 * **押した「戻る」「再開する」などの釦は描き直しで消え、フォーカスが `<body>` へ落ちる。**
 * 「次へ」は同じ釦に残るが、ページが変わったことが読み上げられない。
 * 見出し（無ければ題名）を `tabindex="-1"` で受け皿にして、そこから読み始められるようにする。
 */
export function focusPageHeading(root: FocusRoot): boolean {
  const heading = (root.querySelector('main h2') ?? root.querySelector('main h1')) as FocusableLike | null;
  if (!heading) {
    return false;
  }

  heading.setAttribute?.('tabindex', '-1');
  heading.focus({ preventScroll: true });
  return true;
}

/**
 * 送信（または添付の拒否）のあと、フォーカスが `<body>` へ落ちていたら戻す（Issue #900）。
 *
 * **送信中は釦が無効になり、フォーカスが外れる。** 誤りの一覧があればそこへ、無ければ送信釦へ。
 * すでにどこかにフォーカスがあれば（誤りのある入力へ移した場合など）何もしない。
 */
export function focusSubmitResult(root: FocusRoot): boolean {
  if (root.activeElement && root.activeElement !== root.body) {
    return false;
  }

  const message = root.querySelector('nav.actions') ? findSubmitMessage(root) : null;
  const target = message ?? (root.querySelector('nav.actions button:not([disabled])') as FocusableLike | null);
  if (!target) {
    return false;
  }

  target.setAttribute?.('tabindex', '-1');
  target.focus();
  return true;
}

function findSubmitMessage(root: FocusRoot): FocusableLike | null {
  return root.querySelector('form > .error[role="alert"]') as FocusableLike | null;
}
