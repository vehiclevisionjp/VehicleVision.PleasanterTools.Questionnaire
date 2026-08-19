namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>監査ログへ添える補足を、入口の側から預ける。</summary>
/// <remarks>
/// <para>
/// **<see cref="AuditLogFilter"/> は要求本文に触らない。** 触れば合言葉が入り得るからで、
/// その約束は「書ける場所を絞る」ことで守っている。
/// **本文の中に残したい値があるときだけ、入口が明示して預ける。**
/// </para>
/// <para>
/// **預けてよいのは、漏れても困らない値だけ。**
/// ログイン ID は残す（どの利用者が狙われているか分からないと対処できない）。
/// 合言葉・2 要素の共有鍵・招待の合言葉・回答本文は預けない。
/// </para>
/// </remarks>
public static class AuditNotes
{
    private const string ItemKey = "questionnaire:audit_notes";

    private const string TargetKey = "questionnaire:audit_target";

    /// <summary>補足を 1 つ預ける。</summary>
    /// <remarks>**制御文字は落として長さも切る**（<see cref="LogSafe"/>）。</remarks>
    public static void Add(HttpContext context, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (context.Items[ItemKey] is not Dictionary<string, string> notes)
        {
            notes = [];
            context.Items[ItemKey] = notes;
        }

        notes[key] = LogSafe.Text(value);
    }

    /// <summary>預かった補足。</summary>
    internal static IReadOnlyDictionary<string, string>? Of(HttpContext context) =>
        context.Items[ItemKey] as Dictionary<string, string>;

    /// <summary>この操作が何に対して行われたかを明示する。</summary>
    /// <remarks>
    /// <para>
    /// **経路の値から対象が分からないときに使う**（<see cref="AuditLogFilter"/> は
    /// 経路の値しか見ない）。デッドレターの再送のように、
    /// **対象の識別子が経路に出せない**入口のためにある。
    /// </para>
    /// <para>
    /// **<c>ResponseToken</c> を渡さないこと。** 監査ログへ入れない決まり
    /// （<c>_documents/データモデル設計.md</c> 2.6）。代わりにアンケートの識別子を渡す。
    /// </para>
    /// </remarks>
    public static void SetTarget(HttpContext context, string targetType, string? targetId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);

        // **無い対象は「(なし)」ではなく無いままにする**（列は NULL 可）
        context.Items[TargetKey] =
            (LogSafe.Text(targetType), targetId is null ? null : LogSafe.Text(targetId));
    }

    /// <summary>明示された対象。**無ければ経路の値から見分ける。**</summary>
    internal static (string TargetType, string? TargetId)? TargetOf(HttpContext context) =>
        context.Items[TargetKey] as (string, string?)?;
}
