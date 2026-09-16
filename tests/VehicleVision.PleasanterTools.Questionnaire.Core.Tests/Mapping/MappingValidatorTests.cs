using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

public class MappingValidatorTests
{
    private static SurveyDefinition Definition(params string[] questionIds) => new()
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
                    .. questionIds.Select(id => new Question
                    {
                        QuestionId = id,
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese(id),
                    }),
                ],
            },
        ],
    };

    private static MappingDefinition Mapping(params ColumnAssignment[] assignments) =>
        new() { Assignments = [.. assignments] };

    private static MappingProblemCode[] Codes(ImmutableArray<MappingProblem> problems) =>
        [.. problems.Select(problem => problem.Code)];

    [Fact]
    public void すべての設問が割り当てられていれば不備なし()
    {
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("q1")));

        Assert.Empty(MappingValidator.Validate(mapping, Definition("q1")));
    }

    [Fact]
    public void 同じ列への割り当てが重複していたら拒否する()
    {
        var mapping = Mapping(
            ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
            ColumnAssignment.Direct("ClassA", new MappingSource("q2")));

        var problems = MappingValidator.Validate(mapping, Definition("q1", "q2"));

        Assert.Contains(MappingProblemCode.DuplicateTargetColumn, Codes(problems));
        Assert.Contains(problems, problem => problem.IsBlocking);
    }

    [Fact]
    public void 入力が複数なのに変換が無ければ拒否する()
    {
        var mapping = Mapping(new ColumnAssignment
        {
            TargetColumn = "ClassA",
            Sources = [new MappingSource("q1"), new MappingSource("q2")],
            Converter = null,
        });

        Assert.Contains(
            MappingProblemCode.InvalidShape,
            Codes(MappingValidator.Validate(mapping, Definition("q1", "q2"))));
    }

    [Fact]
    public void 入力が一つも無ければ拒否する()
    {
        var mapping = Mapping(new ColumnAssignment
        {
            TargetColumn = "ClassA",
            Sources = [],
            Converter = MappingConverter.Of(ConverterOperations.Identity),
        });

        Assert.Contains(
            MappingProblemCode.InvalidShape,
            Codes(MappingValidator.Validate(mapping, Definition())));
    }

    [Fact]
    public void 定義に無い設問を入力にしていたら拒否する()
    {
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("存在しない")));

        Assert.Contains(
            MappingProblemCode.QuestionNotInDefinition,
            Codes(MappingValidator.Validate(mapping, Definition())));
    }

    [Fact]
    public void 説明文ブロックを入力にしていたら拒否する()
    {
        var definition = new SurveyDefinition
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
                        new Question
                        {
                            QuestionId = "note1",
                            Type = QuestionType.Note,
                            Title = LocalizedText.Japanese("説明"),
                        },
                    ],
                },
            ],
        };
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("note1")));

        Assert.Contains(
            MappingProblemCode.DisplayOnlyQuestionAsSource,
            Codes(MappingValidator.Validate(mapping, definition)));
    }

    [Fact]
    public void 予約列を書き込み先にしていたら拒否する()
    {
        var mapping = Mapping(ColumnAssignment.Direct("DescriptionZ", new MappingSource("q1")));

        Assert.Contains(
            MappingProblemCode.ReservedColumn,
            Codes(MappingValidator.Validate(mapping, Definition("q1"), ["DescriptionZ"])));
    }

    [Fact]
    public void スクリプトが空なら拒否する()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Script, ("script", "  ")),
            new MappingSource("q1")));

        Assert.Contains(
            MappingProblemCode.EmptyScript,
            Codes(MappingValidator.Validate(mapping, Definition("q1"))));
    }

    [Theory]
    [InlineData(ConverterOperations.Map)]
    [InlineData(ConverterOperations.ToCheck)]
    [InlineData(ConverterOperations.Contains)]
    [InlineData(ConverterOperations.Constant)]
    [InlineData(ConverterOperations.When)]
    public void 必須の変換設定が空なら拒否する(string operation)
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(operation),
            new MappingSource("q1")));

        Assert.Contains(
            MappingProblemCode.MissingConverterConfig,
            Codes(MappingValidator.Validate(mapping, Definition("q1"))));
    }

    [Fact]
    public void mapに置換元が一つあれば置換後が空でも拒否しない()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Map, ("map.削除する", "")),
            new MappingSource("q1")));

        Assert.DoesNotContain(
            MappingProblemCode.MissingConverterConfig,
            Codes(MappingValidator.Validate(mapping, Definition("q1"))));
    }

    [Fact]
    public void joinは区切りが未指定でも拒否しない()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(ConverterOperations.Join),
            new MappingSource("q1")));

        Assert.DoesNotContain(
            MappingProblemCode.MissingConverterConfig,
            Codes(MappingValidator.Validate(mapping, Definition("q1"))));
    }

    [Fact]
    public void whenはelseが空でも拒否しない()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "ClassA",
            MappingConverter.Of(
                ConverterOperations.When,
                ("when", "対象"),
                ("then", "変換後"),
                ("else", "")),
            new MappingSource("q1")));

        Assert.DoesNotContain(
            MappingProblemCode.MissingConverterConfig,
            Codes(MappingValidator.Validate(mapping, Definition("q1"))));
    }

    [Fact]
    public void Statusへ整数でない固定値を割り当てると拒否する()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "Status",
            MappingConverter.Of(ConverterOperations.Constant, ("value", "処理中")),
            new MappingSource("q1")));

        var problems = MappingValidator.Validate(
            mapping,
            Definition("q1"),
            targetValueKind: column => column == "Status"
                ? MappingTargetValueKind.Integer
                : null);

        Assert.Contains(MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Fact]
    public void Statusへ整数の固定値を割り当てられる()
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            "Status",
            MappingConverter.Of(ConverterOperations.Constant, ("value", "10")),
            new MappingSource("q1")));

        var problems = MappingValidator.Validate(
            mapping,
            Definition("q1"),
            targetValueKind: column => column == "Status"
                ? MappingTargetValueKind.Integer
                : null);

        Assert.DoesNotContain(MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Fact]
    public void Statusへ任意の文字列を受ける設問を直接割り当てると拒否する()
    {
        var mapping = Mapping(ColumnAssignment.Direct("Status", new MappingSource("q1")));

        var problems = MappingValidator.Validate(
            mapping,
            Definition("q1"),
            targetValueKind: column => column == "Status"
                ? MappingTargetValueKind.Integer
                : null);

        Assert.Contains(MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Theory]
    [InlineData("Locked", MappingTargetValueKind.Boolean, "true")]
    [InlineData("WorkValue", MappingTargetValueKind.Decimal, "1.5")]
    [InlineData("StartTime", MappingTargetValueKind.DateTime, "2026-09-16")]
    public void 本体の型に合う固定値は割り当てられる(
        string column,
        MappingTargetValueKind kind,
        string value)
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            column,
            MappingConverter.Of(ConverterOperations.Constant, ("value", value)),
            new MappingSource("q1")));

        var problems = MappingValidator.Validate(
            mapping,
            Definition("q1"),
            targetValueKind: target => target == column ? kind : null);

        Assert.DoesNotContain(MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Theory]
    [InlineData("Locked", MappingTargetValueKind.Boolean, "1")]
    [InlineData("WorkValue", MappingTargetValueKind.Decimal, "金額")]
    [InlineData("StartTime", MappingTargetValueKind.DateTime, "来週")]
    public void 本体の型に合わない固定値は公開前に拒否する(
        string column,
        MappingTargetValueKind kind,
        string value)
    {
        var mapping = Mapping(ColumnAssignment.Converted(
            column,
            MappingConverter.Of(ConverterOperations.Constant, ("value", value)),
            new MappingSource("q1")));

        var problems = MappingValidator.Validate(
            mapping,
            Definition("q1"),
            targetValueKind: target => target == column ? kind : null);

        Assert.Contains(MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Fact]
    public void 未割り当ての設問は警告するが保存は拒否しない()
    {
        // **回答の正本 JSON には残る**ので、列へ写らないだけでは拒否しない
        var mapping = Mapping(ColumnAssignment.Direct("ClassA", new MappingSource("q1")));

        var problems = MappingValidator.Validate(mapping, Definition("q1", "q2"));

        Assert.Contains(
            problems,
            problem => problem.Code is MappingProblemCode.UnmappedQuestion && !problem.IsBlocking);
        Assert.DoesNotContain(problems, problem => problem.IsBlocking);
    }

    [Fact]
    public void 説明文ブロックは未割り当てでも警告しない()
    {
        var definition = new SurveyDefinition
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
                        new Question
                        {
                            QuestionId = "note1",
                            Type = QuestionType.Note,
                            Title = LocalizedText.Japanese("説明"),
                        },
                    ],
                },
            ],
        };

        Assert.Empty(MappingValidator.Validate(new MappingDefinition(), definition));
    }

    [Theory]
    [InlineData(QuestionType.Text)]
    [InlineData(QuestionType.Paragraph)]
    [InlineData(QuestionType.Radio)]
    [InlineData(QuestionType.Checkbox)]
    [InlineData(QuestionType.Date)]
    [InlineData(QuestionType.Scale)]
    public void どの設問からでも文字列の列へ割り当てられる(QuestionType type)
    {
        // ⚠️ **文字列はどの設問からでも作れる。** ここを弾くと
        // Title と Body へ何も割り当てられない（Issue #246）
        var definition = Definition("q1") with
        {
            Pages =
            [
                new Page
                {
                    PageId = "p1",
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q1",
                            Type = type,
                            Title = LocalizedText.Japanese("q1"),
                        },
                    ],
                },
            ],
        };

        var problems = MappingValidator.Validate(
            Mapping(ColumnAssignment.Direct("Body", new MappingSource("q1", QuestionPort.Value))),
            definition,
            targetValueKind: _ => MappingTargetValueKind.String);

        Assert.DoesNotContain(
            MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }

    [Fact]
    public void 自由記述は整数の列へは割り当てられない()
    {
        // **数値や日時は変換に失敗し得る。** そちらの検査は効いたままであること
        var problems = MappingValidator.Validate(
            Mapping(ColumnAssignment.Direct("Status", new MappingSource("q1", QuestionPort.Value))),
            Definition("q1"),
            targetValueKind: _ => MappingTargetValueKind.Integer);

        Assert.Contains(
            MappingProblemCode.TargetColumnNeedsCompatibleValue, Codes(problems));
    }
}
