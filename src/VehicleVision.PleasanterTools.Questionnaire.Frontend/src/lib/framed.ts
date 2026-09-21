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

/** 親から渡された参照元を、安全に postMessage の宛先へ変換する。 */
export function heightReportTargetOrigin(isInsideFrame: boolean, referrer: string): string | null {
  if (!isInsideFrame || referrer === '') {
    return null;
  }

  try {
    return new URL(referrer).origin;
  } catch {
    return null;
  }
}

/** 同じ高さの通知を繰り返さない。 */
export function shouldReportHeight(previousHeight: number | null, height: number): boolean {
  return Number.isSafeInteger(height) && height >= 0 && previousHeight !== height;
}

/** 親サイトが受け入れる埋め込み回答画面の最大の高さ。 */
export const MAXIMUM_EMBEDDED_FORM_HEIGHT = 10_000;

interface HeightMessage {
  type: 'questionnaire:height';
  publicId: string;
  height: number;
}

function isHeightMessage(value: unknown): value is HeightMessage {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return false;
  }

  const message = value as Record<string, unknown>;
  return (
    message.type === 'questionnaire:height' &&
    typeof message.publicId === 'string' &&
    Number.isSafeInteger(message.height) &&
    (message.height as number) >= 0
  );
}

/**
 * 埋め込み回答画面から来た高さを受け入れてよいか判定する。
 *
 * 生成元だけでは同じサイトを指す別の iframe を区別できないため、送信元も照合する。
 */
export function receivedEmbeddedFormHeight(
  event: { origin: string; source: unknown; data: unknown },
  applicationOrigin: string,
  iframeWindow: unknown,
  publicId: string,
): number | null {
  if (event.origin !== applicationOrigin || event.source !== iframeWindow || !isHeightMessage(event.data)) {
    return null;
  }

  if (event.data.publicId !== publicId || event.data.height > MAXIMUM_EMBEDDED_FORM_HEIGHT) {
    return null;
  }

  return event.data.height;
}
