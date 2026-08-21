using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>管理者が書いた正規表現を、安全に照合する（Issue #102）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **正規表現は、書き方しだいで照合が終わらなくなる**（ReDoS）。
/// 回答は誰でも送れるので、**入力の長さも中身も選べる**。
/// ここを素の <see cref="Regex"/> で通すと、公開フォームが 1 通の回答で止まる。
/// </para>
/// <para>
/// **後退戻りしない照合器（<see cref="RegexOptions.NonBacktracking"/>）で動かす。**
/// 入力の長さに比例した時間しかかからないので、破滅的な後退戻りが**原理的に起きない**。
/// 代わりに**先読み・後方参照・原子グループが使えない**ので、
/// そうした指定は<b>公開の前に</b>弾く（<see cref="QuestionSettingsValidator"/>）。
/// </para>
/// <para>
/// **時間の上限も併せて渡す。** 照合器を作る側が将来変わっても、
/// 上限だけは残るようにしておく。
/// </para>
/// <para>変換スクリプトを Jint の上限付きで動かしているのと同じ考え方（Issue #90）。</para>
/// </remarks>
public static class TextPattern
{
    /// <summary>1 回の照合に許す時間。</summary>
    /// <remarks>
    /// **後退戻りしない照合器では、まず届かない。** 保険であり、
    /// 照合器の作りが変わったときに効かせるためのもの。
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    /// <summary>覚えておく正規表現の数の上限。</summary>
    /// <remarks>
    /// **際限なく覚えると、アンケートを増やすだけでメモリを食う。**
    /// 溢れたら覚えずに毎回組み立てる（遅くなるだけで、動きは変わらない）。
    /// </remarks>
    private const int CacheCapacity = 256;

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>組み立てられる正規表現か。**公開の前に確かめるのに使う。**</summary>
    public static bool IsSupported(string? pattern) => TryCreate(pattern, out _);

    /// <summary>値の全体が合うか。</summary>
    /// <remarks>
    /// **組み立てられない正規表現は「合わない」とする。**
    /// 通してしまうと、壊れた指定が素通りの検証になる。
    /// </remarks>
    public static bool IsMatch(string? pattern, string value)
    {
        if (!TryCreate(pattern, out var regex))
        {
            return false;
        }

        try
        {
            return regex!.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // **時間切れは「合わない」。** 判断が付かないものを通さない
            return false;
        }
    }

    private static bool TryCreate(string? pattern, out Regex? regex)
    {
        regex = null;

        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (Cache.TryGetValue(pattern, out var cached))
        {
            regex = cached;
            return true;
        }

        try
        {
            // **前後を固定して全体一致にする。** 部分一致だと
            // 「数字 4 桁」が「どこかに数字 4 桁があればよい」になる。
            // 入れ子にして囲むので、選択（`a|b`）を書かれても意図が変わらない
            var created = new Regex(
                $"^(?:{pattern})$",
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                Timeout);

            if (Cache.Count < CacheCapacity)
            {
                Cache.TryAdd(pattern, created);
            }

            regex = created;
            return true;
        }
        catch (ArgumentException)
        {
            // 正規表現として組み立てられない
            return false;
        }
        catch (NotSupportedException)
        {
            // **後退戻りしない照合器で使えない構文**（先読み・後方参照・原子グループ）
            return false;
        }
    }
}
