using System.Collections.Immutable;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>パスワードの条件 1 つ分。**Pleasanter 本体と同じ形**。</summary>
/// <remarks>
/// 本体の <c>Implem.ParameterAccessor/Parts/PasswordPolicy.cs</c> と同じ項目名にしてある。
/// **使い慣れた運用者が、本体の <c>Security.json</c> をそのまま持ってこられるようにするため。**
/// </remarks>
public sealed record AdminPasswordRule
{
    /// <summary>この条件を使うか。**false なら見ない。**</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>条件の正規表現。**部分一致**（本体と同じ）。</summary>
    /// <remarks>
    /// 全体一致にしたいときは <c>^…$</c> を書く。
    /// ⚠️ **先読み・後方参照・原子グループは使えない**（後退戻りしない照合器で動かすため）。
    /// </remarks>
    public string Regex { get; init; } = string.Empty;

    /// <summary>合わないときに出す文言。**言語ごとに並べる。**</summary>
    public IReadOnlyList<AdminPasswordRuleMessage> Languages { get; init; } = [];
}

/// <summary>言語ごとの文言。**Pleasanter 本体の <c>DisplayElement</c> と同じ形**。</summary>
public sealed record AdminPasswordRuleMessage
{
    /// <summary>言語コード。**空なら既定**（どの言語にも当てはまらないときに使う）。</summary>
    public string? Language { get; init; }

    /// <summary>文言そのもの。</summary>
    public string Body { get; init; } = string.Empty;
}

/// <summary>パスワードの条件の設定。</summary>
/// <remarks>
/// 実値は <c>App_Data/Parameters/Security.json</c> から読む。
/// </remarks>
public sealed record AdminPasswordPolicyOptions
{
    /// <summary>最低の長さ。</summary>
    /// <remarks>
    /// **文字種の縛りではなく長さで担保する。** 記号を混ぜさせると短く単純な値に寄る。
    /// </remarks>
    public int MinimumLength { get; init; } = 12;

    /// <summary>**ログイン ID と同じパスワードを許すか。** 既定は許さない。</summary>
    /// <remarks>
    /// 大文字小文字は区別しない。**ログイン ID は総当たりの起点になる**ので、
    /// そのままパスワードにされると 1 回で通る。
    /// </remarks>
    public bool AllowSameAsLoginId { get; init; }

    /// <summary>正規表現の条件。**上から順に見て、最初に合わなかったものの文言を返す。**</summary>
    public IReadOnlyList<AdminPasswordRule> Policies { get; init; } = [];
}

/// <summary>管理者のパスワードに求める条件。</summary>
/// <remarks>
/// <para>
/// **1 か所に置く。** 最初の管理者・招待の受け取り・変更のどこかだけ緩いと、
/// そこが一番弱い所になる（<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// <para>
/// ⚠️ **設定の正規表現は ReDoS になり得る。** 回答側の検証（Issue #102）と同じく
/// **後退戻りしない照合器（<see cref="RegexOptions.NonBacktracking"/>）＋時間の上限**で動かす。
/// **組み立てられない指定は、この型を作る時点で落とす**（黙って無効にすると、条件が無いまま動く）。
/// </para>
/// </remarks>
public sealed class AdminPasswordPolicy
{
    /// <summary>照合の時間の上限。</summary>
    /// <remarks>
    /// 後退戻りしない照合器なので入力の長さに比例した時間で終わるが、
    /// **上限は別に渡しておく。** 照合器を作る側が将来変わっても、上限だけは残る。
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private readonly AdminPasswordPolicyOptions options;
    private readonly ImmutableArray<(Regex Regex, AdminPasswordRule Rule)> rules;

    /// <exception cref="InvalidOperationException">
    /// 正規表現が組み立てられない場合。**起動時に落とす。**
    /// </exception>
    public AdminPasswordPolicy(AdminPasswordPolicyOptions options)
    {
        this.options = options;

        var builder = ImmutableArray.CreateBuilder<(Regex, AdminPasswordRule)>();
        foreach (var rule in options.Policies.Where(rule => rule.Enabled))
        {
            if (string.IsNullOrEmpty(rule.Regex))
            {
                throw new InvalidOperationException(
                    "PasswordPolicies の Regex が空。使わないなら Enabled を false にする");
            }

            try
            {
                // **部分一致**（Pleasanter 本体の RegexExists と同じ）。
                // 全体一致にしたいときは設定側で ^…$ を書く
                builder.Add((
                    new Regex(rule.Regex, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Timeout),
                    rule));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // **先読み・後方参照・原子グループは NotSupportedException、
                // 組み立てられない書き方は ArgumentException で来る。** 両方受ける
                // 起動時に気付けるよう、書き方をそのまま出す
                throw new InvalidOperationException(
                    $"PasswordPolicies の Regex が使えない: {rule.Regex}"
                    + "（先読み・後方参照・原子グループは使えない）",
                    exception);
            }
        }

        rules = builder.ToImmutable();
    }

    /// <summary>最低の長さ。</summary>
    public int MinimumLength => options.MinimumLength;

    /// <summary>受け付けられるパスワードか。</summary>
    /// <param name="password">パスワード。</param>
    /// <param name="loginId">
    /// ログイン ID。**同じ値を許さない設定のときに使う。** 分からなければ <c>null</c>。
    /// </param>
    public bool IsAcceptable(string? password, string? loginId = null) =>
        Check(password, loginId, language: null) is null;

    /// <summary>条件に合わない理由を返す。**合っていれば <c>null</c>。**</summary>
    /// <remarks>
    /// **順に見て、最初に合わなかったものの文言を返す。**
    /// まとめて返さないのは、直す側が 1 つずつ潰せる方が分かりやすいため。
    /// </remarks>
    public string? Check(string? password, string? loginId, string? language)
    {
        if (password is null || password.Length < options.MinimumLength)
        {
            return ServerMessages.Get(ServerMessageKeys.PasswordTooShort, language, options.MinimumLength);
        }

        if (!options.AllowSameAsLoginId
            && !string.IsNullOrEmpty(loginId)
            && string.Equals(password, loginId, StringComparison.OrdinalIgnoreCase))
        {
            return ServerMessages.Get(ServerMessageKeys.PasswordSameAsLoginId, language);
        }

        foreach (var (regex, rule) in rules)
        {
            if (regex.IsMatch(password))
            {
                continue;
            }

            // **文言を書き忘れても、通ってしまわないようにする。**
            // 条件には合っていないのだから、何かは返す
            return MessageOf(rule, language)
                ?? ServerMessages.Get(ServerMessageKeys.PasswordPolicyMismatch, language);
        }

        return null;
    }

    /// <summary>条件を満たさないときの既定の文言。</summary>
    /// <remarks>
    /// **どの条件で落ちたか分からない場所**（結果だけを見るところ）で使う。
    /// </remarks>
    public string Message(string? language) =>
        ServerMessages.Get(ServerMessageKeys.PasswordTooShort, language, options.MinimumLength);

    /// <summary>言語に合う文言を選ぶ。**無ければ言語指定なしのもの。**</summary>
    private static string? MessageOf(AdminPasswordRule rule, string? language)
    {
        var normalized = SupportedLanguages.Normalize(language);

        var exact = rule.Languages.FirstOrDefault(
            message => !string.IsNullOrEmpty(message.Language)
                && string.Equals(message.Language, normalized, StringComparison.OrdinalIgnoreCase));

        var fallback = rule.Languages.FirstOrDefault(message => string.IsNullOrEmpty(message.Language));

        var body = exact?.Body ?? fallback?.Body;
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}
