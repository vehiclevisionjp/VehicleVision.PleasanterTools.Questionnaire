using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail;

/// <summary>件名と本文へ差し込める値（Issue #209）。</summary>
/// <remarks>
/// <para>
/// **差し込めるのはここに挙げたものだけ。** 回答の中身を任意に差し込む口は作らない。
/// **どの設問が何を含むか分からないまま本文へ出す**ことになるため
/// （回答の写しは専用のチェックがある）。
/// </para>
/// <para>
/// **知らない差し込みはそのまま残す。** 消すと、文面の一部が黙って欠ける。
/// **書き間違いに気付けるほうがよい。**
/// </para>
/// </remarks>
public static partial class MailPlaceholders
{
    /// <summary>日時の書き方。**秒は出さない**（受け付けた時刻の精度に意味は無い）。</summary>
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>差し込みを埋める。</summary>
    /// <param name="text">元の文字列。</param>
    /// <param name="title">アンケートの題名（回答者の言語）。</param>
    /// <param name="submittedAt">受け付けた日時。**表示する時間帯へ直したもの。**</param>
    /// <param name="values">回答や URL など、送信経路で解決した値。</param>
    public static string Fill(
        string text,
        string title,
        DateTimeOffset submittedAt,
        AutoReplyPlaceholderValues? values = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        values ??= new AutoReplyPlaceholderValues();
        return AutoReplyKeywords.Pattern().Replace(text, match => match.Groups[1].Value switch
        {
            AutoReplyKeywords.Title => title,
            AutoReplyKeywords.SubmittedAt => Format(submittedAt),
            AutoReplyKeywords.AcceptTo => Format(values.AcceptTo),
            AutoReplyKeywords.Answers => values.Answers ?? string.Empty,
            AutoReplyKeywords.FormUrl => values.FormUrl ?? string.Empty,
            AutoReplyKeywords.EditUrl => values.EditUrl ?? string.Empty,
            AutoReplyKeywords.EditUrlExpiresAt => Format(values.EditUrlExpiresAt),
            AutoReplyKeywords.AssetsUrl => values.AssetsUrl ?? string.Empty,
            AutoReplyKeywords.AssetsUrlExpiresAt => Format(values.AssetsUrlExpiresAt),

            // **知らない差し込みはそのまま残す**
            _ => match.Value,
        });
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToString(DateTimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>送信経路で解決してからメール本文へ差し込む値。</summary>
/// <remarks>
/// **値が作れない既知のキーワードは空文字にする。**
/// 未知のキーワードをそのまま残す規則とは別である。
/// </remarks>
public sealed record AutoReplyPlaceholderValues(
    DateTimeOffset? AcceptTo = null,
    string? Answers = null,
    string? FormUrl = null,
    string? EditUrl = null,
    DateTimeOffset? EditUrlExpiresAt = null,
    string? AssetsUrl = null,
    DateTimeOffset? AssetsUrlExpiresAt = null);
