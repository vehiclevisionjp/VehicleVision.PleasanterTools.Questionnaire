using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

/// <summary>要求 1 件をどの言語で返すか。</summary>
/// <remarks>
/// <para>
/// **<c>Accept-Language</c> だけを見る**（<c>_documents/多言語対応方針.md</c> 2 章）。
/// 管理画面は自分が描いている言語をこのヘッダに明示して送るので、
/// **画面の文言とサーバから返る文言が食い違わない。**
/// </para>
/// <para>
/// **利用者ごとの設定（<c>AdminUsers.Language</c>）はここで読まない。**
/// 読むと要求のたびに管理者を 1 行引くことになり、
/// しかも画面が実際に描いている言語と食い違い得る。
/// 設定は**画面の初期値を決めるためのもの**で、応答の言語を決めるものではない。
/// </para>
/// <para>
/// **回答画面はここを通らない。** 回答者へ返す応答は理由の符号しか持たず、
/// 文言はブラウザの中で組み立てる（完全匿名を崩さないため）。
/// </para>
/// </remarks>
public static class RequestLanguage
{
    /// <summary>この要求へ返す言語。決まらなければ既定の言語。</summary>
    public static string Of(HttpContext context) =>
        SupportedLanguages.Resolve(
            requested: null,
            acceptLanguageHeader: context.Request.Headers.AcceptLanguage.ToString());
}
