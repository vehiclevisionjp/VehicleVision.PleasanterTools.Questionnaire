using System.Globalization;
using System.Text.RegularExpressions;

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
    /// <summary>アンケートの題名。**回答者の言語のもの。**</summary>
    public const string Title = "title";

    /// <summary>受け付けた日時。</summary>
    public const string SubmittedAt = "submittedAt";

    /// <summary>日時の書き方。**秒は出さない**（受け付けた時刻の精度に意味は無い）。</summary>
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary><c>{{name}}</c> の形。</summary>
    /// <remarks>
    /// **二重の波括弧にしてある。** 文中に単独の <c>{</c> が出ることはあるが、
    /// <c>{{…}}</c> が偶然現れることはまず無い。
    /// </remarks>
    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>差し込みを埋める。</summary>
    /// <param name="text">元の文字列。</param>
    /// <param name="title">アンケートの題名（回答者の言語）。</param>
    /// <param name="submittedAt">受け付けた日時。**表示する時間帯へ直したもの。**</param>
    public static string Fill(string text, string title, DateTimeOffset submittedAt)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Pattern().Replace(text, match => match.Groups[1].Value switch
        {
            Title => title,
            SubmittedAt => submittedAt.ToString(DateTimeFormat, CultureInfo.InvariantCulture),

            // **知らない差し込みはそのまま残す**
            _ => match.Value,
        });
    }
}
