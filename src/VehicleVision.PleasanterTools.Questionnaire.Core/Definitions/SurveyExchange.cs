using System.Text;
using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>エクスポートした定義の入れ物（Issue #1009）。</summary>
/// <remarks>
/// **定義とマッピングだけを持つ。** 書き込み先のサイト・公開用 ID・公開状態・公開済みの版・
/// 受付期間・回答上限・資格情報は、**環境ごとに決め直すものなので入れない。**
/// </remarks>
public sealed record SurveyExchangeDocument
{
    /// <summary>この入れ物の種類。**他の JSON をインポートさせないための目印。**</summary>
    public string Format { get; init; } = SurveyExchange.FormatName;

    /// <summary>入れ物の版。**読めない新しい版は断る。**</summary>
    public int FormatVersion { get; init; } = SurveyExchange.CurrentFormatVersion;

    /// <summary>
    /// エクスポートで落としたものの一覧。**人が読むための情報で、インポートでは使わない。**
    /// </summary>
    public IReadOnlyList<string> Omitted { get; init; } = [];

    public required SurveyDefinition Definition { get; init; }

    public MappingDefinition Mapping { get; init; } = new();
}

/// <summary>インポートで入れ物を断った理由。</summary>
public enum SurveyExchangeError
{
    /// <summary>大きすぎる。</summary>
    TooLarge,

    /// <summary>JSON として読めない、または定義の形に合わない。</summary>
    Malformed,

    /// <summary>この製品のエクスポートではない。</summary>
    WrongFormat,

    /// <summary>この版では読めない新しい入れ物。</summary>
    UnsupportedVersion,

    /// <summary>ページ・設問・選択肢の数が上限を超えている。</summary>
    TooManyItems,

    /// <summary>ページまたは設問の識別子が重なっている、または長すぎる。</summary>
    InvalidIdentifier,
}

/// <summary>入れ物を読んだ結果。</summary>
public sealed record SurveyExchangeParseResult(
    SurveyExchangeDocument? Document,
    SurveyExchangeError? Error,
    string? Detail = null)
{
    public bool IsSuccess => Document is not null;
}

/// <summary>
/// アンケートの定義を JSON としてエクスポートし、インポートする（Issue #1009）。
/// </summary>
/// <remarks>
/// <para>
/// **複製（Issue #46）・テンプレート（Issue #58）と同じ「コピーしてよいもの」の線を使う。**
/// コピーする側の線引き（<see cref="SurveyDuplication.Copy"/>）をエクスポートにもインポートにも使い、
/// 線が 2 つに割れないようにする。違いは **コピーする先が同じ環境の DB か、環境をまたぐファイルか**
/// だけで、ファイルは複製と違って **誰が作ったか分からない入力**になる。
/// </para>
/// <para>
/// **秘密はエクスポートしない。** 定義にも資格情報の平文は無いが、
/// 参照（<see cref="LookupDefinition.CredentialId"/>）は他の環境では意味を持たず、
/// 取り違えると別の環境の資格情報を指すので、複製と同じく落とす。
/// </para>
/// <para>
/// **画像などの資産（ヘッダ画像・記法から参照する画像）は運ばない。**
/// DB の別の表にあり、大きさの上限や種類の検査（<c>AdminSurveyEndpoints</c>）を
/// 迂回してインポートされるのを避けるため。ヘッダ画像の指定は落とす。
/// </para>
/// </remarks>
public static class SurveyExchange
{
    /// <summary>入れ物の種類を表す文字列。</summary>
    public const string FormatName = "vehiclevision-questionnaire-survey";

    /// <summary>いまの入れ物の版。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>インポートする JSON の大きさの上限（バイト）。</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>ページ数の上限。</summary>
    public const int MaxPages = 200;

    /// <summary>設問数（全ページ合計）の上限。</summary>
    public const int MaxQuestions = 2000;

    /// <summary>1 設問あたりの選択肢数の上限。</summary>
    public const int MaxChoicesPerQuestion = 1000;

    /// <summary>識別子の最大長。DB の列（64）に合わせる。</summary>
    public const int MaxIdentifierLength = 64;

    private static readonly string[] OmittedItems =
    [
        "pleasanterSiteId",
        "responseJsonColumn",
        "publicId",
        "status",
        "publishedVersion",
        "acceptancePeriod",
        "responseLimit",
        "credentials",
        "headerImage",
        "uploadedAssets",
        "assetHistory",
    ];

    private static readonly JsonSerializerOptions IndentedOptions =
        new(SurveyJson.Options) { WriteIndented = true };

    /// <summary>定義とマッピングをエクスポートする。</summary>
    /// <param name="definition">元の定義。**変えない。**</param>
    /// <param name="mapping">元のマッピング。</param>
    public static string Export(SurveyDefinition definition, MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(mapping);

        var document = new SurveyExchangeDocument
        {
            Omitted = OmittedItems,
            Definition = Portable(definition),
            Mapping = mapping,
        };

        return JsonSerializer.Serialize(document, IndentedOptions);
    }

    /// <summary>
    /// 環境をまたいで持ち運べる形にする。**複製と同じ線引きに、画像の指定を落とす。**
    /// </summary>
    /// <remarks>内部 ID はインポート側が決めるので空にする。</remarks>
    public static SurveyDefinition Portable(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var copied = SurveyDuplication.Copy(definition, "portable", renameAsCopy: false);
        var theme = copied.Theme is { } source
            ? source with { HeaderImageId = null }
            : null;

        return copied with
        {
            SurveyId = string.Empty,
            Theme = theme is { IsDefault: true } ? null : theme,
        };
    }

    /// <summary>入れ物を読み、**形と大きさ**を確かめる。</summary>
    /// <remarks>
    /// ここで見るのは**入力として安全か**だけ。**公開前の検査と同じ内容の検査**
    /// （マッピング・分岐・自動返信・設問の設定・ルックアップ）は
    /// 呼ぶ側が <c>PublishInspector</c> で行う。
    /// </remarks>
    public static SurveyExchangeParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.TooLarge);
        }

        // **種類と版は、定義の形より先に見る。** 別の JSON や新しい版を、
        // 「形が違う」というあいまいな理由で断らない
        try
        {
            using var probe = JsonDocument.Parse(json);
            if (probe.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
            }

            if (!TryGetProperty(probe.RootElement, "format", out var format)
                || format.ValueKind != JsonValueKind.String
                || !string.Equals(format.GetString(), FormatName, StringComparison.Ordinal))
            {
                return new SurveyExchangeParseResult(null, SurveyExchangeError.WrongFormat);
            }

            if (!TryGetProperty(probe.RootElement, "formatVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number)
                || number < 1
                || number > CurrentFormatVersion)
            {
                return new SurveyExchangeParseResult(null, SurveyExchangeError.UnsupportedVersion);
            }
        }
        catch (JsonException)
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
        }

        var document = SurveyJson.Deserialize<SurveyExchangeDocument>(json);
        if (document?.Definition is null)
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
        }

        try
        {
            if (Check(document.Definition) is { } error)
            {
                return error;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            // **配列の欄に null を入れられた**など。既定値の配列は列挙で落ちる
            return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
        }

        return new SurveyExchangeParseResult(document, null);
    }

    /// <summary>
    /// インポートする定義を、新しいアンケートの下書きにする。
    /// **複製と同じ線引き（版・資格情報）に、画像の指定の除去を足す。**
    /// </summary>
    /// <param name="document">読んだ入れ物。</param>
    /// <param name="newSurveyId">インポート先の内部 ID。</param>
    public static SurveyDefinition ToDraft(SurveyExchangeDocument document, string newSurveyId)
    {
        ArgumentNullException.ThrowIfNull(document);

        var portable = Portable(document.Definition);
        return SurveyDuplication.Copy(portable, newSurveyId, renameAsCopy: false);
    }

    private static SurveyExchangeParseResult? Check(SurveyDefinition definition)
    {
        if (definition.Pages.IsDefault || definition.Title is null)
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
        }

        if (definition.Pages.Length > MaxPages)
        {
            return new SurveyExchangeParseResult(null, SurveyExchangeError.TooManyItems);
        }

        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in definition.Pages)
        {
            if (page.Questions.IsDefault)
            {
                return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
            }

            if (!IsValidIdentifier(page.PageId) || !pageIds.Add(page.PageId))
            {
                return new SurveyExchangeParseResult(
                    null, SurveyExchangeError.InvalidIdentifier, page.PageId);
            }

            foreach (var question in page.Questions)
            {
                if (question.Choices.IsDefault)
                {
                    return new SurveyExchangeParseResult(null, SurveyExchangeError.Malformed);
                }

                if (question.Choices.Length > MaxChoicesPerQuestion)
                {
                    return new SurveyExchangeParseResult(
                        null, SurveyExchangeError.TooManyItems, question.QuestionId);
                }

                if (!IsValidIdentifier(question.QuestionId) || !questionIds.Add(question.QuestionId))
                {
                    return new SurveyExchangeParseResult(
                        null, SurveyExchangeError.InvalidIdentifier, question.QuestionId);
                }
            }
        }

        return questionIds.Count > MaxQuestions
            ? new SurveyExchangeParseResult(null, SurveyExchangeError.TooManyItems)
            : null;
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentifierLength;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
