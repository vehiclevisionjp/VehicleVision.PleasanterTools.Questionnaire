/**
 * 編集画面で `Esc` を押したとき、一覧へ戻ってよいかを決める（Issue #903）。
 *
 * **`Esc` は入力中の人の操作にも使われる。** 日本語入力（IME）の変換の取り消し、
 * プルダウンを閉じる、確認ダイアログを閉じる。これらの `Esc` まで「戻る」と受け取ると、
 * 入力中に編集画面を離れてしまい、確認ダイアログは閉じても開き直される。
 */
export interface EscapeKeyEvent {
  key: string;
  /** すでに別の処理（ダイアログを閉じるなど）が受け持った。 */
  defaultPrevented: boolean;
  /** 日本語入力の変換中。 */
  isComposing: boolean;
  target: unknown;
}

/** 入力欄・プルダウン・編集可能な領域か。 */
export function isEditingTarget(target: unknown): boolean {
  if (typeof target !== 'object' || target === null) {
    return false;
  }

  const element = target as { tagName?: unknown; isContentEditable?: unknown };
  if (element.isContentEditable === true) {
    return true;
  }

  return (
    typeof element.tagName === 'string' &&
    ['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName.toUpperCase())
  );
}

/** `Esc` で一覧へ戻ってよいか。 */
export function shouldGoBackOnEscape(event: EscapeKeyEvent): boolean {
  return (
    event.key === 'Escape' &&
    !event.defaultPrevented &&
    !event.isComposing &&
    !isEditingTarget(event.target)
  );
}
