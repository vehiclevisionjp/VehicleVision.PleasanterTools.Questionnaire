using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>ルックアップ定義の構造の検証（Issue #807・仕様書 3.2 節）。</summary>
public class LookupValidatorTests
{
    private static Question Q(string id, QuestionType type = QuestionType.Text) => new()
    {
        QuestionId = id,
        Type = type,
        Title = LocalizedText.Japanese(id),
    };

    private static SurveyDefinition Survey(params Question[] extra) => new()
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
                    Q("btn"), Q("k1"), Q("k2"), Q("t1"), Q("t2"),
                    Q("radio", QuestionType.Radio), Q("check", QuestionType.Checkbox),
                    Q("date", QuestionType.Date),
                    .. extra,
                ],
            },
            new Page { PageId = "p2", Questions = [Q("other")] },
        ],
    };

    private static LookupDefinition Valid() => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "btn",
        SourceSiteId = 10,
        CredentialId = "c1",
        Keys = [new LookupKey { QuestionId = "k1", SourceColumn = "ClassA" }],
        Targets = [new LookupTarget { SourceColumn = "ClassB", QuestionId = "t1" }],
        AllowedColumns = ["ClassB"],
    };

    private static LookupProblemCode[] Codes(params LookupDefinition[] lookups) =>
        Codes(Survey(), lookups);

    private static LookupProblemCode[] Codes(
        SurveyDefinition survey, params LookupDefinition[] lookups) =>
        [.. LookupValidator.Validate(survey, lookups).Select(problem => problem.Code)];

    [Fact]
    public void 正しい定義は通る()
    {
        Assert.Empty(Codes(Valid()));
    }

    [Fact]
    public void ルックアップが無ければ何も言わない()
    {
        Assert.Empty(Codes());
    }

    [Fact]
    public void ルックアップIDの重複と空を弾く()
    {
        var second = Valid() with { AnchorQuestionId = "k2" };

        Assert.Contains(LookupProblemCode.LookupIdInvalid, Codes(Valid(), second));
        Assert.Contains(LookupProblemCode.LookupIdInvalid, Codes(Valid() with { LookupId = " " }));
    }

    [Fact]
    public void 日本郵便は未実装として弾く()
    {
        Assert.Contains(
            LookupProblemCode.SourceKindUnsupported,
            Codes(Valid() with { SourceKind = LookupSourceKind.JapanPost }));
    }

    [Fact]
    public void サイトIDと資格情報が無ければ弾く()
    {
        var codes = Codes(Valid() with { SourceSiteId = 0, CredentialId = null });

        Assert.Contains(LookupProblemCode.SourceSiteInvalid, codes);
        Assert.Contains(LookupProblemCode.CredentialMissing, codes);
    }

    [Fact]
    public void 起点が無い設問や重複を弾く()
    {
        Assert.Contains(
            LookupProblemCode.AnchorNotFound, Codes(Valid() with { AnchorQuestionId = "zzz" }));
        Assert.Contains(
            LookupProblemCode.AnchorDuplicated,
            Codes(Valid(), Valid() with { LookupId = "l2" }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void キーの数はNが1から5(int count, bool ok)
    {
        var keys = Enumerable.Range(0, count)
            .Select(i => new LookupKey { QuestionId = $"n{i}", SourceColumn = $"Class{i}" })
            .ToImmutableArray();
        var survey = Survey([.. Enumerable.Range(0, 7).Select(i => Q($"n{i}"))]);

        var codes = Codes(survey, Valid() with { Keys = keys });

        Assert.Equal(ok, !codes.Contains(LookupProblemCode.KeyCountInvalid));
    }

    [Fact]
    public void キーの設問が不在_形式違い_別ページ_重複を弾く()
    {
        LookupDefinition WithKeys(params LookupKey[] keys) =>
            Valid() with { Keys = [.. keys] };

        Assert.Contains(
            LookupProblemCode.KeyQuestionNotFound,
            Codes(WithKeys(new LookupKey { QuestionId = "zzz", SourceColumn = "A" })));
        Assert.Contains(
            LookupProblemCode.KeyQuestionTypeUnsupported,
            Codes(WithKeys(new LookupKey { QuestionId = "radio", SourceColumn = "A" })));
        Assert.Contains(
            LookupProblemCode.KeyPageMismatch,
            Codes(WithKeys(new LookupKey { QuestionId = "other", SourceColumn = "A" })));
        Assert.Contains(
            LookupProblemCode.KeyDuplicated,
            Codes(WithKeys(
                new LookupKey { QuestionId = "k1", SourceColumn = "A" },
                new LookupKey { QuestionId = "k2", SourceColumn = "A" })));
    }

    [Fact]
    public void 前方一致は許可したキーだけ()
    {
        var denied = Valid() with
        {
            Keys =
            [
                new LookupKey
                {
                    QuestionId = "k1", SourceColumn = "A", MatchMode = LookupMatchMode.Prefix,
                },
            ],
        };
        var allowed = Valid() with
        {
            Keys =
            [
                new LookupKey
                {
                    QuestionId = "k1",
                    SourceColumn = "A",
                    MatchMode = LookupMatchMode.Prefix,
                    AllowPrefix = true,
                },
            ],
        };

        Assert.Contains(LookupProblemCode.PrefixNotAllowed, Codes(denied));
        Assert.Empty(Codes(allowed));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void 入力先の数はMが1から10(int count, bool ok)
    {
        var survey = Survey([.. Enumerable.Range(0, 11).Select(i => Q($"m{i}"))]);
        var targets = Enumerable.Range(0, count)
            .Select(i => new LookupTarget { SourceColumn = $"C{i}", QuestionId = $"m{i}" })
            .ToImmutableArray();
        var columns = Enumerable.Range(0, Math.Max(count, 1)).Select(i => $"C{i}").ToImmutableArray();

        var codes = Codes(survey, Valid() with { Targets = targets, AllowedColumns = columns });

        Assert.Equal(ok, !codes.Contains(LookupProblemCode.TargetCountInvalid));
    }

    [Fact]
    public void 入力先の不在_複数選択_日付_別ページ_競合を弾く()
    {
        LookupProblemCode[] With(params LookupTarget[] targets) =>
            Codes(Valid() with { Targets = [.. targets], AllowedColumns = ["A", "B"] });

        Assert.Contains(
            LookupProblemCode.TargetQuestionNotFound,
            With(new LookupTarget { SourceColumn = "A", QuestionId = "zzz" }));
        Assert.Contains(
            LookupProblemCode.TargetQuestionTypeUnsupported,
            With(new LookupTarget { SourceColumn = "A", QuestionId = "check" }));
        Assert.Contains(
            LookupProblemCode.TargetQuestionTypeUnsupported,
            With(new LookupTarget { SourceColumn = "A", QuestionId = "date" }));
        Assert.Contains(
            LookupProblemCode.TargetPageMismatch,
            With(new LookupTarget { SourceColumn = "A", QuestionId = "other" }));
        Assert.Contains(
            LookupProblemCode.TargetConflict,
            With(
                new LookupTarget { SourceColumn = "A", QuestionId = "t1" },
                new LookupTarget { SourceColumn = "B", QuestionId = "t1" }));
    }

    [Fact]
    public void 単一選択は入力先にできる()
    {
        var lookup = Valid() with
        {
            Targets = [new LookupTarget { SourceColumn = "ClassB", QuestionId = "radio" }],
        };

        Assert.Empty(Codes(lookup));
    }

    [Fact]
    public void 許可列の不備を弾く()
    {
        Assert.Contains(
            LookupProblemCode.AllowedColumnsInvalid, Codes(Valid() with { AllowedColumns = [] }));
        Assert.Contains(
            LookupProblemCode.TargetColumnNotAllowed,
            Codes(Valid() with { AllowedColumns = ["ClassZ"] }));
        Assert.Contains(
            LookupProblemCode.DisplayColumnNotAllowed,
            Codes(Valid() with { DisplayColumns = ["ClassQ"] }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void 最大候補数は1からシステム上限(int max, bool ok)
    {
        var codes = Codes(Valid() with { MaxResults = max });

        Assert.Equal(ok, !codes.Contains(LookupProblemCode.MaxResultsInvalid));
    }
}
