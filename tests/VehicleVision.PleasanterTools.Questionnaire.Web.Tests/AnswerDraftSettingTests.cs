using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>下書きの可否が回答画面まで伝わること（Issue #59）。**DB は使わない。**</summary>
/// <remarks>
/// <para>
/// **下書きはサーバへ送らない。** ここで確かめるのは「置いてよいか」を伝える経路だけ。
/// 端末での保存・再開そのものは、ブラウザで動かして確かめる
/// （<c>tools/screenshots/specs/draft.spec.ts</c>）。
/// </para>
/// <para>
/// ⚠️ **既定は無効。** 端末は共有され得るので、
/// 判断が付かないときは残さない側へ倒す。
/// </para>
/// </remarks>
public class AnswerDraftSettingTests
{
    private const string PublicId = "pub-draft";

    private static readonly Guid SurveyId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private sealed class FakeSurveys(SurveyRecord survey) : ISurveyRepository
    {
        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(survey.PublicId == publicId ? survey : null);

        public Task<SurveyRecord?> FindBySurveyIdAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(survey.SurveyId == surveyId ? survey : null);

        public Task PublishAsync(
            Guid surveyId,
            int version,
            SurveyDefinition definition,
            MappingDefinition mapping,
            Guid? publishedBy,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(SurveyRecord record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SuspendForResponseLimitAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeSnapshots(SurveySnapshot snapshot) : ISurveySnapshotStore
    {
        public Task<SurveySnapshot?> FindAsync(
            Guid surveyId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveySnapshot?>(snapshot);
    }

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("下書きの検証"),
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

    private static ResponseIntake Intake(bool allowDraft)
    {
        var survey = new SurveyRecord(
            SurveyId, PublicId, "下書きの検証", 1, "DescriptionA",
            (int)SurveyStatus.Published, 1)
        {
            AllowDraft = allowDraft,
        };

        return new ResponseIntake(
            new FakeSurveys(survey),
            new FakeSnapshots(new SurveySnapshot(
                Definition(), new MappingDefinition(), 1, "DescriptionA")),
            new NullOutbox(),
            new NullTokens());
    }

    [Fact]
    public async Task 入れてあれば下書きを許すと伝える()
    {
        var (form, rejection) = await Intake(allowDraft: true).GetPublishedAsync(PublicId);

        Assert.Null(rejection);
        Assert.True(form!.AllowsDraft);
    }

    [Fact]
    public async Task 既定では下書きを許さない()
    {
        // **端末は共有され得る。** 黙って残さない
        var (form, _) = await Intake(allowDraft: false).GetPublishedAsync(PublicId);

        Assert.False(form!.AllowsDraft);
    }

    [Fact]
    public void 記録の既定値は残さない側()
    {
        // **移行しただけで残し始めない**（列の既定値と揃っていること）
        var record = new SurveyRecord(
            SurveyId, PublicId, "既定", 1, null, (int)SurveyStatus.Published, 1);

        Assert.False(record.AllowDraft);
    }

    /// <summary>使わない口。**定義を読むだけの試験なので呼ばれない。**</summary>
    private sealed class NullOutbox : IResponseOutbox
    {
        public Task SaveAsync(
            string responseToken, Guid surveyId, int surveyVersion, string payloadJson,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PendingResponse?> ClaimAsync(
            string lockedBy, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RescheduleAsync(
            string responseToken, DateTime nextAttemptAtUtc, string? error,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeadLetterAsync(
            string responseToken, string error, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> FindPayloadAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountPendingAsync(
            Guid? surveyId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PendingBacklog> CountBacklogAsync(
            int perSurveyAtLeast, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
            DeadLetterQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> RequeueDeadLetterAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NullTokens : IResponseTokenStore
    {
        public Task<bool> EnsureAsync(
            string responseToken, Guid surveyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long?> FindReferenceIdAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(
            string responseToken, Guid surveyId, long? referenceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CountAcceptedAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
