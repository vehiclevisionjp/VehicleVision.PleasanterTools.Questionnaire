using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>外部の CAPTCHA の判定。</summary>
public enum CaptchaOutcome
{
    /// <summary>通った。</summary>
    Succeeded,

    /// <summary>解答が付いていない。</summary>
    Missing,

    /// <summary>解答が通らなかった（使い回し・期限切れ・偽造）。</summary>
    Invalid,

    /// <summary>
    /// **検証先へ到達できなかった。**
    /// </summary>
    /// <remarks>
    /// ⚠️ **これは「通った」ではない。** 通してしまうと、
    /// 外部が落ちている間だけ bot が素通りする。添付のウイルス検査と同じ扱い。
    /// </remarks>
    Unavailable,
}

/// <summary>外部の CAPTCHA の解答を確かめる（Issue #164）。</summary>
/// <remarks>
/// <para>
/// **3 つのサービスは、確かめ方がほぼ同じ。**
/// <c>secret</c> と <c>response</c>（と送信元 IP）を form で POST し、
/// <c>success</c> が返る。だから 1 つの実装で足りる。
/// </para>
/// <para>
/// ⚠️ **画面から「通った」と言われただけでは通さない。**
/// 解答はサービス側でしか検証できない。
/// </para>
/// </remarks>
public sealed class CaptchaVerifier(
    CaptchaOptions options,
    IHttpClientFactory factory,
    ILogger<CaptchaVerifier> logger)
{
    /// <summary>HTTP クライアントの名前。**タイムアウトを別に持たせる。**</summary>
    public const string HttpClientName = "captcha";

    /// <summary>解答を確かめる。</summary>
    /// <param name="response">画面が受け取った解答。</param>
    /// <param name="remoteIp">
    /// 送信元 IP。**サービスへ渡すのは任意。** 渡すと判定の精度が上がる代わりに、
    /// **回答者の IP が第三者へ渡る。** 外部の CAPTCHA を選んだ導入先はそれを承知している前提。
    /// </param>
    public async Task<CaptchaOutcome> VerifyAsync(
        string? response,
        string? remoteIp,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsExternalReady || options.VerifyUrl is not { } verifyUrl)
        {
            // **外部を使わない設定。** 呼ぶ側の判断ミスなので、通さない
            return CaptchaOutcome.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return CaptchaOutcome.Missing;
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("secret", options.SecretKey!),
            new("response", response),
        };

        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            fields.Add(new KeyValuePair<string, string>("remoteip", remoteIp));
        }

        try
        {
            using var client = factory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(fields);
            using var result = await client.PostAsync(verifyUrl, content, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "CAPTCHA の検証が {Status} を返した（Provider={Provider}）",
                    (int)result.StatusCode,
                    options.Provider);
                return CaptchaOutcome.Unavailable;
            }

            var body = await result.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var verification = JsonSerializer.Deserialize<CaptchaVerification>(body);

            if (verification is null)
            {
                return CaptchaOutcome.Unavailable;
            }

            if (verification.Success)
            {
                return CaptchaOutcome.Succeeded;
            }

            // **理由は記録するが、回答者へは返さない。**
            // 「なぜ落ちたか」を返すと、通し方を探る手掛かりになる
            logger.LogInformation(
                "CAPTCHA の解答が通らなかった（Provider={Provider}, Codes={Codes}）",
                options.Provider,
                verification.ErrorCodes is null ? "(なし)" : string.Join(',', verification.ErrorCodes));

            return CaptchaOutcome.Invalid;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // ⚠️ **到達できないときは通さない**（fail closed）。
            // 通すと、外部が落ちている間だけ素通りになる
            logger.LogWarning(exception, "CAPTCHA の検証先へ到達できなかった（Provider={Provider}）", options.Provider);
            return CaptchaOutcome.Unavailable;
        }
    }

    /// <summary>検証先の応答。**見るのは <c>success</c> と理由だけ。**</summary>
    private sealed record CaptchaVerification
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; init; }
    }
}
