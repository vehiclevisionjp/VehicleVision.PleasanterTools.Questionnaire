using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

/// <summary>送信待ちテーブルに入れる回答の中身。</summary>
/// <remarks>
/// <para>
/// **これがそのまま「回答の正本」として Pleasanter の JSON 列へも入る**
/// （<c>_documents/アーキテクチャ方針.md</c> 8 章）。
/// </para>
/// <para>
/// **<see cref="Token"/> を必ず含める。** 応答が返らなかった <c>Create</c> を
/// 部分一致検索で照合するのに使う（同 9 章）。
/// </para>
/// </remarks>
/// <param name="Token">回答トークン。</param>
/// <param name="Answers">回答。</param>
public sealed record ResponsePayload(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("answers")] ImmutableArray<PayloadAnswer> Answers)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // **回答本文に絵文字や記号が入る。** 読めない形にしない
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ResponsePayload? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ResponsePayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><see cref="Answer"/> の一覧へ戻す。</summary>
    public IReadOnlyCollection<Answer> ToAnswers() =>
    [
        .. Answers.Select(answer => new Answer(answer.QuestionId, answer.Values)
        {
            OtherText = answer.OtherText,
            FileNames = answer.FileNames,
        }),
    ];

    public static ResponsePayload Create(string token, IEnumerable<Answer> answers) =>
        new(token,
        [
            .. answers.Select(answer => new PayloadAnswer(
                answer.QuestionId,
                answer.Values.IsDefault ? [] : answer.Values,
                answer.OtherText,
                answer.FileNames.IsDefault ? [] : answer.FileNames)),
        ]);
}

/// <summary>回答 1 件（保存用）。</summary>
public sealed record PayloadAnswer(
    [property: JsonPropertyName("questionId")] string QuestionId,
    [property: JsonPropertyName("values")] ImmutableArray<string> Values,
    [property: JsonPropertyName("otherText")] string? OtherText = null,
    [property: JsonPropertyName("fileNames")] ImmutableArray<string> FileNames = default);
