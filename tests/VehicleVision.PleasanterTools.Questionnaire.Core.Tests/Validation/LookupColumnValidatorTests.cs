using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>参照元の列の型の検証（Issue #808。実測の判断 2・3 の決定）。</summary>
public class LookupColumnValidatorTests
{
    private static Question Q(string id, QuestionType type = QuestionType.Text) => new()
    {
        QuestionId = id,
        Type = type,
        Title = LocalizedText.Japanese(id),
    };

    private static SurveyDefinition Survey() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    Q("btn"), Q("k1"), Q("t1"), Q("t2"),
                    Q("radio", QuestionType.Radio), Q("scale", QuestionType.Scale),
                ],
            },
        ],
    };

    private static LookupSourceColumn Text(string name, bool choices = false) =>
        new(name, LookupColumnKind.Text, HasChoices: choices);

    private static LookupSourceColumn Num(
        string name, bool nullable = true, int? places = 0) =>
        new(name, LookupColumnKind.Number, Nullable: nullable, DecimalPlaces: places);

    private static LookupDefinition Lookup(
        string keyColumn = "ClassA",
        string targetColumn = "ClassB",
        string targetQuestion = "t1",
        LookupMatchMode mode = LookupMatchMode.Exact) => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "btn",
        SourceSiteId = 1,
        CredentialId = "c",
        Keys =
        [
            new LookupKey
            {
                QuestionId = "k1", SourceColumn = keyColumn,
                MatchMode = mode, AllowPrefix = mode == LookupMatchMode.Prefix,
            },
        ],
        Targets = [new LookupTarget { SourceColumn = targetColumn, QuestionId = targetQuestion }],
        AllowedColumns = [targetColumn],
    };

    private static LookupColumnProblemCode[] Codes(
        LookupDefinition lookup, params LookupSourceColumn[] columns) =>
        [.. LookupColumnValidator.Validate(Survey(), lookup, columns).Select(p => p.Code)];

    [Fact]
    public void 選択肢のない文字列列のキーと文字列列の入力先は通る()
    {
        Assert.Empty(Codes(Lookup(), Text("ClassA"), Text("ClassB")));
    }

    [Fact]
    public void 整数だけの数値列はキーにできる()
    {
        Assert.Empty(Codes(Lookup(keyColumn: "NumA"), Num("NumA", places: 0), Text("ClassB")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void 小数を持つ数値列はキーにできない(int? places)
    {
        Assert.Contains(
            LookupColumnProblemCode.KeyColumnTypeUnsupported,
            Codes(Lookup(keyColumn: "NumA"), Num("NumA", places: places), Text("ClassB")));
    }

    [Fact]
    public void 選択肢のある列_日付_チェックはキーにできない()
    {
        // **選択肢のある列は単一の文字列を渡すと条件が捨てられて全件が返る**（実測）
        Assert.Contains(
            LookupColumnProblemCode.KeyColumnTypeUnsupported,
            Codes(Lookup(), Text("ClassA", choices: true), Text("ClassB")));
        Assert.Contains(
            LookupColumnProblemCode.KeyColumnTypeUnsupported,
            Codes(Lookup(), new LookupSourceColumn("ClassA", LookupColumnKind.Date), Text("ClassB")));
        Assert.Contains(
            LookupColumnProblemCode.KeyColumnTypeUnsupported,
            Codes(Lookup(), new LookupSourceColumn("ClassA", LookupColumnKind.Check), Text("ClassB")));
    }

    [Fact]
    public void 前方一致は文字列列だけ()
    {
        var lookup = Lookup(keyColumn: "NumA", mode: LookupMatchMode.Prefix);

        Assert.Contains(
            LookupColumnProblemCode.PrefixColumnNotText,
            Codes(lookup, Num("NumA"), Text("ClassB")));
    }

    [Fact]
    public void 存在しない列を弾く()
    {
        var codes = Codes(Lookup(), Text("ClassB"));

        Assert.Contains(LookupColumnProblemCode.ColumnNotFound, codes);
        Assert.Contains(
            LookupColumnProblemCode.ColumnNotFound, Codes(Lookup(), Text("ClassA")));
    }

    [Fact]
    public void 返却する数値列はNullableを要求する()
    {
        // **0 は空として保存・返却され、反映されない**（実測）
        var lookup = Lookup(targetColumn: "NumB", targetQuestion: "scale");

        Assert.Contains(
            LookupColumnProblemCode.NumberColumnNotNullable,
            Codes(lookup, Text("ClassA"), Num("NumB", nullable: false)));
        Assert.Empty(Codes(lookup, Text("ClassA"), Num("NumB", nullable: true)));
    }

    [Fact]
    public void 許可列だけに含まれる数値列もNullableを要求する()
    {
        var lookup = Lookup() with { AllowedColumns = ["ClassB", "NumB"] };

        Assert.Contains(
            LookupColumnProblemCode.NumberColumnNotNullable,
            Codes(lookup, Text("ClassA"), Text("ClassB"), Num("NumB", nullable: false)));
    }

    [Theory]
    [InlineData("t1", LookupColumnKind.Text, true)]
    [InlineData("t1", LookupColumnKind.Number, true)]
    [InlineData("t1", LookupColumnKind.Date, false)]
    [InlineData("t1", LookupColumnKind.Check, false)]
    [InlineData("radio", LookupColumnKind.Text, true)]
    [InlineData("radio", LookupColumnKind.Number, false)]
    [InlineData("scale", LookupColumnKind.Number, true)]
    [InlineData("scale", LookupColumnKind.Text, false)]
    public void 入力先の設問の形式と列の型の互換性(string question, LookupColumnKind kind, bool ok)
    {
        var target = new LookupSourceColumn("ClassB", kind, Nullable: true, DecimalPlaces: 0);

        var codes = Codes(Lookup(targetQuestion: question), Text("ClassA"), target);

        Assert.Equal(ok, !codes.Contains(LookupColumnProblemCode.TargetColumnTypeIncompatible));
    }
}
