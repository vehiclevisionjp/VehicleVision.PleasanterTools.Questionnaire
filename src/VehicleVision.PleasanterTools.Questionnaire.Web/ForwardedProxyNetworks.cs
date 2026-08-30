using System.Net;

namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>信頼するリバースプロキシのネットワーク設定。</summary>
public static class ForwardedProxyNetworks
{
    /// <summary>カンマ区切りの CIDR。</summary>
    public const string Setting = "QUESTIONNAIRE_FORWARDED_NETWORKS";

    /// <summary>設定値を CIDR の一覧にする。</summary>
    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseOne)
            .ToArray();
    }

    private static IPNetwork ParseOne(string value) =>
        IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"{Setting} に CIDR でない値がある: {value}");
}
