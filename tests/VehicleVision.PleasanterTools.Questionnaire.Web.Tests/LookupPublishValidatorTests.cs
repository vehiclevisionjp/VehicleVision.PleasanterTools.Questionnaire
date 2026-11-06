using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>公開前のルックアップ検証（Issue #879）。偽の資格情報ストア・偽の点検で確かめる。</summary>
public sealed class LookupPublishValidatorTests
{
    private static readonly Guid SurveyId = Guid.NewGuid();

    private sealed class FakeCredentials(LookupCredentialStatus? status) : ILookupCredentialStore
    {
        public Task<LookupCredentialStatus?> FindStatusAsync(Guid s, string id, CancellationToken c = default) =>
            Task.FromResult(status);

        public Task<string> CreateAsync(Guid s, LookupSourceKind k, string secret, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> RotateAsync(Guid s, string id, string secret, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> SetEnabledAsync(Guid s, string id, bool e, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid s, string id, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LookupCredentialStatus>> ListStatusAsync(Guid s, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<LookupSecretResolution> ResolveSecretAsync(Guid s, string id, CancellationToken c = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeInspector(LookupSiteInspection inspection) : ILookupSiteInspectionService
    {
        public int Calls { get; private set; }

        public Task<LookupSiteInspection> InspectAsync(Guid s, string c, long site, CancellationToken t = default)
        {
            Calls++;
            return Task.FromResult(inspection);
        }
    }

    private static LookupCredentialStatus Status(bool enabled = true) =>
        new("cred", LookupSourceKind.Pleasanter, enabled, DateTime.UtcNow, null);

    private static LookupSiteInspection Good() => new()
    {
        Status = LookupSearchStatus.Success,
        Columns =
        [
            new LookupSourceColumn("ClassA", LookupColumnKind.Text),
            new LookupSourceColumn("ClassB", LookupColumnKind.Text),
        ],
    };

    private static SurveyDefinition Survey(bool lookup = true)
    {
        static Question Q(string id) => new()
        {
            QuestionId = id,
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese(id),
        };

        return new SurveyDefinition
        {
            SurveyId = SurveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("t"),
            Pages = [new Page { PageId = "p1", Questions = [Q("a1"), Q("k1"), Q("t1")] }],
            Lookups = lookup
                ?
                [
                    new LookupDefinition
                    {
                        LookupId = "l1",
                        AnchorQuestionId = "a1",
                        SourceSiteId = 5,
                        CredentialId = "cred",
                        Keys = [new LookupKey
                        {
                            QuestionId = "k1",
                            SourceColumn = "ClassA",
                            SourceColumnKind = LookupColumnKind.Text,
                        }],
                        Targets = [new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" }],
                        AllowedColumns = ["ClassB"],
                    },
                ]
                : null,
        };
    }

    private static async Task<string[]> Codes(
        LookupCredentialStatus? status, LookupSiteInspection inspection, bool lookup = true)
    {
        var validator = new LookupPublishValidator(new FakeCredentials(status), new FakeInspector(inspection));
        var problems = await validator.ValidateAsync(SurveyId, Survey(lookup));
        return [.. problems.Select(problem => $"{problem.Kind}:{problem.Code}")];
    }

    [Fact]
    public async Task 正しい定義は不備なし()
    {
        Assert.Empty(await Codes(Status(), Good()));
    }

    [Fact]
    public async Task ルックアップが無ければ外部を呼ばない()
    {
        var inspector = new FakeInspector(Good());
        var validator = new LookupPublishValidator(new FakeCredentials(null), inspector);

        Assert.Empty(await validator.ValidateAsync(SurveyId, Survey(lookup: false)));
        Assert.Equal(0, inspector.Calls);
    }

    [Fact]
    public async Task 資格情報が無い_停止中なら外部を呼ばず拒否する()
    {
        Assert.Contains("credential:CredentialNotFound", await Codes(null, Good()));
        Assert.Contains("credential:CredentialDisabled", await Codes(Status(enabled: false), Good()));
    }

    [Fact]
    public async Task 参照元を点検できなければ有効化しない()
    {
        var failed = new LookupSiteInspection
        {
            Status = LookupSearchStatus.SourceInvalid,
            Failure = LookupSearchFailure.Forbidden,
        };

        Assert.Equal(["source:SourceUnavailable"], await Codes(Status(), failed));
    }

    [Fact]
    public async Task 統合と継承は拒否する()
    {
        var codes = await Codes(Status(), Good() with { IsIntegrated = true, InheritsPermission = true });

        Assert.Contains("source:SourceSiteIntegrated", codes);
        Assert.Contains("source:SourceSiteInheritsPermission", codes);
    }

    [Fact]
    public async Task 列の型の不備と構造の不備を返す()
    {
        var missing = Good() with { Columns = [new LookupSourceColumn("ClassA", LookupColumnKind.Text)] };

        Assert.Contains("column:ColumnNotFound", await Codes(Status(), missing));
    }
}
