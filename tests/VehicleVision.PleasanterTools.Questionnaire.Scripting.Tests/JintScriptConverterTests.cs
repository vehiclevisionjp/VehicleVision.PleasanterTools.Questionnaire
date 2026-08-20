using System.Collections.Immutable;
using System.Diagnostics;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Scripting.Tests;

public class JintScriptConverterTests
{
    private static readonly ScriptConverterOptions ShortLimit = new()
    {
        TimeLimit = TimeSpan.FromMilliseconds(100),
    };

    private static string[] Convert(
        string script, params string[] input) =>
        [.. new JintScriptConverter(ShortLimit).Convert(script, [.. input])];

    [Fact]
    public void 入力を受け取って結果を返す()
    {
        Assert.Equal(["ABC"], Convert("return input[0].toUpperCase();", "abc"));
    }

    [Fact]
    public void 配列を返せる()
    {
        Assert.Equal(
            ["a", "b"],
            Convert("return input.map(function (v) { return v; });", "a", "b"));
    }

    [Fact]
    public void 数値や真偽値は文字列になる()
    {
        Assert.Equal(["3"], Convert("return input.length + 1;", "x", "y"));
        Assert.Equal(["true"], Convert("return input.length > 0;", "x"));
    }

    [Fact]
    public void 値なしは空になる()
    {
        // **空配列は「消す」**（MappingEvaluator と同じ約束）
        Assert.Empty(Convert("return null;", "x"));
        Assert.Empty(Convert("return;", "x"));
        Assert.Empty(Convert("return [];", "x"));
        Assert.Empty(Convert("return [null, undefined];", "x"));
    }

    [Fact]
    public void 止まらないスクリプトは上限で打ち切る()
    {
        // **これがこの型の存在理由**（Issue #83）。
        // 上限が無いと 1 件の書き間違いで送信ワーカーが永久に固まる
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<ScriptConverterException>(
            () => Convert("while (true) { }"));

        stopwatch.Stop();

        Assert.Contains("実行時間の上限", exception.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"上限を過ぎても止まらなかった（{stopwatch.Elapsed}）");
    }

    [Fact]
    public void 終わらない再帰も打ち切る()
    {
        var exception = Assert.Throws<ScriptConverterException>(
            () => Convert("function f() { return f(); } return f();"));

        Assert.Contains("再帰", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 増え続ける確保も打ち切る()
    {
        var converter = new JintScriptConverter(new ScriptConverterOptions
        {
            TimeLimit = TimeSpan.FromSeconds(10),
            MemoryLimitBytes = 1024 * 1024,
        });

        var exception = Assert.Throws<ScriptConverterException>(() => converter.Convert(
            "var a = []; while (true) { a.push('0123456789'); } return a.length;", []));

        Assert.Contains("メモリの上限", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void スクリプトの例外は不備として返る()
    {
        // **回答は捨てない。** 不備にして人が対処する
        var exception = Assert.Throws<ScriptConverterException>(
            () => Convert("throw new Error('だめ');"));

        Assert.Contains("だめ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 構文エラーも不備として返る()
    {
        Assert.Throws<ScriptConverterException>(() => Convert("return ("));
    }

    [Fact]
    public void 時刻と乱数は使えない()
    {
        // **決定性を壊す口は塞ぐ**（_documents/アーキテクチャ方針.md 8 章）
        Assert.Throws<ScriptConverterException>(() => Convert("return new Date().toString();"));
        Assert.Throws<ScriptConverterException>(() => Convert("return Math.random();"));
    }

    [Fact]
    public void CLRへは手が届かない()
    {
        Assert.Throws<ScriptConverterException>(
            () => Convert("return System.IO.File.ReadAllText('C:/');"));
        Assert.Throws<ScriptConverterException>(
            () => Convert("return importNamespace('System').Environment.MachineName;"));
    }

    [Fact]
    public void 前の実行の状態は次に見えない()
    {
        var converter = new JintScriptConverter(ShortLimit);

        converter.Convert("globalThis.leaked = 'x'; return 'a';", []);

        Assert.Equal(
            ["なし"],
            (string[])[.. converter.Convert(
                "return typeof globalThis.leaked === 'undefined' ? 'なし' : 'あり';", [])]);
    }
}
