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

        public Task<int> SaveAsync(
            Guid surveyId,
            SurveyDefinition definition,
            MappingDefinition mapping,
            int expectedRevision,
            CancellationToken cancellationToken = default,
            long assetHistorySiteId = 0,
            MappingDefinition? assetHistoryMapping = null) =>
            throw new NotSupportedException();

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
