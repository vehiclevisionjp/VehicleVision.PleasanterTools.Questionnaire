using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>テスト公開に反映する操作が、失敗しても公開状態を外さないこと（Issue #1008）。</summary>
/// <remarks>**DB へは繋がない。** 状態の遷移だけを、記録するだけの偽物で確かめる。</remarks>
public class AdminTestRepublishTests
{
    private const long SiteId = 10;

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 2,
        Title = LocalizedText.Japanese("満足度調査"),
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
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                ],
            },
        ],
    };

    private static SurveyRecord Record(SurveyStatus status) =>
        new(Guid.NewGuid(), "pub", "題名", SiteId, null, (int)status, PublishedVersion: 1);

    private static Task<IResult> RunAsync(
        FakeRepository surveys,
        FakeDrafts drafts,
        bool republish) =>
        AdminSurveyEndpoints.TestPublishAsync(
            surveys.Record!.SurveyId,
            new DefaultHttpContext(),
            drafts,
            surveys,
            new LookupPublishValidator(null!, null!),
            republish,
            CancellationToken.None);

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Fact]
    public async Task 割り当ての誤りで拒否されてもテスト公開のまま残る()
    {
        // 履歴の投射先が回答先と同じ（公開を止める不備）。再現した現象:
        // 画面は下書きへ戻してからテスト公開を呼んでいたため、拒否されると下書きに落ちた
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(
            Definition(), new MappingDefinition(), 3, AssetHistorySiteId: SiteId));

        var result = await RunAsync(surveys, drafts, republish: true);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
        Assert.Equal(1, surveys.Record.PublishedVersion);
        Assert.Empty(surveys.Published);
        Assert.Equal(0, surveys.SaveCount);
    }

    [Fact]
    public async Task 版を固める段で衝突してもテスト公開のまま残る()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished))
        {
            PublishThrows = true,
        };
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await RunAsync(surveys, drafts, republish: true);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
        Assert.Equal(1, surveys.Record.PublishedVersion);
    }

    [Fact]
    public async Task 反映できたら次の版がテスト公開される()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await RunAsync(surveys, drafts, republish: true);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal([2], surveys.Published);
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
    }

    [Fact]
    public async Task 反映は下書きのアンケートでは受け付けない()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.Draft));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await RunAsync(surveys, drafts, republish: true);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Empty(surveys.Published);
    }

    [Fact]
    public async Task 通常のテスト公開は下書きからだけ受け付ける()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await RunAsync(surveys, drafts, republish: false);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));

        var fresh = new FakeRepository(Record(SurveyStatus.Draft));
        var ok = await RunAsync(fresh, drafts, republish: false);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(ok));
        Assert.Equal((int)SurveyStatus.TestPublished, fresh.Record!.Status);
    }

    private static Task<IResult> PublishAsync(
        FakeRepository surveys,
        FakeDrafts drafts,
        bool? reflectDraft = null) =>
        AdminSurveyEndpoints.PublishAsync(
            surveys.Record!.SurveyId,
            reflectDraft is null ? null : new AdminSurveyEndpoints.PublishRequest(reflectDraft.Value),
            new DefaultHttpContext(),
            drafts,
            surveys,
            new LookupPublishValidator(null!, null!),
            CancellationToken.None);

    [Fact]
    public async Task 下書きからその場で版を固めて本公開できる()
    {
        // Issue #1057。テスト公開を経由しない
        var surveys = new FakeRepository(Record(SurveyStatus.Draft));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await PublishAsync(surveys, drafts);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal([2], surveys.Published);
        Assert.Equal((int)SurveyStatus.Published, surveys.Record!.Status);
        Assert.Equal(2, surveys.Record.PublishedVersion);
    }

    [Fact]
    public async Task 下書きからの本公開にも公開前の検査を掛ける()
    {
        // 履歴の投射先が回答先と同じ（公開を止める不備）。固めず、下書きのまま残る
        var surveys = new FakeRepository(Record(SurveyStatus.Draft));
        var drafts = new FakeDrafts(new SurveyDraft(
            Definition(), new MappingDefinition(), 3, AssetHistorySiteId: SiteId));

        var result = await PublishAsync(surveys, drafts);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Empty(surveys.Published);
        Assert.Equal((int)SurveyStatus.Draft, surveys.Record!.Status);
        Assert.Equal(0, surveys.SaveCount);
    }

    [Fact]
    public async Task テスト公開中の本公開は固めた版をそのまま使う()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await PublishAsync(surveys, drafts);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Empty(surveys.Published);
        Assert.Equal((int)SurveyStatus.Published, surveys.Record!.Status);
        Assert.Equal(1, surveys.Record.PublishedVersion);

        // 本文が `reflectDraft: false` でも同じ
        var explicitFalse = new FakeRepository(Record(SurveyStatus.TestPublished));
        await PublishAsync(explicitFalse, drafts, reflectDraft: false);
        Assert.Empty(explicitFalse.Published);
    }

    [Fact]
    public async Task テスト公開中でも下書きを反映して本公開できる()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await PublishAsync(surveys, drafts, reflectDraft: true);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal([2], surveys.Published);
        Assert.Equal((int)SurveyStatus.Published, surveys.Record!.Status);
        Assert.Equal(2, surveys.Record.PublishedVersion);
    }

    [Fact]
    public async Task 反映して本公開が検査で断られたらテスト公開のまま残る()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(
            Definition(), new MappingDefinition(), 3, AssetHistorySiteId: SiteId));

        var result = await PublishAsync(surveys, drafts, reflectDraft: true);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
        Assert.Equal(1, surveys.Record.PublishedVersion);
        Assert.Empty(surveys.Published);
    }

    [Theory]
    [InlineData(SurveyStatus.Published)]
    [InlineData(SurveyStatus.Suspended)]
    public async Task 本公開中と停止中からは本公開できない(SurveyStatus status)
    {
        var surveys = new FakeRepository(Record(status));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await PublishAsync(surveys, drafts, reflectDraft: true);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Empty(surveys.Published);
        Assert.Equal(0, surveys.SaveCount);
    }

    [Fact]
    public async Task アーカイブ済みは本公開できない()
    {
        var surveys = new FakeRepository(
            Record(SurveyStatus.Draft) with { ArchivedAt = DateTime.UtcNow });
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await PublishAsync(surveys, drafts);

        Assert.NotEqual(StatusCodes.Status200OK, StatusOf(result));
        Assert.Empty(surveys.Published);
    }

    private static HttpContext AdminContext(string role = "Administrator")
    {
        var context = new DefaultHttpContext();
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)],
                "test"));
        return context;
    }

    private static Task<IResult> SaveAsync(
        FakeRepository surveys,
        FakeDrafts drafts,
        SurveyDefinition definition,
        long assetHistorySiteId = 0,
        string role = "Administrator") =>
        AdminSurveyEndpoints.SaveDraftCoreAsync(
            surveys.Record!.SurveyId,
            new AdminSurveyEndpoints.SaveDraftRequest(
                definition, new MappingDefinition(), 3, assetHistorySiteId),
            surveys.Record,
            AdminContext(role),
            drafts,
            surveys,
            new LookupPublishValidator(null!, null!),
            CancellationToken.None);

    [Fact]
    public async Task テスト公開中の保存は自動で次の版を固める()
    {
        // Issue #1057。保存のたびに版を固め直す。URL で見える内容＝最後に保存した内容
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await SaveAsync(surveys, drafts, Definition());

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(1, drafts.SaveCount);
        Assert.Equal([2], surveys.Published);
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
    }

    [Fact]
    public async Task テスト公開中の保存は版へ反映できないなら保存もしない()
    {
        // 履歴の投射先が回答先と同じ（公開を止める不備）。**片方だけ進んで食い違わない**
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await SaveAsync(surveys, drafts, Definition(), assetHistorySiteId: SiteId);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(0, drafts.SaveCount);
        Assert.Empty(surveys.Published);
    }

    [Fact]
    public async Task 保存できても版を固める段で衝突したら_保存済みと未反映を伝えて状態は変えない()
    {
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished)) { PublishThrows = true };
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await SaveAsync(surveys, drafts, Definition());

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(1, drafts.SaveCount);
        Assert.Equal((int)SurveyStatus.TestPublished, surveys.Record!.Status);
        Assert.Equal(1, surveys.Record.PublishedVersion);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(((dynamic)result).Value);
        Assert.False(body.GetProperty("reflected").GetBoolean());
    }

    [Theory]
    [InlineData(SurveyStatus.Draft)]
    [InlineData(SurveyStatus.Published)]
    [InlineData(SurveyStatus.Suspended)]
    public async Task テスト公開中以外の保存は版を固めない(SurveyStatus status)
    {
        var surveys = new FakeRepository(Record(status));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await SaveAsync(surveys, drafts, Definition());

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(1, drafts.SaveCount);
        Assert.Empty(surveys.Published);
    }

    [Fact]
    public async Task 公開の権限が無い呼び出しの保存は版を固めない()
    {
        // 編集者（surveys.write のみ）。保存はできるが、版の固めは公開の権限
        var surveys = new FakeRepository(Record(SurveyStatus.TestPublished));
        var drafts = new FakeDrafts(new SurveyDraft(Definition(), new MappingDefinition(), 3));

        var result = await SaveAsync(surveys, drafts, Definition(), role: "Editor");

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(1, drafts.SaveCount);
        Assert.Empty(surveys.Published);
    }

    private sealed class FakeRepository(SurveyRecord record) : ISurveyRepository
    {
        public SurveyRecord? Record { get; private set; } = record;

        public List<int> Published { get; } = [];

        public int SaveCount { get; private set; }

        public bool PublishThrows { get; init; }

        public Task SaveAsync(SurveyRecord survey, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Record = survey;
            return Task.CompletedTask;
        }

        public Task PublishAsync(
            Guid surveyId,
            int version,
            SurveyDefinition definition,
            MappingDefinition mapping,
            Guid? publishedBy,
            CancellationToken cancellationToken = default)
        {
            if (PublishThrows)
            {
                throw new InvalidOperationException("既に公開されている");
            }

            Published.Add(version);
            Record = Record! with { PublishedVersion = version };
            return Task.CompletedTask;
        }

        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<SurveyRecord?> FindBySurveyIdAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record);

        public Task<bool> SuspendForResponseLimitAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PleasanterSiteUpdateResult> UpdatePleasanterSiteIdAsync(
            Guid surveyId, long pleasanterSiteId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDrafts(SurveyDraft draft) : ISurveyDraftStore
    {
        public Task<SurveyDraft?> LoadAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyDraft?>(draft);

        public Task<IReadOnlyList<SurveySummary>> ListAsync(
            SurveyListQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int SaveCount { get; private set; }

        public Task<int> SaveAsync(
            Guid surveyId,
            SurveyDefinition definition,
            MappingDefinition mapping,
            int expectedRevision,
            CancellationToken cancellationToken = default,
            long assetHistorySiteId = 0,
            MappingDefinition? assetHistoryMapping = null)
        {
            SaveCount++;
            return Task.FromResult(expectedRevision + 1);
        }

        public Task<bool> DuplicateAsync(
            Guid sourceSurveyId,
            SurveyDuplicationTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SurveyTemplateSummary>> ListTemplatesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SaveAsTemplateAsync(
            Guid sourceSurveyId,
            SurveyTemplateTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CreateFromTemplateAsync(
            Guid templateId,
            SurveyDuplicationTarget target,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteTemplateAsync(
            Guid templateId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
