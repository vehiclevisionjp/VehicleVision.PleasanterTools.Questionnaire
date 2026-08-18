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
}
