using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>埋め込みを許す配信元の設定（Issue #104 / #107）。</summary>
/// <remarks>
/// <para>
/// **既定は空。** 何も設定しなければ一切埋め込めない（設定前と同じ振る舞い）。
/// **管理者が任意のホストを書けると、実質 <c>frame-src https:</c> と変わらない**ので、
/// 許すホストは運用側だけが決められる場所（設定）へ置く。
/// </para>
/// <para>
/// **書き方は CSP のホスト源に揃える。**
/// <c>www.example.net</c> はそのホストだけ、<c>*.example.net</c> は
/// その下のホストすべて（<c>example.net</c> 自体は含まない）。
/// </para>
/// <para>
/// 例: <c>QUESTIONNAIRE_EMBED_ALLOWEDHOSTS=www.youtube.com,*.example.net</c>
/// </para>
/// </remarks>
public sealed class EmbedOptions
{
    /// <summary>設定の鍵。</summary>
    public const string AllowedHostsKey = "QUESTIONNAIRE_EMBED_ALLOWEDHOSTS";

    /// <summary>埋め込みを許すホスト。**既定は空。**</summary>
    public ImmutableArray<string> AllowedHosts { get; init; } = [];

    /// <summary>埋め込みを使えるか。</summary>
    public bool Enabled => !AllowedHosts.IsDefaultOrEmpty;

    /// <summary>CSP のホスト源に直したもの。</summary>
    /// <remarks>**ヘッダを組み立てるたびに作り直さない。** 設定は動かない。</remarks>
    public ImmutableArray<string> CspSources { get; }

    public EmbedOptions() => CspSources = [];

    private EmbedOptions(ImmutableArray<string> allowedHosts)
    {
        AllowedHosts = allowedHosts;
        CspSources = EmbedPolicy.ToCspSources(allowedHosts);
    }

    public static EmbedOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[AllowedHostsKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new EmbedOptions();
        }

        var hosts = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableArray();

        return new EmbedOptions(hosts);
    }

    /// <summary>この URL を埋め込んでよいか。</summary>
    public bool IsAllowed(string? url) => EmbedPolicy.IsAllowed(url, AllowedHosts);
}
