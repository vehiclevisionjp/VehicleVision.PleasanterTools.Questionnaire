using System.Security.Cryptography;
using System.Text;

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
}
