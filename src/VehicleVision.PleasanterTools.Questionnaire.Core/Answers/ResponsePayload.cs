using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

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

    /// <summary>添付の中身を落とした写しを返す。</summary>
    /// <remarks>
    /// **Pleasanter の回答 JSON 列へ Base64 を載せない。** 列が添付本文で埋まるうえ、
    /// 応答不明時の照合はこの列を部分一致で引くため、巨大な文字列は検索の邪魔になる
    /// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
    /// **ファイル名は残す。** 何が添えられていたかは正本に要る。
    /// </remarks>
    public ResponsePayload WithoutFileContent() => this with
    {
        Answers =
        [
            .. Answers.Select(answer => answer with
            {
                Files = answer.Files.IsDefaultOrEmpty
                    ? answer.Files
                    : [.. answer.Files.Select(file => file with { Base64 = null })],
            }),
        ],
    };

    public static ResponsePayload Create(
        string token,
        IEnumerable<Answer> answers,
        IReadOnlyList<AnsweredAttachment>? attachments = null)
    {
        var filesByQuestion = (attachments ?? [])
            .GroupBy(attachment => attachment.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(attachment => new PayloadFile(
                        attachment.File.FileName,
                        Convert.ToBase64String(attachment.File.Content.Span)))
                    .ToImmutableArray(),
                StringComparer.Ordinal);

        return new ResponsePayload(token,
        [
            .. answers.Select(answer => new PayloadAnswer(
                answer.QuestionId,
                answer.Values.IsDefault ? [] : answer.Values,
                answer.OtherText,
                answer.FileNames.IsDefault ? [] : answer.FileNames)
            {
                Files = filesByQuestion.TryGetValue(answer.QuestionId, out var files)
                    ? files
                    : [],
            }),
        ]);
    }
}

/// <summary>回答 1 件（保存用）。</summary>
public sealed record PayloadAnswer(
    [property: JsonPropertyName("questionId")] string QuestionId,
    [property: JsonPropertyName("values")] ImmutableArray<string> Values,
    [property: JsonPropertyName("otherText")] string? OtherText = null,
    [property: JsonPropertyName("fileNames")] ImmutableArray<string> FileNames = default)
{
    /// <summary>添付ファイルの中身。</summary>
    /// <remarks>
    /// **送信待ちの行にだけ載せる。** Pleasanter へ渡す正本 JSON からは
    /// <see cref="ResponsePayload.WithoutFileContent"/> で落とす。
    /// </remarks>
    [JsonPropertyName("files")]
    public ImmutableArray<PayloadFile> Files { get; init; } = [];
}

/// <summary>添付ファイル 1 件（保存用）。</summary>
/// <param name="Name">ファイル名。**入口で検査済みのものだけが入る。**</param>
/// <param name="Base64">
/// 中身。正本 JSON では <c>null</c> になる（<see cref="ResponsePayload.WithoutFileContent"/>）。
/// </param>
public sealed record PayloadFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("base64")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64);
