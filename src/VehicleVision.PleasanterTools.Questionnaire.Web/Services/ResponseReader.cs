using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答を読み出した結果の種類。</summary>
public enum ResponseReadStatus
{
    /// <summary>読めた。</summary>
    Found,

    /// <summary>読める回答が無い。**他人の回答・存在しない回答・編集できない設定も、ここへ丸める。**</summary>
    NotFound,

    /// <summary>今は読めない（Pleasanter の停止・遅延・権限・設定の誤り）。後で読めるかもしれない。</summary>
    Unavailable,
}

/// <summary>読み出しの結果。</summary>
/// <param name="Status">結果の種類。</param>
/// <param name="Answers">回答。<see cref="ResponseReadStatus.Found"/> のときだけ入る。</param>
public sealed record ResponseReadResult(
    ResponseReadStatus Status,
    ImmutableArray<PayloadAnswer> Answers = default)
{
    public static ResponseReadResult NotFound { get; } = new(ResponseReadStatus.NotFound);

    public static ResponseReadResult Unavailable { get; } = new(ResponseReadStatus.Unavailable);

    public static ResponseReadResult Found(ImmutableArray<PayloadAnswer> answers) =>
        new(ResponseReadStatus.Found, answers);
}

/// <summary>自分の回答を読み直す（Issue #908）。</summary>
/// <remarks>
/// <para>
/// <b>読み出しは 2 段構え</b>（<c>_documents/アーキテクチャ方針.md</c> 10 章）。
/// <list type="number">
/// <item>送信待ちに同じトークンがあれば、それを返す（未送信の最新状態）。</item>
/// <item>無ければ、<c>ResponseTokens</c> の <c>ReferenceId</c> で Pleasanter から <c>Get</c> し、
/// 回答 JSON 列を設問へ戻して返す（送信済み）。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>以下は設計書に決まりが無く、推奨どおりに実装した判断であって、確定した仕様ではない。</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>持ち主の照合。</b> トークンの行の <c>SurveyId</c> が、公開 ID のアンケートと一致するときだけ読む。
/// さらに Pleasanter から戻った回答 JSON が<b>同じトークンを含む</b>ことを確かめる
/// （レコードの取り違え・差し替えを返さない）。<b>DB に無いトークンでは Pleasanter を叩かない</b>
/// （当て推量のトークンが Pleasanter への負荷にならない）。
/// 回答トークンに期限は無い（再編集トークンの期限は引き換えの時点で見ている）。
/// </item>
/// <item>
/// <b>版。</b> 回答 JSON は設問 ID の組なので、公開中の版の設問へ戻る。送信した版の記録は持たないため、
/// 公開版が変わったあとは、設問 ID が一致するものだけが戻る（画面が知らない設問の回答は無視される）。
/// 読む列と書き込み先のサイトは、公開中の版の割り当てに従う。
/// </item>
/// <item>
/// <b>回答 JSON 列が無い・編集を許していない</b>なら、Pleasanter を叩かず「無い」として返す
/// （<c>_documents/アーキテクチャ方針.md</c> 9 章。列が無ければ設問へ戻せない）。
/// </item>
/// <item>
/// <b>添付。</b> ファイル名だけを返し、中身は返さない（Pleasanter の列にも Base64 は載せていない）。
/// 添付は送信のたびに選び直す仕様のまま。
/// </item>
/// <item>
/// <b>失敗の見せ方。</b> レコードが無い・消された・持ち主が違う・読めない形のときは「無い」（404）、
/// Pleasanter の停止・遅延・認証・権限・設定の誤りは「今は読めない」（503）。
/// <b>どちらも本文を持たない</b>ので、内部の事情（サイト ID・列名・エラー文）は回答者へ出ない。
/// </item>
/// </list>
/// </remarks>
public sealed class ResponseReader(
    ISurveyRepository surveys,
    ISurveySnapshotStore snapshots,
    IResponseTokenStore tokens,
    ResponseIntake intake,
    PleasanterGateway pleasanter,
    ILogger<ResponseReader> logger)
{
    /// <summary>回答を読む。</summary>
    public async Task<ResponseReadResult> ReadAsync(
        string publicId,
        string responseToken,
        CancellationToken cancellationToken = default)
    {
        var survey = await surveys.FindByPublicIdAsync(publicId, cancellationToken)
            .ConfigureAwait(false);
        if (survey is null || survey.PublishedVersion is not { } version || survey.ArchivedAt is not null)
        {
            return ResponseReadResult.NotFound;
        }

        var entry = await tokens.FindAsync(responseToken, cancellationToken).ConfigureAwait(false);

        // **他のアンケートのトークンでは読ませない。**
        if (entry is not null && entry.SurveyId != survey.SurveyId)
        {
            return ResponseReadResult.NotFound;
        }

        // 1 段目: 送信待ち。**未送信の回答は Pleasanter にまだ無い**
        var pending = await intake.FindPendingAsync(responseToken, cancellationToken)
            .ConfigureAwait(false);
        if (pending is not null)
        {
            return ResponseReadResult.Found(pending.WithoutFileContent().Answers);
        }

        // 2 段目: Pleasanter（送信済み）
        if (entry?.ReferenceId is not { } referenceId)
        {
            return ResponseReadResult.NotFound;
        }

        var snapshot = await snapshots.FindAsync(survey.SurveyId, version, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null
            || !snapshot.Definition.AllowEditingAfterSubmit
            || string.IsNullOrEmpty(snapshot.ResponseJsonColumn))
        {
            return ResponseReadResult.NotFound;
        }

        PleasanterResponse response;
        try
        {
            response = await pleasanter.GetRecordAsync(referenceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PleasanterConfigurationException)
        {
            logger.LogError("送信済みの回答を読めない: Pleasanter への接続先（BaseUrl）の設定が使えない");
            return ResponseReadResult.Unavailable;
        }

        if (!response.IsSuccess)
        {
            // **記録するのは分類とステータスだけ。** 本文・URL・資格情報は載せない
            if (response.ErrorKind == PleasanterErrorKind.Permanent && response.DisplayStatusCode() == 404)
            {
                // レコードが消された
                return ResponseReadResult.NotFound;
            }

            logger.LogWarning(
                "送信済みの回答を読めない: Pleasanter が失敗を返した（分類={Kind}, ステータス={Status}）",
                response.ErrorKind,
                response.DisplayStatusCode());
            return ResponseReadResult.Unavailable;
        }

        var row = response.Body?["Response"]?["Data"]?.AsArray()?.FirstOrDefault();
        if (row is null)
        {
            // 200 でも 1 件も返らない（消された・権限で見えない）
            return ResponseReadResult.NotFound;
        }

        // **別のサイトのレコードは読まない。** 返ってきた場合だけ照合する
        if (row["SiteId"]?.TryGetValueAsLong() is { } siteId && siteId != snapshot.PleasanterSiteId)
        {
            return ResponseReadResult.NotFound;
        }

        var json = ReadColumn(row, snapshot.ResponseJsonColumn);
        var payload = json is null ? null : ResponsePayload.FromJson(json);

        // **同じトークンを含むときだけ返す。** 取り違えたレコードを他人へ見せない
        return payload is not null && string.Equals(payload.Token, responseToken, StringComparison.Ordinal)
            ? ResponseReadResult.Found(payload.WithoutFileContent().Answers)
            : ResponseReadResult.NotFound;
    }

    /// <summary>行から回答 JSON 列の値を取り出す。</summary>
    private static string? ReadColumn(JsonNode row, string column)
    {
        var kind = PleasanterColumn.KindOf(column);
        if (kind is not null && row[$"{kind}Hash"] is JsonObject hash
            && hash[column] is JsonValue value
            && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }
}

internal static class JsonNodeExtensions
{
    /// <summary>数値でも文字列でも整数として読む。読めなければ <c>null</c>。</summary>
    public static long? TryGetValueAsLong(this JsonNode node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)
            ? parsed
            : null;
    }
}
