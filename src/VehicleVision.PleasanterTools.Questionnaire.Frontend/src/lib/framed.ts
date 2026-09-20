/** 回答画面が別の文書の枠内で動いているか。 */
export function isFramed(context: { self: unknown; top: unknown } = window): boolean {
  return context.self !== context.top;
}

/** 枠内の回答画面から送る申告ヘッダ。 */
export function framedHeaders(
  context: { self: unknown; top: unknown } = window,
): Record<string, string> {
  return isFramed(context) ? { 'X-Questionnaire-Framed': '1' } : {};
}
