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
    /// <remarks>同じ設定スナップショットからの判定と CSP がずれないよう、生成時に確定する。</remarks>
    public ImmutableArray<string> CspSources { get; }

    public EmbedOptions() => CspSources = [];

    private EmbedOptions(ImmutableArray<string> allowedHosts)
    {
        AllowedHosts = allowedHosts;
        CspSources = EmbedPolicy.ToCspSources(allowedHosts);
    }

    public static EmbedOptions FromSnapshot(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new EmbedOptions(EmbedHostSettings.Parse(snapshot[AllowedHostsKey]));
    }

    /// <summary>この URL を埋め込んでよいか。</summary>
    public bool IsAllowed(string? url) => EmbedPolicy.IsAllowed(url, AllowedHosts);
}

/// <summary>埋め込み先のホスト設定を検証し、同じ規則で読み取る。</summary>
internal static class EmbedHostSettings
{
    public static string Normalize(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var hosts = value.Split(',', StringSplitOptions.TrimEntries);
        if (hosts.Any(host => host.Length == 0
            || EmbedPolicy.ToCspSources([host]).Length != 1))
        {
            throw new AppSettingValidationException(
                "埋め込み先は www.example.net または *.example.net の形式で、読点区切りで指定してください。");
        }

        return string.Join(", ", hosts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static ImmutableArray<string> Parse(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToImmutableArray();
}
