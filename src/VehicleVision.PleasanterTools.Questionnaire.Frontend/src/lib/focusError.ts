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
