using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Scripting;

/// <summary>変換スクリプトに掛ける上限。</summary>
/// <remarks>
/// <para>
/// **無限ループを書くのに悪意は要らない。** <c>while (true)</c> を 1 つ書き間違えれば足りる。
/// 送信ワーカーは 1 件ずつ順に送る作りなので、1 件が止まると
/// **そのインスタンスの送信がすべて止まる**（Issue #83）。
/// </para>
/// <para>
/// **文法は狭めない**（<c>_documents/アーキテクチャ方針.md</c> 15 章の決定 2）。
/// ループも正規表現も書けてよい。**止まらないものを止められることだけを保証する。**
/// </para>
/// </remarks>
public sealed class ScriptConverterOptions
{
    /// <summary>スクリプト 1 回あたりの実行時間の上限。</summary>
    /// <remarks>
    /// ⚠️ **「1 回」は列 1 つぶん。** スクリプト変換を持つ列の数だけ実行されるので、
    /// 緩めるときは 1 件の回答あたりの合計で見積もること。
    /// </remarks>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>スクリプト 1 回あたりに使えるメモリの上限（バイト）。</summary>
    /// <remarks>
    /// **時間の上限だけでは、確保し続けるコードを止めきれない。**
    /// 打ち切るまでの間に大きな配列を作られると、他の処理を巻き添えにする。
    /// </remarks>
    public long MemoryLimitBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>再帰の深さの上限。</summary>
    /// <remarks>
    /// **スタックオーバーフローはプロセスごと落ちる。** 例外にして受け止められる形にしておく。
    /// </remarks>
    public int RecursionLimit { get; init; } = 64;

    /// <summary>実行時間の上限（ミリ秒）の設定名。</summary>
    public const string TimeLimitKey = "QUESTIONNAIRE_SCRIPT_TIMEOUT_MS";

    /// <summary>メモリの上限（バイト）の設定名。</summary>
    public const string MemoryLimitKey = "QUESTIONNAIRE_SCRIPT_MEMORY_BYTES";

    /// <summary>再帰の深さの上限の設定名。</summary>
    public const string RecursionLimitKey = "QUESTIONNAIRE_SCRIPT_RECURSION_LIMIT";

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **読めない値を黙って既定へ落とさない。** 設定したつもりが効いていない状態を作る。
    /// **0 以下も受け付けない。** 上限を外す指定は用意しない
    /// （外せると、この Issue で塞いだ穴がそのまま戻る）。
    /// </remarks>
    public static ScriptConverterOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new ScriptConverterOptions();

        return new ScriptConverterOptions
        {
            TimeLimit = TimeSpan.FromMilliseconds(ReadPositive(
                configuration,
                TimeLimitKey,
                (long)defaults.TimeLimit.TotalMilliseconds,
                "ミリ秒")),
            MemoryLimitBytes = ReadPositive(
                configuration, MemoryLimitKey, defaults.MemoryLimitBytes, "バイト"),
            RecursionLimit = (int)ReadPositive(
                configuration, RecursionLimitKey, defaults.RecursionLimit, "段"),
        };
    }

    private static long ReadPositive(
        IConfiguration configuration, string key, long fallback, string unit)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return long.TryParse(raw, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new InvalidOperationException(
                $"{key} は 1 以上の整数（{unit}）で指定する（今の値: {raw}）。上限は外せない");
    }
}
