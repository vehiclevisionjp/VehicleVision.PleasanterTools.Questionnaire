/**
 * 公開の流れ（Issue #1057）。**公開は「下書き → 本公開」の 2 ステップ**で、
 * テスト公開は段階ではなく、下書きに付けるオプション（チェック）。
 *
 * エディタと一覧が同じ判断を使うよう、状態の値から「何を出すか」だけをここに置く。
 * 状態の値: 0 下書き / 1 本公開 / 2 停止中 / 3 テスト公開中。
 */

/** 本公開を押せるか。**下書きからも、テスト公開中からも押せる**（サーバと同じ）。 */
export function canPublish(status: number): boolean {
  return status === 0 || status === 3;
}

/** テスト公開のチェックを出すか。本公開中・停止中は、下書きへ戻すまで出さない。 */
export function showsTestPublishCheck(status: number): boolean {
  return status === 0 || status === 3;
}

/** テスト公開のチェックが ON か。**ON ＝ 下書きがテスト公開されている（公開 URL が付く）。** */
export function isTestPublishChecked(status: number): boolean {
  return status === 3;
}

/**
 * 本公開の前に「下書きを反映するか」を選ばせるか。
 *
 * **固めた版と下書きが食い違うのは、テスト公開中だけ**（下書きから本公開するときは、
 * その場で下書きを固めるので食い違いようがない）。未保存の編集も、保存すれば食い違いになる。
 */
export function needsReflectChoice(
  status: number,
  hasUnpublishedChanges: boolean,
  hasUnsavedChanges: boolean,
): boolean {
  return status === 3 && (hasUnpublishedChanges || hasUnsavedChanges);
}
