using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Mapping;

/// <summary>添付の割り当て（設問 → 添付列）。</summary>
/// <remarks>
/// **形は <c>1 : 0 : 1</c> に限る**（2026-08-18 決定。変換は掛けない）。
/// </remarks>
public class AttachmentMappingTests
{
    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("添付"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "qf",
                        Type = QuestionType.File,
                        Title = LocalizedText.Japanese("資料"),
                    },
                    new Question
                    {
                        QuestionId = "qt",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                ],
            },
        ],
    };

    private static ColumnAssignment Attachment(string column, string questionId) => new()
    {
        TargetColumn = column,
        Sources = [new MappingSource(questionId, QuestionPort.Files)],
        Converter = null,
    };

    /// <summary>添付列の判定。**Pleasanter 側の決まりに合わせた最小の代役。**</summary>
    private static bool IsAttachmentColumn(string column) =>
        column.StartsWith("Attachments", StringComparison.Ordinal);

    private static ImmutableArray<MappingProblem> Validate(MappingDefinition mapping) =>
        MappingValidator.Validate(mapping, Definition(), null, IsAttachmentColumn);

    // ---- 評価 ---------------------------------------------------------------

    [Fact]
    public void 添付の割り当ては値の列に混ざらない()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                Attachment("AttachmentsA", "qf"),
                ColumnAssignment.Direct("ClassA", new MappingSource("qt")),
            ],
        };

        var result = new MappingEvaluator().Evaluate(
            mapping,
            [Answer.Of("qt", "満足"), new Answer("qf", []) { FileNames = ["a.png"] }]);

        Assert.Empty(result.Problems);

        // **Base64 を値の流れに乗せない。** 乗せると他の列と同じ変換の対象になる
        Assert.False(result.Columns.ContainsKey("AttachmentsA"));
        Assert.Equal(["ClassA"], result.Columns.Keys.ToArray());

        Assert.Equal("qf", result.AttachmentColumns["AttachmentsA"]);
    }

    [Fact]
    public void 添付に変換を挟むと実行時に弾く()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                new ColumnAssignment
                {
                    TargetColumn = "AttachmentsA",
                    Sources = [new MappingSource("qf", QuestionPort.Files)],
                    Converter = MappingConverter.Of("join", ("separator", "、")),
                },
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [new Answer("qf", [])]);

        Assert.Contains(result.Problems, problem => problem.TargetColumn == "AttachmentsA");
        Assert.Empty(result.AttachmentColumns);
    }

    [Fact]
    public void 添付の入力を二つにすると実行時に弾く()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                new ColumnAssignment
                {
                    TargetColumn = "AttachmentsA",
                    Sources = [new MappingSource("qf", QuestionPort.Files), new MappingSource("qt")],
                    Converter = null,
                },
            ],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [new Answer("qf", [])]);

        Assert.NotEmpty(result.Problems);
        Assert.Empty(result.AttachmentColumns);
    }

    [Fact]
    public void 同じ添付列への割り当てが二つあれば弾く()
    {
        var mapping = new MappingDefinition
        {
            Assignments = [Attachment("AttachmentsA", "qf"), Attachment("AttachmentsA", "qf")],
        };

        var result = new MappingEvaluator().Evaluate(mapping, [new Answer("qf", [])]);

        Assert.NotEmpty(result.Problems);
        Assert.Single(result.AttachmentColumns);
    }

    // ---- 保存前の検査 -------------------------------------------------------

    [Fact]
    public void 正しい形なら不備は出ない()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                Attachment("AttachmentsA", "qf"),
                ColumnAssignment.Direct("ClassA", new MappingSource("qt")),
            ],
        };

        Assert.Empty(Validate(mapping).Where(problem => problem.IsBlocking));
    }

    [Fact]
    public void 添付に変換を付けたら不備になる()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                new ColumnAssignment
                {
                    TargetColumn = "AttachmentsA",
                    Sources = [new MappingSource("qf", QuestionPort.Files)],
                    Converter = MappingConverter.Of("join"),
                },
            ],
        };

        Assert.Contains(
            Validate(mapping),
            problem => problem.Code is MappingProblemCode.InvalidAttachmentShape);
    }

    [Fact]
    public void 添付でない設問を添付の口へ繋いだら不備になる()
    {
        var mapping = new MappingDefinition { Assignments = [Attachment("AttachmentsA", "qt")] };

        Assert.Contains(
            Validate(mapping),
            problem => problem.Code is MappingProblemCode.NonFileQuestionAsAttachment);
    }

    [Fact]
    public void 添付列に普通の口を繋いだら不備になる()
    {
        // 名前だけを添付列へ入れても、ファイルとしては取り出せない
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct(
                    "AttachmentsA", new MappingSource("qf", QuestionPort.FileNames)),
            ],
        };

        Assert.Contains(
            Validate(mapping),
            problem => problem.Code is MappingProblemCode.AttachmentColumnNeedsFilePort);
    }

    [Fact]
    public void 添付の口を添付列以外へ繋いだら不備になる()
    {
        // 中身は Base64。**入れると列が本文で埋まる**
        var mapping = new MappingDefinition { Assignments = [Attachment("ClassA", "qf")] };

        Assert.Contains(
            Validate(mapping),
            problem => problem.Code is MappingProblemCode.FilePortNeedsAttachmentColumn);
    }

    [Fact]
    public void 添付の設問も割り当てれば未割り当ての警告は出ない()
    {
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                Attachment("AttachmentsA", "qf"),
                ColumnAssignment.Direct("ClassA", new MappingSource("qt")),
            ],
        };

        Assert.DoesNotContain(
            Validate(mapping),
            problem => problem.Code is MappingProblemCode.UnmappedQuestion);
    }
}
