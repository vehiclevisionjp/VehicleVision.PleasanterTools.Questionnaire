/**
 * アクセス解析のタグを差し込む（Issue #162）。
 *
 * **既定では何もしない。** 運用側が設定したときだけ、そのサービスのスクリプトを読み込む。
 *
 * ⚠️ **inline script は使わない。** タグは同梱したこの JS から DOM へ差し込む。
 * `unsafe-inline` を CSP へ足さずに済ませるため。
 *
 * ⚠️ **サーバから受け取るのは ID と配信元だけ。** 任意のスクリプトは受け取らない
 * （受け取れる作りにすると、そこが XSS の入口になる）。
 */

/** サーバが返す設定。 */
export interface AnalyticsSettings {
  enabled: boolean;
  provider: 'None' | 'Ga4' | 'Gtm' | 'Matomo' | 'Plausible';
  siteId: string | null;
  origin: string | null;
  showNotice: boolean;
}

/** 既定（無効）。**取得に失敗したときもこれを使う。** */
export const DISABLED_ANALYTICS: AnalyticsSettings = {
  enabled: false,
  provider: 'None',
  siteId: null,
  origin: null,
  showNotice: false,
};

declare global {
  interface Window {
    dataLayer?: unknown[];
    _paq?: unknown[];
  }
}

/**
 * 設定を読む。
 *
 * **失敗しても回答画面は動かす。** 解析は飾りであって、回答の入口ではない。
 */
export async function fetchAnalyticsSettings(): Promise<AnalyticsSettings> {
  try {
    const response = await fetch('/api/analytics', { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      return DISABLED_ANALYTICS;
    }

    const settings = (await response.json()) as AnalyticsSettings;
    return settings.enabled ? settings : DISABLED_ANALYTICS;
  } catch {
    return DISABLED_ANALYTICS;
  }
}

/** 読み込みが二重に走らないようにする印。 */
let installed = false;

/**
 * タグを差し込む。**同じ画面で 2 度呼んでも 1 度しか差し込まない。**
 *
 * @returns 差し込んだかどうか。
 */
export function installAnalytics(settings: AnalyticsSettings): boolean {
  if (installed || !settings.enabled || settings.siteId === null || settings.origin === null) {
    return false;
  }

  // **配信元は http(s) の絶対 URL だけ通す。** サーバ側でも見ているが、
  // ここで組み立てるので、もう一度確かめる
  let origin: URL;
  try {
    origin = new URL(settings.origin);
  } catch {
    return false;
  }

  if (origin.protocol !== 'https:' && origin.protocol !== 'http:') {
    return false;
  }

  switch (settings.provider) {
    case 'Ga4':
      appendScript(`${origin.origin}/gtag/js?id=${encodeURIComponent(settings.siteId)}`);
      window.dataLayer = window.dataLayer ?? [];
      // **gtag は「引数をそのまま dataLayer へ積む」だけの関数。**
      // 公式の inline snippet と同じことを、同梱した JS の中で行う
      push('js', new Date());
      push('config', settings.siteId);
      break;

    case 'Gtm':
      window.dataLayer = window.dataLayer ?? [];
      window.dataLayer.push({ 'gtm.start': Date.now(), event: 'gtm.js' });
      appendScript(`${origin.origin}/gtm.js?id=${encodeURIComponent(settings.siteId)}`);
      break;

    case 'Matomo':
      window._paq = window._paq ?? [];
      window._paq.push(['trackPageView']);
      window._paq.push(['enableLinkTracking']);
      window._paq.push(['setTrackerUrl', `${origin.origin}/matomo.php`]);
      window._paq.push(['setSiteId', settings.siteId]);
      appendScript(`${origin.origin}/matomo.js`);
      break;

    case 'Plausible': {
      const script = appendScript(`${origin.origin}/js/script.js`);
      // **計測するドメインは data 属性で渡す**（Plausible の作り）
      script.dataset.domain = settings.siteId;
      break;
    }

    default:
      return false;
  }

  installed = true;
  return true;
}

/** `dataLayer` へ積む（gtag と同じ動き）。 */
function push(...args: unknown[]): void {
  // **arguments をそのまま積むのが gtag の仕様。** 配列にしてはいけない
  window.dataLayer?.push(args);
}

/** スクリプトを 1 本足す。 */
function appendScript(source: string): HTMLScriptElement {
  const script = document.createElement('script');
  script.async = true;
  script.src = source;
  document.head.appendChild(script);
  return script;
}
