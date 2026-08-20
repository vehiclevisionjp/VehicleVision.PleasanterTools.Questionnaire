using System.Collections.Immutable;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Runtime;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Scripting;

/// <summary>Jint（JavaScript）で変換スクリプトを実行する。</summary>
/// <remarks>
/// <para>
/// **上限付きで走らせることがこの型の目的**（Issue #83）。
/// 上限を超えたら <see cref="ScriptConverterException"/> を投げ、
/// <c>MappingEvaluator</c> がマッピングの不備に変え、送信ワーカーがデッドレターへ回す。
/// **黙って空の値を入れない。** 何が起きたか分からないまま列が消えるのが一番困る。
/// </para>
/// <para>
/// **スクリプトは関数の中身として扱う。** <c>input</c>（文字列の配列）を受け取り、
/// <c>return</c> した値が結果になる。返せるのは文字列・数値・真偽値・それらの配列。
/// <c>null</c> / <c>undefined</c> / 空配列は「値なし（＝列を消す）」。
/// </para>
/// <para>
/// **決定性を壊す口を塞ぐ**（<c>_documents/アーキテクチャ方針.md</c> 8 章）。
/// <c>Date</c> と <c>Math.random</c> は使えない。同じ回答からは常に同じ結果が出る。
/// CLR へのアクセスは Jint の既定で閉じている（こちらから何も渡さない）。
/// </para>
/// </remarks>
public sealed class JintScriptConverter(ScriptConverterOptions? options = null) : IScriptConverter
{
    private readonly ScriptConverterOptions _options = options ?? new ScriptConverterOptions();

    /// <summary>決定性を壊す口を塞ぐ前置き。</summary>
    private const string Prelude = """
        Math.random = function () { throw new Error('乱数は使えない'); };
        """;

    public ImmutableArray<string> Convert(string script, ImmutableArray<string> input)
    {
        ArgumentNullException.ThrowIfNull(script);

        // **エンジンは 1 回ごとに作る。** 使い回すと前の実行が残した状態が次に見え、
        // 「同じ回答からは同じ結果」が崩れる
        var engine = new Engine(engineOptions =>
        {
            engineOptions.TimeoutInterval(_options.TimeLimit);
            engineOptions.LimitMemory(_options.MemoryLimitBytes);
            engineOptions.LimitRecursion(_options.RecursionLimit);
            engineOptions.Strict();
        });

        try
        {
            engine.SetValue("Date", JsValue.Undefined);
            engine.Execute(Prelude);

            // **スクリプトを関数の中身として包む。** 変数がグローバルへ漏れず、
            // `return` で結果を返す形にできる
            var function = engine.Evaluate($"(function (input) {{\n{script}\n}})");
            var result = engine.Call(function, [Arguments(engine, input)]);

            return ToValues(result);
        }
        catch (TimeoutException exception)
        {
            // **打ち切ったことが分かる文言にする。** デッドレターの理由にそのまま載る
            throw new ScriptConverterException(
                $"実行時間の上限 {_options.TimeLimit.TotalMilliseconds:0} ミリ秒を超えたので打ち切った",
                exception);
        }
        catch (MemoryLimitExceededException exception)
        {
            throw new ScriptConverterException(
                $"メモリの上限 {_options.MemoryLimitBytes} バイトを超えたので打ち切った", exception);
        }
        catch (RecursionDepthOverflowException exception)
        {
            throw new ScriptConverterException(
                $"再帰が上限 {_options.RecursionLimit} 段を超えたので打ち切った", exception);
        }
        catch (JavaScriptException exception)
        {
            throw new ScriptConverterException(
                $"スクリプトが例外を投げた: {exception.Message}", exception);
        }
        catch (Exception exception) when (exception is not ScriptConverterException)
        {
            // 構文エラーなど、上のどれでもない失敗。**回答は捨てず、不備として扱う**
            throw new ScriptConverterException(
                $"スクリプトを実行できなかった: {exception.Message}", exception);
        }
    }

    private static JsValue Arguments(Engine engine, ImmutableArray<string> input)
    {
        var items = new JsValue[input.IsDefault ? 0 : input.Length];

        for (var index = 0; index < items.Length; index++)
        {
            items[index] = input[index];
        }

        return new JsArray(engine, items);
    }

    /// <summary>戻り値を文字列の配列にする。</summary>
    /// <remarks>**空配列は「消す」**（<c>MappingEvaluator</c> と同じ約束）。</remarks>
    private static ImmutableArray<string> ToValues(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return [];
        }

        if (value is JsArray array)
        {
            var values = ImmutableArray.CreateBuilder<string>();

            foreach (var item in array)
            {
                if (item.IsNull() || item.IsUndefined())
                {
                    continue;
                }

                values.Add(TypeConverter.ToString(item));
            }

            return values.ToImmutable();
        }

        return [TypeConverter.ToString(value)];
    }
}
