using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答後の配布資産に使う引換券（Issue #318）。</summary>
public static class AssetTicket
{
    private const int TokenBytes = 32;

    public const string FragmentKey = "d";

    public const string CookieName = "q.asset";

    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string HashOf(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    public static string UrlOf(string baseUrl, string publicId, string token) =>
        $"{baseUrl.TrimEnd('/')}/f/{Uri.EscapeDataString(publicId)}"
        + $"#{FragmentKey}={Uri.EscapeDataString(token)}";

    /// <summary>Cookie が資産へのアクセスを許可するか調べる。</summary>
    public static async Task<bool> CanAccessAsync(
        string? cookie,
        string publicId,
        Guid surveyId,
        SubmissionGuard guard,
        IAssetTicketStore tickets,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        // 署名付きの値は DB 引換券と書式で区別し、期限を署名から検証する。
        // 壊れた署名を DB 側へ回すと、方式 A なのに表を引く経路ができてしまう。
        if (cookie.StartsWith(SubmissionGuard.AssetAccessVersion + '.', StringComparison.Ordinal))
        {
            return guard.CheckAssetAccess(cookie, publicId);
        }

        return await tickets
            .RedeemAsync(HashOf(cookie), surveyId, nowUtc, cancellationToken)
            .ConfigureAwait(false) is not null;
    }
}
