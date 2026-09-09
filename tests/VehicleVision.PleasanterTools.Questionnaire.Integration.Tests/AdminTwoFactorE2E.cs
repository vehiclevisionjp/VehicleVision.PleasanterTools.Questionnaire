using System.Net.Http.Json;
using System.Text.Json.Nodes;
using OtpNet;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 端から端まで通す試験で、2 要素の登録まで済ませる（Issue #174）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ **2 要素をどう扱うかは運用側の設定で変わる**（<c>QUESTIONNAIRE_ADMIN_TWOFACTOR</c>。
/// Issue #154）。初期設定や招待の受け取りの応答に入る <c>next</c> が、
/// **設定によって <c>enroll</c>（必須）と <c>done</c>（任意・無効）に分かれる。**
/// </para>
/// <para>
/// **試験の側で設定を決め打ちにしない。** 検証環境は写しを撮るために
/// <c>required</c> にしてあるが（Issue #172）、切り替えたときに
/// **試験が落ちるのは「アプリが壊れた」ではなく「試験が思い込んでいた」だけ**になる。
/// 実際に #171 と #173 で踏んだ。
/// </para>
/// <para>
/// **通る道は 2 つある。**
/// </para>
/// <list type="bullet">
///   <item>
///     <c>enroll</c> … 途中状態。<c>/api/admin/enroll/begin</c> →
///     <c>/api/admin/enroll/complete</c> で登録して初めてログインになる
///   </item>
///   <item>
///     <c>done</c> … 既にログイン済み。**登録は任意**なので、
///     必要なら <c>/api/admin/me/totp/begin</c>（パスワードの再確認つき）→
///     <c>/api/admin/me/totp/complete</c> で登録する
///   </item>
/// </list>
/// </remarks>
internal static class AdminTwoFactorE2E
{
    /// <summary>2 要素の登録まで済ませた結果。</summary>
    /// <param name="Secret">共有鍵。**登録しなかったときは空。**</param>
    /// <param name="RecoveryCodes">復旧コード。**登録しなかったときは空。**</param>
    internal sealed record Enrollment(string Secret, string[] RecoveryCodes)
    {
        internal static Enrollment None { get; } = new(string.Empty, []);
    }

    /// <summary>今の時間枠の使い捨てパスワード。</summary>
    internal static string Code(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    /// <summary>応答から <c>next</c> を読む。</summary>
    internal static async Task<string> NextOfAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return body?["next"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>
    /// <paramref name="next"/> が <c>enroll</c> のときだけ登録する。
    /// **任意・無効なら何もしない**（既にログイン済み）。
    /// </summary>
    internal static Task<Enrollment> CompleteIfRequiredAsync(HttpClient http, string next) =>
        next == "enroll" ? EnrollFromPendingAsync(http) : Task.FromResult(Enrollment.None);

    /// <summary>
    /// 設定にかかわらず 2 要素を登録する。**2 要素そのものを見る試験で使う。**
    /// </summary>
    internal static Task<Enrollment> EnrollAnywayAsync(HttpClient http, string next, string password) =>
        next == "enroll"
            ? EnrollFromPendingAsync(http)
            : EnrollFromSessionAsync(http, password);

    /// <summary>途中状態から登録する（必須のとき）。</summary>
    private static async Task<Enrollment> EnrollFromPendingAsync(HttpClient http)
    {
        string secret;
        using (var begin = await http.PostAsJsonAsync("/api/admin/enroll/begin", new { })
            .ConfigureAwait(false))
        {
            begin.EnsureSuccessStatusCode();
            secret = await SecretOfAsync(begin).ConfigureAwait(false);
        }

        using var complete = await http
            .PostAsJsonAsync("/api/admin/enroll/complete", new { code = Code(secret) })
            .ConfigureAwait(false);
        complete.EnsureSuccessStatusCode();

        return new Enrollment(secret, await RecoveryCodesOfAsync(complete).ConfigureAwait(false));
    }

    /// <summary>ログイン済みの状態から登録する（任意・無効のとき）。</summary>
    /// <remarks>**パスワードをもう一度求められる。** 離席で奪われた画面から差し替えられないため。</remarks>
    private static async Task<Enrollment> EnrollFromSessionAsync(HttpClient http, string password)
    {
        string secret;
        using (var begin = await http
            .PostAsJsonAsync("/api/admin/me/totp/begin", new { password })
            .ConfigureAwait(false))
        {
            begin.EnsureSuccessStatusCode();
            secret = await SecretOfAsync(begin).ConfigureAwait(false);
        }

        using var complete = await http
            .PostAsJsonAsync("/api/admin/me/totp/complete", new { code = Code(secret) })
            .ConfigureAwait(false);
        complete.EnsureSuccessStatusCode();

        return new Enrollment(secret, await RecoveryCodesOfAsync(complete).ConfigureAwait(false));
    }

    private static async Task<string> SecretOfAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return body!["secret"]!.GetValue<string>();
    }

    private static async Task<string[]> RecoveryCodesOfAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return body?["recoveryCodes"]?.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray() ?? [];
    }
}
