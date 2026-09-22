using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答画面の埋め込みを許す親サイトの設定（Issue #334）。</summary>
/// <remarks>
/// <para>
/// **既定は空。** 何も設定しなければ <c>frame-ancestors 'none'</c> のままにする。
/// 許可先はアンケート管理者ではなく、運用側だけが設定する。
/// </para>
/// <para>
/// 書き方は <see cref="EmbedOptions"/> と同じ。
/// <c>www.example.net</c> はそのホストだけ、<c>*.example.net</c> は
/// その下のホストすべて（<c>example.net</c> 自体は含まない）。
/// </para>
/// </remarks>
public sealed class EmbedParentOptions
{
    /// <summary>設定の鍵。</summary>
    public const string AllowedParentsKey = "QUESTIONNAIRE_EMBED_ALLOWEDPARENTS";

    /// <summary>回答画面の埋め込みを許す親ホスト。**既定は空。**</summary>
    public ImmutableArray<string> AllowedParents { get; init; } = [];

    /// <summary>回答画面を埋め込める構成か。</summary>
    public bool Enabled => !CspSources.IsDefaultOrEmpty;

    /// <summary>CSP のホスト源に直したもの。</summary>
    public ImmutableArray<string> CspSources { get; }

    public EmbedParentOptions() => CspSources = [];

    private EmbedParentOptions(ImmutableArray<string> allowedParents)
    {
        AllowedParents = allowedParents;
        CspSources = EmbedPolicy.ToCspSources(allowedParents);
    }

    public static EmbedParentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[AllowedParentsKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new EmbedParentOptions();
        }

        var parents = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableArray();

        return new EmbedParentOptions(parents);
    }
}
