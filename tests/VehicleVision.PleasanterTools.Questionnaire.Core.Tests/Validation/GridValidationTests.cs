using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>グリッドとランキングの回答を受け付けてよいかを見る（Issue #54）。</summary>
public class GridValidationTests
{
    private static Question Grid(bool required, bool multi = false) => new()
    {
        QuestionId = "q-grid",
        Type = multi ? QuestionType.CheckboxGrid : QuestionType.Grid,
        Title = LocalizedText.Japanese("満足度"),
        IsRequired = required,
        Choices =
        [
            new Choice("good", LocalizedText.Japanese("よい")),
            new Choice("bad", LocalizedText.Japanese("わるい")),
        ],
        Settings = new QuestionSettings
        {
            Rows =
            [
                new GridRow("price", LocalizedText.Japanese("価格")),
                new GridRow("quality", LocalizedText.Japanese("品質")),
            ],
        },
    };

    private static Question Ranking() => new()
    {
        QuestionId = "q-rank",
        Type = QuestionType.Ranking,
        Title = LocalizedText.Japanese("大事な順"),
        Choices =
        [
            new Choice("price", LocalizedText.Japanese("価格")),
            new Choice("speed", LocalizedText.Japanese("速さ")),
        ],
    };

    private static SurveyDefinition Definition(params Question[] questions) => new()
    {
        SurveyId = Guid.NewGuid().ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("見本"),
        Pages = [new Page { PageId = "p1", Questions = [.. questions] }],
    };

    private static Answer Rows(params (string RowId, string[] Values)[] rows)
    {
        var builder = ImmutableDictionary<string, ImmutableArray<string>>.Empty.ToBuilder();
        foreach (var (rowId, values) in rows)
        {
            builder[rowId] = [.. values];
        }

        return new Answer("q-grid", []) { Rows = builder.ToImmutable() };
    }

    private static ValidationErrorCode[] Codes(SurveyDefinition definition, params Answer[] answers) =>
        [.. AnswerValidator.Validate(definition, answers).Select(error => error.Code)];

    [Fact]
    public void 必須なら全部の行が要る()
    {
        // **1 行でも空なら足りない。** どこまで答えたのか分からなくなる
        var codes = Codes(
            Definition(Grid(required: true)),
            Rows(("price", ["good"])));

        Assert.Contains(ValidationErrorCode.RowRequired, codes);
    }

    [Fact]
    public void 全部の行が埋まっていれば通る()
    {
        Assert.Empty(AnswerValidator.Validate(
            Definition(Grid(required: true)),
            [Rows(("price", ["good"]), ("quality", ["bad"]))]));
    }

    [Fact]
    public void 必須でなければ空の行があってよい()
    {
        Assert.Empty(AnswerValidator.Validate(
            Definition(Grid(required: false)),
            [Rows(("price", ["good"]))]));
    }

    [Fact]
    public void 知らない行への答えは受け取らない()
    {
        // **行を消した後の古い画面から届き得る**
        var codes = Codes(
            Definition(Grid(required: false)),
            Rows(("居ない行", ["good"])));

        Assert.Contains(ValidationErrorCode.UnknownRow, codes);
    }

    [Fact]
    public void 一つ選ぶグリッドで二つ来たら受け取らない()
    {
        var codes = Codes(
            Definition(Grid(required: false)),
            Rows(("price", ["good", "bad"])));

        Assert.Contains(ValidationErrorCode.MultipleValuesNotAllowed, codes);
    }

    [Fact]
    public void 複数選べるグリッドなら二つ通る()
    {
        Assert.Empty(AnswerValidator.Validate(
            Definition(Grid(required: false, multi: true)),
            [Rows(("price", ["good", "bad"]))]));
    }

    [Fact]
    public void 無い選択肢は受け取らない()
    {
        var codes = Codes(
            Definition(Grid(required: false)),
            Rows(("price", ["ふつう"])));

        Assert.Contains(ValidationErrorCode.UnknownChoice, codes);
    }

    // ---- ランキング ---------------------------------------------------------

    [Fact]
    public void 同じ項目を二度並べたら受け取らない()
    {
        // **順位が決まらない**
        var codes = Codes(Definition(Ranking()), Answer.Of("q-rank", "price", "price"));

        Assert.Contains(ValidationErrorCode.DuplicateRank, codes);
    }

    [Fact]
    public void 一部だけ並べても通る()
    {
        // **全部並べることは求めない。** 上位だけ選ぶ使い方がある
        Assert.Empty(AnswerValidator.Validate(
            Definition(Ranking()), [Answer.Of("q-rank", "speed")]));
    }

    [Fact]
    public void ランキングに無い項目は受け取らない()
    {
        var codes = Codes(Definition(Ranking()), Answer.Of("q-rank", "居ない"));

        Assert.Contains(ValidationErrorCode.UnknownChoice, codes);
    }
}
