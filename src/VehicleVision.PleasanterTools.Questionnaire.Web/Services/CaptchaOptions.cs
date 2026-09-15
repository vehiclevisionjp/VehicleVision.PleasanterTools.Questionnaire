using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答の送信に課す課題（Issue #164）。</summary>
/// <remarks>
/// **選ぶのは 1 つ。** 外部を選んだら、その課題を ALTCHA の代わりに課す。
/// </remarks>
public enum CaptchaProvider
{
    /// <summary>自前設置の proof-of-work（既定）。**外部へ出ない。**</summary>
    Altcha,

    /// <summary>Google reCAPTCHA v2。</summary>
    Recaptcha,

    /// <summary>Cloudflare Turnstile。</summary>
    Turnstile,

    /// <summary>hCaptcha。</summary>
    Hcaptcha,
}

/// <summary>外部の CAPTCHA の設定。</summary>
/// <remarks>
/// <para>
/// **既定は <see cref="CaptchaProvider.Altcha"/>。** 何も設定しなければ外部通信は出ない
/// （インターネットへ出られないイントラでも動く。<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// <para>
/// ⚠️ **秘密鍵はブラウザへ渡さない。** 画面へ渡すのはサイトキーだけ。
/// </para>
/// </remarks>
public sealed record CaptchaOptions
{
    /// <summary>使う課題。</summary>
    public CaptchaProvider Provider { get; init; } = CaptchaProvider.Altcha;

    /// <summary>サイトキー。**画面へ渡す。**</summary>
    public string? SiteKey { get; init; }

    /// <summary>秘密鍵。**画面へ渡さない。** 検証のときサーバだけが使う。</summary>
    public string? SecretKey { get; init; }

    /// <summary>外部のサービスを使う設定か。</summary>
    public bool UsesExternalService => Provider is not CaptchaProvider.Altcha;

    /// <summary>設定が使える形になっているか。</summary>
    /// <remarks>
    /// **鍵が片方でも欠けていれば使えない。** 半端な状態で画面へ出さない。
    /// </remarks>
    public bool IsExternalReady =>
        UsesExternalService
        && !string.IsNullOrWhiteSpace(SiteKey)
        && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>スクリプトの配信元。</summary>
    public string? ScriptOrigin => Provider switch
    {
        CaptchaProvider.Recaptcha => "https://www.google.com",
        CaptchaProvider.Turnstile => "https://challenges.cloudflare.com",
        CaptchaProvider.Hcaptcha => "https://js.hcaptcha.com",
        _ => null,
    };

    /// <summary>読み込むスクリプトの URL。</summary>
    /// <remarks>
    /// **明示的に描く形（explicit render）で読み込む。**
    /// 自動で描かせると、差し込む場所と件数をこちら側で決められない。
    /// </remarks>
    public string? ScriptUrl => Provider switch
    {
        CaptchaProvider.Recaptcha => "https://www.google.com/recaptcha/api.js?render=explicit",
        CaptchaProvider.Turnstile =>
            "https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit",
        CaptchaProvider.Hcaptcha => "https://js.hcaptcha.com/1/api.js?render=explicit",
        _ => null,
    };

    /// <summary>解答を確かめる先。</summary>
    public string? VerifyUrl => Provider switch
    {
        CaptchaProvider.Recaptcha => "https://www.google.com/recaptcha/api/siteverify",
        CaptchaProvider.Turnstile => "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        CaptchaProvider.Hcaptcha => "https://api.hcaptcha.com/siteverify",
        _ => null,
    };

    /// <summary>CSP へ足す配信元。**外部を使う設定のときだけ返す。**</summary>
    /// <remarks>
    /// <para>
    /// **`script-src` と `frame-src` の両方に要る。** どのサービスも iframe で課題を出す。
    /// </para>
    /// <para>
    /// ⚠️ **`https:` のようには広げない。** 許すのは選んだサービスの配信元だけ。
    /// </para>
    /// </remarks>
    public ImmutableArray<string> CspSources
    {
        get
        {
            if (!IsExternalReady)
            {
                return [];
            }

            return Provider switch
            {
                // reCAPTCHA は www.google.com と www.gstatic.com（画像と部品）を使う
                CaptchaProvider.Recaptcha => ["https://www.google.com", "https://www.gstatic.com"],
                CaptchaProvider.Turnstile => ["https://challenges.cloudflare.com"],
                CaptchaProvider.Hcaptcha =>
                [
                    "https://js.hcaptcha.com",
                    "https://newassets.hcaptcha.com",
                    "https://api.hcaptcha.com",
                ],
                _ => [],
            };
        }
    }

    /// <summary>設定から読む。**知らない値は既定（自前設置）へ落とす。**</summary>
    /// <remarks>
    /// ⚠️ **ここで例外を投げない。** 落とす先が「外部を使わない」なので、
    /// 書き間違いで**弱くならない**（自前の課題は動いたまま）。
    /// </remarks>
    public static CaptchaOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = CaptchaProvider.Altcha;
        if (configuration["CaptchaProvider"] is { Length: > 0 } configured
            && (!Enum.TryParse(configured, ignoreCase: true, out provider) || !Enum.IsDefined(provider)))
        {
            provider = CaptchaProvider.Altcha;
        }

        return new CaptchaOptions
        {
            Provider = provider,
            SiteKey = configuration["CaptchaSiteKey"]?.Trim(),

            // **秘密鍵は設定ファイルへ書かせない想定。** 環境変数か Key Vault から
            SecretKey = configuration["CaptchaSecretKey"]?.Trim(),
        };
    }
}
