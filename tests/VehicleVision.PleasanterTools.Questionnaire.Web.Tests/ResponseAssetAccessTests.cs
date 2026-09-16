using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>公開版での資産の用途と、受付終了・アーカイブの扱い（Issue #318）。</summary>
public class ResponseAssetAccessTests
{
    private static readonly Guid SurveyId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AssetId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task 完了画面だけの配布物は引換券を要求する()
    {
        var asset = await Intake(Definition(completion: true, question: false))
            .GetPublishedAssetAsync("pub-1", AssetId);

        Assert.True(asset!.RequiresTicket);
    }

    [Fact]
    public async Task 設問の画像は引換券を要求しない()
    {
        var asset = await Intake(Definition(completion: false, question: true))
            .GetPublishedAssetAsync("pub-1", AssetId);

        Assert.False(asset!.RequiresTicket);
    }

    [Fact]
    public async Task 受付終了後も公開版の配布物を特定できる()
    {
        var survey = Record() with { AcceptTo = DateTime.UtcNow.AddDays(-1) };
        var asset = await Intake(Definition(completion: true, question: false), survey)
            .GetPublishedAssetAsync("pub-1", AssetId);

        Assert.NotNull(asset);
    }

    [Fact]
    public async Task アーカイブ後は配布物を返さない()
    {
        var survey = Record() with { ArchivedAt = DateTime.UtcNow };
        var asset = await Intake(Definition(completion: true, question: false), survey)
            .GetPublishedAssetAsync("pub-1", AssetId);

        Assert.Null(asset);
    }

    [Fact]
    public async Task 回答受付で配布物の引換券を発行する()
    {
        var now = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        var tickets = new FakeAssetTickets();
        var definition = Definition(completion: true, question: false) with
        {
            AssetDelivery = new AssetDeliverySettings
            {
                Expiration = AssetTicketExpiration.DaysAfterResponse,
                Days = 30,
            },
        };
        var intake = new ResponseIntake(
            new FakeSurveys(Record() with { AcceptTo = now.UtcDateTime.AddDays(1) }),
            new FakeSnapshots(new SurveySnapshot(definition, new MappingDefinition(), 1, null)),
            new ResponseLimitTests.FakeOutbox(),
            new ResponseLimitTests.FakeTokens(),
            timeProvider: new FixedTimeProvider(now),
            assets: new FakeAssets(),
            assetTickets: tickets);

        var result = await intake.SubmitAsync("pub-1", "response-1", []);

        Assert.True(result.Accepted);
        Assert.NotNull(result.AssetTicket);
        Assert.Equal(now.UtcDateTime.AddDays(30), tickets.ExpiresAtUtc);
        Assert.NotEqual(result.AssetTicket, tickets.TicketHash);
    }

    private static ResponseIntake Intake(
        SurveyDefinition definition,
        SurveyRecord? survey = null) =>
        new(
            new FakeSurveys(survey ?? Record()),
            new FakeSnapshots(new SurveySnapshot(definition, new MappingDefinition(), 1, null)),
            null!,
            null!,
            assets: new FakeAssets());

    private static SurveyRecord Record() =>
        new(
            SurveyId,
            "pub-1",
            "資産配布の検証",
            1,
            null,
            (int)SurveyStatus.Published,
            1);

    private static SurveyDefinition Definition(bool completion, bool question) => new()
    {
        SurveyId = SurveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("資産配布の検証"),
        ConfirmationMessage = completion
            ? LocalizedText.Japanese($"[資料](asset:{AssetId:D})")
            : null,
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions = question
                    ?
                    [
                        new Question
                        {
                            QuestionId = "note",
                            Type = QuestionType.Note,
                            Title = LocalizedText.Japanese("説明"),
                            Description = LocalizedText.Japanese($"![画像](asset:{AssetId:D})"),
                        },
                    ]
                    : [],
            },
        ],
    };

    private sealed class FakeSurveys(SurveyRecord survey) : ISurveyRepository
    {
        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(publicId == survey.PublicId ? survey : null);

        public Task<SurveyRecord?> FindBySurveyIdAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(surveyId == survey.SurveyId ? survey : null);

        public Task SaveAsync(
            SurveyRecord value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PublishAsync(
            Guid surveyId,
            int version,
            SurveyDefinition definition,
            MappingDefinition mapping,
            Guid? publishedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SuspendForResponseLimitAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PleasanterSiteUpdateResult> UpdatePleasanterSiteIdAsync(
            Guid surveyId,
            long pleasanterSiteId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSnapshots(SurveySnapshot snapshot) : ISurveySnapshotStore
    {
        public Task<SurveySnapshot?> FindAsync(
            Guid surveyId,
            int version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveySnapshot?>(snapshot);
    }

    private sealed class FakeAssets : ISurveyAssetStore
    {
        public Task<SurveyAsset?> FindAsync(
            Guid surveyId,
            Guid assetId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyAsset?>(
                surveyId == SurveyId && assetId == AssetId
                    ? new SurveyAsset(assetId, "image/png", "asset.png", [0x89, 0x50])
                    : null);

        public Task<Guid> AddAsync(
            Guid surveyId,
            string contentType,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> TryAddContentAsync(
            Guid surveyId,
            string contentType,
            string fileName,
            byte[] content,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSurveyAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAssetTickets : IAssetTicketStore
    {
        public string? TicketHash { get; private set; }

        public DateTime? ExpiresAtUtc { get; private set; }

        public Task SaveAsync(
            string ticketHash,
            string responseToken,
            Guid surveyId,
            DateTime expiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            TicketHash = ticketHash;
            ExpiresAtUtc = expiresAtUtc;
            return Task.CompletedTask;
        }

        public Task<AssetTicketGrant?> RedeemAsync(
            string ticketHash,
            Guid surveyId,
            DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RevokeBySurveyAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredAsync(
            DateTime threshold,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
