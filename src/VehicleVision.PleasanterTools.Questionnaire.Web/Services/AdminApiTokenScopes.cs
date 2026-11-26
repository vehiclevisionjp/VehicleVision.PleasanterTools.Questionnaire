using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Net;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理 API の個人トークンに付けられるスコープと、実効の権限（Issue #1025）。</summary>
/// <remarks>
/// <para>
/// <b>新しい権限は増やさない。</b>既存の権限（<see cref="AdminPermissions"/>）のサブセットをスコープにする。
/// エクスポート（<c>surveys.read</c>）・インポート（<c>surveys.publish</c>。#1009・#1029）は、既存の権限を流用する
/// （2026-11-18 の決定）。スコープ名は、将来の OAuth の <c>scope</c> 値と同じ文字列。
/// </para>
/// <para>
/// <b>付けられるのは許可リストだけ。</b><c>users.*</c>・<c>settings.*</c>・<c>lookups.manage</c>・
/// <c>surveys.delete</c> などは付けられない（乗っ取りの足場になる・完全削除は画面操作に限る）。
/// トークンの発行・失効そのものにも付けられない（トークンでトークンを作らせない）。
/// </para>
/// <para>
/// <b>実効の権限 ＝ 持ち主の「いま」の役割の権限 ∩ トークンに付けたスコープ。</b>
/// 権限はトークンへ焼かない。役割を降格すれば、トークンもその場で弱くなる。
/// </para>
/// </remarks>
public static class AdminApiTokenScopes
{
    /// <summary>トークンに付けられるスコープ（許可リスト）。</summary>
    public static readonly ImmutableArray<string> Allowed =
    [
        AdminPermissions.SurveysRead,
        AdminPermissions.SurveysWrite,
        AdminPermissions.SurveysPublish,
    ];

    private static readonly FrozenSet<string> AllowedSet = Allowed.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>そのスコープを付けてよいか（許可リストにあるか）。</summary>
    public static bool IsAllowed(string? scope) => scope is not null && AllowedSet.Contains(scope);

    /// <summary>その役割が付けられるスコープ（許可リスト ∩ 役割の権限）。</summary>
    public static IReadOnlyList<string> IssuableFor(string? roleName)
    {
        var permissions = AdminPermissions.Of(roleName);
        return Allowed.Where(permissions.Contains).ToList();
    }

    /// <summary>その役割はトークンを発行できるか（付けられるスコープが 1 つでもあるか）。</summary>
    /// <remarks>
    /// <c>UserAdministrator</c>・<c>Auditor</c> は <c>surveys.*</c> を持たないので発行できない。
    /// <b>役割ごとの表を別に持たず、権限の対応表から導く</b>（役割を足したときに食い違わない）。
    /// </remarks>
    public static bool CanIssue(string? roleName) => IssuableFor(roleName).Count > 0;

    /// <summary>
    /// 実効の権限。<b>持ち主の役割の権限 ∩ トークンのスコープ ∩ 許可リスト</b>。
    /// </summary>
    /// <remarks>
    /// 許可リストでも絞る（DB の行が書き換えられて許可外のスコープが入っていても、権限にならない）。
    /// </remarks>
    public static IReadOnlySet<string> EffectivePermissions(string? ownerRoleName, IEnumerable<string> tokenScopes)
    {
        ArgumentNullException.ThrowIfNull(tokenScopes);

        var rolePermissions = AdminPermissions.Of(ownerRoleName);
        return tokenScopes
            .Where(scope => IsAllowed(scope) && rolePermissions.Contains(scope))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// 接続元の許可リスト（CIDR）を読む。<b>1 つでも解釈できなければ <c>null</c></b>（黙って捨てると、制限したつもりで開く）。
    /// 区切りは<b>カンマ・空白・改行</b>。単独の IP アドレスは <c>/32</c>（IPv6 は <c>/128</c>）として扱う。
    /// </summary>
    /// <returns>正規化した CIDR（重複を除く）。解釈できないものがあるとき、または多すぎるときは <c>null</c>。</returns>
    public static IReadOnlyList<string>? NormalizeIpAllowList(IEnumerable<string>? entries)
    {
        var result = new List<string>();
        foreach (var raw in entries ?? [])
        {
            foreach (var part in (raw ?? string.Empty).Split(
                [',', ' ', '\t', '\r', '\n', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseNetwork(part) is not { } network)
                {
                    return null;
                }

                var normalized = network.ToString();
                if (!result.Contains(normalized, StringComparer.Ordinal))
                {
                    result.Add(normalized);
                }
            }
        }

        return result.Count > AdminApiTokenOptions.MaxIpAllowListEntries ? null : result;
    }

    /// <summary>接続元が許可リストに入っているか。<b>リストが空なら制限しない</b>（<c>true</c>）。接続元が不明なら <c>false</c>。</summary>
    public static bool IsIpAllowed(IReadOnlyList<string> allowList, IPAddress? remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(allowList);
        if (allowList.Count == 0)
        {
            return true;
        }

        if (remoteAddress is null)
        {
            return false;
        }

        var address = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        foreach (var entry in allowList)
        {
            if (IPNetwork.TryParse(entry, out var network) && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private static IPNetwork? TryParseNetwork(string value)
    {
        if (!value.Contains('/', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(value, out var single))
            {
                return null;
            }

            value = $"{value}/{(single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}";
        }

        return IPNetwork.TryParse(value, out var network) ? network : null;
    }
}
