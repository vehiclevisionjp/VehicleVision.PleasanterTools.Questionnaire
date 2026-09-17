using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>自動返信メールで使えるキーワード（Issue #319）。</summary>
public static partial class AutoReplyKeywords
{
    public const string Title = "title";
    public const string SubmittedAt = "submittedAt";
    public const string AcceptTo = "acceptTo";
    public const string Answers = "answers";
    public const string FormUrl = "formUrl";
    public const string EditUrl = "editUrl";
    public const string EditUrlExpiresAt = "editUrlExpiresAt";
    public const string AssetsUrl = "assetsUrl";
    public const string AssetsUrlExpiresAt = "assetsUrlExpiresAt";

    public static ImmutableArray<string> All { get; } =
    [
        Title,
        SubmittedAt,
        AcceptTo,
        Answers,
        FormUrl,
        EditUrl,
        EditUrlExpiresAt,
        AssetsUrl,
        AssetsUrlExpiresAt,
    ];

    private static readonly HashSet<string> Known = All.ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    public static partial Regex Pattern();

    /// <summary>指定したキーワードが件名または本文のいずれかに書かれているか。</summary>
    public static bool Contains(AutoReplySettings settings, string keyword) =>
        Contains(settings.Subject, keyword) || Contains(settings.Body, keyword);

    /// <summary>使える一覧に無いキーワードを、最初に現れた順で返す。</summary>
    public static ImmutableArray<string> UnknownIn(params string?[] texts) =>
        [
            .. texts
                .Where(text => !string.IsNullOrEmpty(text))
                .SelectMany(text => Pattern().Matches(text!).Select(match => match.Groups[1].Value))
                .Where(keyword => !Known.Contains(keyword))
                .Distinct(StringComparer.Ordinal),
        ];

    private static bool Contains(LocalizedText? text, string keyword) =>
        text is not null
        && text.Languages.Any(language => Contains(text.Get(language), keyword));

    private static bool Contains(string text, string keyword) =>
        Pattern().Matches(text).Any(match =>
            string.Equals(match.Groups[1].Value, keyword, StringComparison.Ordinal));
}
