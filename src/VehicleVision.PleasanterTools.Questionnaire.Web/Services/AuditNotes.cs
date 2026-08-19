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
}
