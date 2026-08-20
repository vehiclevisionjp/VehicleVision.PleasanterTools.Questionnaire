using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答数の上限と、受付を止めた理由の試験（Issue #53）。**DB は使わない。**</summary>
public class ResponseLimitTests
{
    private const string PublicId = "pub-1";

    private static readonly Guid SurveyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>アンケートの 1 行を覚えておく代わり。</summary>
    private sealed class FakeSurveys(SurveyRecord survey) : ISurveyRepository
    {
        public SurveyRecord Survey { get; private set; } = survey;

        /// <summary>自動停止を呼んだ回数。**何度も止めに行かないことを見る。**</summary>
        public int SuspendCalls { get; private set; }

        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(Survey.PublicId == publicId ? Survey : null);

        public Task<SurveyRecord?> FindBySurveyIdAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveyRecord?>(Survey.SurveyId == surveyId ? Survey : null);

        public Task PublishAsync(
            Guid surveyId,
            int version,
            SurveyDefinition definition,
            MappingDefinition mapping,
            Guid? publishedBy,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(SurveyRecord record, CancellationToken cancellationToken = default)
        {
            Survey = record;
            return Task.CompletedTask;
        }

        /// <summary>**公開中の行しか止めない**（本物の SQL と同じ条件）。</summary>
        public Task<bool> SuspendForResponseLimitAsync(
            Guid surveyId, CancellationToken cancellationToken = default)
        {
            SuspendCalls++;

            if (Survey.Status != (int)SurveyStatus.Published)
            {
                return Task.FromResult(false);
            }

            Survey = Survey with
            {
                Status = (int)SurveyStatus.Suspended,
                SuspendedReason = (int)SurveySuspendedReason.ResponseLimitReached,
                SuspendedAt = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified),
            };

            return Task.FromResult(true);
        }
    }

    private sealed class FakeSnapshots(SurveySnapshot snapshot) : ISurveySnapshotStore
    {
        public Task<SurveySnapshot?> FindAsync(
            Guid surveyId, int version, CancellationToken cancellationToken = default) =>
            Task.FromResult<SurveySnapshot?>(snapshot);
    }

    /// <summary>送信待ちの代わり。</summary>
    /// <remarks>**他の試験からも使う**ので <c>internal</c>（<c>ProofOfWorkPerSurveyTests</c>）。</remarks>
    internal sealed class FakeOutbox : IResponseOutbox
    {
        private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);

        public Task SaveAsync(
            string responseToken,
            Guid surveyId,
            int surveyVersion,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            _saved[responseToken] = payloadJson;
            return Task.CompletedTask;
        }

        public Task<PendingResponse?> ClaimAsync(
            string lockedBy, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
            Task.FromResult<PendingResponse?>(null);

        public Task CompleteAsync(string responseToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RescheduleAsync(
            string responseToken,
            DateTime nextAttemptAtUtc,
            string? error,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeadLetterAsync(
            string responseToken, string error, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<string?> FindPayloadAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(_saved.GetValueOrDefault(responseToken));

        public Task<int> CountPendingAsync(
            Guid? surveyId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_saved.Count);

        public Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboxStatus(_saved.Count, null, 0, null));

        public Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
            DeadLetterQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterView>>([]);

        public Task<Guid?> RequeueDeadLetterAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }

    /// <summary>トークン対応表の代わり。**受付数はここの行数。**</summary>
    /// <remarks>**他の試験からも使う**ので <c>internal</c>（<c>ProofOfWorkPerSurveyTests</c>）。</remarks>
    internal sealed class FakeTokens : IResponseTokenStore
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);

        /// <summary>数えた回数。**上限が無ければ数えないことを見る。**</summary>
        public int CountCalls { get; private set; }

        /// <summary>既にある回答として登録しておく。</summary>
        public void Seed(params string[] tokens) => _tokens.UnionWith(tokens);

        public Task<long?> FindReferenceIdAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<bool> EnsureAsync(
            string responseToken, Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tokens.Add(responseToken));

        public Task<int> CountAcceptedAsync(
            Guid surveyId, CancellationToken cancellationToken = default)
        {
            CountCalls++;
            return Task.FromResult(_tokens.Count);
        }

        public Task SaveAsync(
            string responseToken,
            Guid surveyId,
            long? referenceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = SurveyId.ToString(),
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
                        QuestionId = "q1",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("感想"),
                    },
                ],
            },
        ],
    };

    private static (ResponseIntake Intake, FakeSurveys Surveys, FakeTokens Tokens) Intake(
        int? responseLimit = null,
        int status = (int)SurveyStatus.Published,
        int? suspendedReason = null,
        params string[] existingTokens)
    {
        var survey = new SurveyRecord(
            SurveyId,
            PublicId,
            "検証用",
            1,
            "DescriptionA",
            status,
            1,
            ResponseLimit: responseLimit,
            SuspendedReason: suspendedReason);

        var surveys = new FakeSurveys(survey);
        var tokens = new FakeTokens();
        tokens.Seed(existingTokens);

        var intake = new ResponseIntake(
            surveys,
            new FakeSnapshots(new SurveySnapshot(
                Definition(), new MappingDefinition(), 1, "DescriptionA")),
            new FakeOutbox(),
            tokens);

        return (intake, surveys, tokens);
    }

    private static Task<IntakeResult> SubmitAsync(ResponseIntake intake, string token) =>
        intake.SubmitAsync(PublicId, token, [Answer.Of("q1", "よかった")]);

    [Fact]
    public async Task 上限に達していなければ受け付ける()
    {
        var (intake, surveys, _) = Intake(responseLimit: 3, existingTokens: ["t1"]);

        Assert.True((await SubmitAsync(intake, "t2")).Accepted);

        // **まだ 2 件目。** 止めない
        Assert.Equal((int)SurveyStatus.Published, surveys.Survey.Status);
    }

    [Fact]
    public async Task 上限に達したら断って自動で停止し理由を残す()
    {
        var (intake, surveys, _) = Intake(responseLimit: 2, existingTokens: ["t1", "t2"]);

        var result = await SubmitAsync(intake, "t3");

        Assert.False(result.Accepted);

        // **回答者には「受付終了」としか伝えない**（上限の有無も到達も見せない）
        Assert.Equal(IntakeRejection.Closed, result.Rejection);

        Assert.Equal((int)SurveyStatus.Suspended, surveys.Survey.Status);
        Assert.Equal(
            (int)SurveySuspendedReason.ResponseLimitReached, surveys.Survey.SuspendedReason);
        Assert.NotNull(surveys.Survey.SuspendedAt);
    }

    [Fact]
    public async Task 上限に届いた回答は受け付けたうえで受付を止める()
    {
        // **最後の 1 件は受け付ける。** 上限は「そこまで受け付ける数」
        var (intake, surveys, _) = Intake(responseLimit: 2, existingTokens: ["t1"]);

        Assert.True((await SubmitAsync(intake, "t2")).Accepted);

        // **次の人が入力し終えてから断られるのを減らす**ため、受け付けた側で止める
        Assert.Equal((int)SurveyStatus.Suspended, surveys.Survey.Status);
        Assert.Equal(
            (int)SurveySuspendedReason.ResponseLimitReached, surveys.Survey.SuspendedReason);
    }

    [Fact]
    public async Task 前の回答の編集では受付数が増えない()
    {
        // **編集は同じトークンで届く。** 数え方を誤ると、上限 2 のアンケートが
        // 1 件の回答を編集しただけで止まる
        var (intake, surveys, _) = Intake(responseLimit: 2, existingTokens: ["t1"]);

        Assert.True((await SubmitAsync(intake, "t1")).Accepted);

        Assert.Equal((int)SurveyStatus.Published, surveys.Survey.Status);
    }

    [Fact]
    public async Task 上限が無ければ数えない()
    {
        // **受け付けのたびに走る処理。** 上限を使っていないアンケートに費用を掛けない
        var (intake, _, tokens) = Intake(responseLimit: null, existingTokens: ["t1"]);

        Assert.True((await SubmitAsync(intake, "t2")).Accepted);

        Assert.Equal(0, tokens.CountCalls);
    }

    [Fact]
    public async Task 上限を数えるのは受け付けあたり1回だけ()
    {
        var (intake, _, tokens) = Intake(responseLimit: 10, existingTokens: ["t1"]);

        Assert.True((await SubmitAsync(intake, "t2")).Accepted);

        // **数え直さない。** 受付前に数えた件数に、受け付けた 1 件を足して判断する
        Assert.Equal(1, tokens.CountCalls);
    }

    [Fact]
    public async Task 上限が0なら上限なしとして扱う()
    {
        // **0 を上限として効かせない。** 公開しているのに誰も回答できない状態を作らない
        var (intake, surveys, tokens) = Intake(responseLimit: 0);

        Assert.True((await SubmitAsync(intake, "t1")).Accepted);

        Assert.Equal(0, tokens.CountCalls);
        Assert.Equal((int)SurveyStatus.Published, surveys.Survey.Status);
    }

    [Fact]
    public async Task 上限で自動停止したアンケートは受付終了として断る()
    {
        // **「停止中」は一時的に見える。** もう再開しないものに使わない
        var (intake, _, _) = Intake(
            responseLimit: 1,
            status: (int)SurveyStatus.Suspended,
            suspendedReason: (int)SurveySuspendedReason.ResponseLimitReached,
            existingTokens: ["t1"]);

        var result = await SubmitAsync(intake, "t2");

        Assert.Equal(IntakeRejection.Closed, result.Rejection);
    }

    [Fact]
    public async Task 手で止めたアンケートは停止中として断る()
    {
        var (intake, _, _) = Intake(
            status: (int)SurveyStatus.Suspended,
            suspendedReason: (int)SurveySuspendedReason.Manual);

        var result = await SubmitAsync(intake, "t1");

        Assert.Equal(IntakeRejection.Suspended, result.Rejection);
    }

    [Fact]
    public async Task 上限に達していたら回答画面の定義も返さない()
    {
        // **最後まで入力させてから断る方が悪い**（_documents/画面設計.md 1 章）
        var (intake, surveys, _) = Intake(responseLimit: 1, existingTokens: ["t1"]);

        var (form, rejection) = await intake.GetPublishedAsync(PublicId);

        Assert.Null(form);
        Assert.Equal(IntakeRejection.Closed, rejection);

        // 開こうとした時点でも止める
        Assert.Equal((int)SurveyStatus.Suspended, surveys.Survey.Status);
    }

    [Fact]
    public async Task 自動停止したあとは止め直しに行かない()
    {
        var (intake, surveys, _) = Intake(responseLimit: 1, existingTokens: ["t1"]);

        Assert.Equal(IntakeRejection.Closed, (await SubmitAsync(intake, "t2")).Rejection);
        Assert.Equal(IntakeRejection.Closed, (await SubmitAsync(intake, "t3")).Rejection);

        // 2 回目は状態の判定が先に断るので、止める処理まで来ない
        Assert.Equal(1, surveys.SuspendCalls);
        Assert.Equal(
            (int)SurveySuspendedReason.ResponseLimitReached, surveys.Survey.SuspendedReason);
    }
}
