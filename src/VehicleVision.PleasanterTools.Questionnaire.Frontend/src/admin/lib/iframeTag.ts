import { MAXIMUM_EMBEDDED_FORM_HEIGHT } from '../../lib/framed';

/** HTML 属性へ安全に埋め込める文字列へ直す。 */
function htmlAttribute(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}

/** script 内の文字列を閉じる文字列として解釈させない。 */
function scriptValue(value: string): string {
  return JSON.stringify(value).replaceAll('<', '\\u003C');
}

/**
 * 高さを親サイトへ通知する iframe と受信側の組。
 *
 * 受信時の検査規則の正本は `receivedEmbeddedFormHeight`。断片との一致は単体テストで守る。
 */
export function iframeTag(url: string, title: string, publicIdValue: string): string {
  const applicationOrigin = new URL(url).origin;
  const publicId = scriptValue(publicIdValue);
  const origin = scriptValue(applicationOrigin);

  return `<iframe src="${htmlAttribute(url)}" title="${htmlAttribute(title)}" loading="lazy" height="800" data-questionnaire-public-id="${htmlAttribute(publicIdValue)}"></iframe>
<script>
(() => {
  const iframe = document.currentScript?.previousElementSibling;
  if (!(iframe instanceof HTMLIFrameElement)) return;

  const applicationOrigin = ${origin};
  const publicId = ${publicId};
  const maximumHeight = ${MAXIMUM_EMBEDDED_FORM_HEIGHT};

  window.addEventListener('message', (event) => {
    // 検査規則の正本は回答画面側の receivedEmbeddedFormHeight。
    if (event.origin !== applicationOrigin || event.source !== iframe.contentWindow) return;

    const message = event.data;
    if (
      message === null ||
      typeof message !== 'object' ||
      Array.isArray(message) ||
      message.type !== 'questionnaire:height' ||
      message.publicId !== publicId ||
      !Number.isSafeInteger(message.height) ||
      message.height < 0 ||
      message.height > maximumHeight
    ) return;

    iframe.style.height = message.height + 'px';
  });
})();
<\/script>`;
}
