/**
 * 外部の CAPTCHA を出す（Issue #164）。
 *
 * **既定では使わない。** 運用側が選んだときだけ、そのサービスの部品を読み込む。
 *
 * **3 つのサービスは、明示的に描く形（explicit render）の API がほぼ同じ。**
 * だから 1 つの実装で足りる。
 *
 * | サービス | 大域の名前 |
 * | --- | --- |
 * | reCAPTCHA v2 | `grecaptcha` |
 * | Turnstile | `turnstile` |
 * | hCaptcha | `hcaptcha` |
 *
 * ⚠️ **解答を持っているだけでは通らない。** サーバが検証先へ問い合わせて確かめる。
 */

/** サーバが返す課題の指定。 */
export interface CaptchaChallenge {
  /** `Altcha`（自前設置）/ `Recaptcha` / `Turnstile` / `Hcaptcha`。 */
  provider: string;
  /** サイトキー。**外部を選んでいるときだけ入る。** */
  siteKey: string | null;
  /** 読み込むスクリプトの URL。 */
  scriptUrl: string | null;
}

/** 部品を描く相手。 */
interface CaptchaWidgetApi {
  render(container: HTMLElement, parameters: Record<string, unknown>): string;
  reset(widgetId?: string): void;
  getResponse?(widgetId?: string): string;
}

/** サービスごとの大域の名前。 */
const GLOBALS: Record<string, string> = {
  Recaptcha: 'grecaptcha',
  Turnstile: 'turnstile',
  Hcaptcha: 'hcaptcha',
};

/** 外部のサービスを使う指定か。 */
export function usesExternalCaptcha(challenge: CaptchaChallenge | null | undefined): boolean {
  return (
    challenge !== null &&
    challenge !== undefined &&
    challenge.provider in GLOBALS &&
    (challenge.siteKey ?? '') !== '' &&
    (challenge.scriptUrl ?? '') !== ''
  );
}

let scriptPromise: Promise<CaptchaWidgetApi | null> | null = null;

/**
 * 部品を読み込む。**2 度呼んでも 1 度しか読み込まない。**
 *
 * **失敗しても例外にしない。** 解答が付かないまま送信すればサーバが断るので、
 * 画面が壊れて何も送れなくなるより素直。
 */
export async function loadCaptcha(challenge: CaptchaChallenge): Promise<CaptchaWidgetApi | null> {
  if (!usesExternalCaptcha(challenge)) {
    return null;
  }

  scriptPromise ??= new Promise<CaptchaWidgetApi | null>((resolve) => {
    const globalName = GLOBALS[challenge.provider];
    if (globalName === undefined) {
      resolve(null);
      return;
    }

    // **配信元は http(s) の絶対 URL だけ通す。** サーバ側でも決め打ちにしているが、
    // 組み立てるのはここなので、もう一度確かめる
    let url: URL;
    try {
      url = new URL(challenge.scriptUrl!);
    } catch {
      resolve(null);
      return;
    }

    if (url.protocol !== 'https:') {
      resolve(null);
      return;
    }

    const script = document.createElement('script');
    script.async = true;
    script.defer = true;
    script.src = url.toString();
    script.onload = () => {
      const api = (window as unknown as Record<string, CaptchaWidgetApi | undefined>)[globalName];
      resolve(api ?? null);
    };
    script.onerror = () => resolve(null);
    document.head.appendChild(script);
  });

  return scriptPromise;
}

/** 描いた部品 1 つ分。 */
export interface CaptchaWidget {
  /** いまの解答。**まだ解いていなければ空。** */
  response(): string;
  /** 解答を捨てて描き直す。**送信が断られた後に使う。** */
  reset(): void;
}

/**
 * 部品を描く。
 *
 * @param onSolved 解けたときに呼ばれる。**解答を受け取って送信に載せる。**
 */
export async function renderCaptcha(
  challenge: CaptchaChallenge,
  container: HTMLElement,
  onSolved: (response: string) => void,
): Promise<CaptchaWidget | null> {
  const api = await loadCaptcha(challenge);
  if (api === null) {
    return null;
  }

  let widgetId: string;
  try {
    widgetId = api.render(container, {
      sitekey: challenge.siteKey,
      callback: onSolved,

      // **期限切れと失敗のときは解答を捨てる。** 古い解答を送っても断られる
      'expired-callback': () => onSolved(''),
      'error-callback': () => onSolved(''),
    });
  } catch {
    return null;
  }

  return {
    response: () => api.getResponse?.(widgetId) ?? '',
    reset: () => {
      try {
        api.reset(widgetId);
      } catch {
        // **描き直せなくても送信の邪魔はしない**
      }
    },
  };
}
