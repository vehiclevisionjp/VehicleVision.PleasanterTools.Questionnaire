using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>アクセス解析の設定を回答画面へ渡す入口（Issue #162）。</summary>
/// <remarks>
/// <para>
/// **アンケートごとには出し分けない。** 出し分けると、応答の違いから
/// **公開 ID の実在が分かる**（<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
/// アプリ全体で 1 つの設定なので、公開 ID を受け取らない口にしてある。
/// </para>
/// <para>
/// **認証は要らない。** 返すのは運用側が置いたタグの ID と配信元だけで、
/// どれも**ページを開けば分かる**もの。
/// </para>
/// <para>
/// **無効なら空の応答を返す。** 「設定していない」ことも素直に伝える
/// （画面はタグを差し込まない）。
/// </para>
/// </remarks>
public static class AnalyticsEndpoints
{
    /// <summary>入口を生やす。</summary>
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/api/analytics", (AnalyticsOptions options) =>
            Results.Ok(new
            {
                // **無効なときも同じ形で返す。** 画面側で分岐を増やさない
                enabled = options.IsEnabled,
                provider = options.IsEnabled ? options.Provider.ToString() : AnalyticsProvider.None.ToString(),
                siteId = options.IsEnabled ? options.SiteId : null,
                origin = options.IsEnabled ? options.ResolvedOrigin() : null,
                showNotice = options.IsEnabled && options.ShowNotice,
            }));

        return builder;
    }
}
