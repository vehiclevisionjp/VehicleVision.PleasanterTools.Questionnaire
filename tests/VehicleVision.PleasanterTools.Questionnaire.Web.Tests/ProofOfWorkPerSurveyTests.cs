using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// proof-of-work をアンケートごとに切り替える（Issue #66）。**DB は使わない。**
/// </summary>
/// <remarks>
/// **ここで見たいのは判定の根拠。**
/// 回答画面へ返す旗は待たせないための知らせでしかなく、
/// **受け付ける側は必ず DB の行を見る。**
/// アンケートが無いときに「要らない」と答えると、
/// 解答を付けずに投げるだけで公開 ID の実在が分かってしまう。
/// </remarks>
public class ProofOfWorkPerSurveyTests
{
    private const string PublicId = "pub-pow";

    private static readonly Guid SurveyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>アンケートの 1 行を覚えておく代わり。**引けるのは 1 件だけ。**</summary>
    private sealed class FakeSurveys(SurveyRecord survey) : ISurveyRepository
    {
        /// <summary>行を読んだ回数。**受付側が DB を見ていることを見る。**</summary>
        public int Reads { get; private set; }

        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<SurveyRecord?>(survey.PublicId == publicId ? survey : null);
        }

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

    private static (ResponseIntake Intake, FakeSurveys Surveys) Intake(
        bool? requireProofOfWork = null)
    {
        // **旗を渡さない経路も試す。** 既定が有効でなければ、
        // 移行しただけで既存のアンケートの守りが緩む
        var survey = requireProofOfWork is { } flag
            ? new SurveyRecord(
                SurveyId, PublicId, "検証用", 1, null, (int)SurveyStatus.Published, 1,
                RequireProofOfWork: flag)
            : new SurveyRecord(
                SurveyId, PublicId, "検証用", 1, null, (int)SurveyStatus.Published, 1);

        var surveys = new FakeSurveys(survey);

        var intake = new ResponseIntake(
            surveys,
            new FakeSnapshots(new SurveySnapshot(Definition(), new MappingDefinition(), 1, null)),
            new ResponseLimitTests.FakeOutbox(),
            new ResponseLimitTests.FakeTokens());

        return (intake, surveys);
    }

    [Fact]
    public async Task 旗を指定しなければ要る()
    {
        // **既定は有効**（Issue #66）。公開の窓口に置かれることを前提にする
        var (intake, _) = Intake();

        var (form, rejection) = await intake.GetPublishedAsync(PublicId);

        Assert.Null(rejection);
        Assert.NotNull(form);
        Assert.True(form.RequiresProofOfWork);
    }

    [Fact]
    public async Task 切ったアンケートは要らないと伝える()
    {
        var (intake, _) = Intake(requireProofOfWork: false);

        var (form, _) = await intake.GetPublishedAsync(PublicId);

        Assert.NotNull(form);
        Assert.False(form.RequiresProofOfWork);

        // **定義そのものは変わらない。** 切っても回答画面は同じものを出す
        Assert.NotNull(form.Definition.FindQuestion("q1"));
    }

    [Fact]
    public async Task 受付側の判定はDBの旗を見る()
    {
        var (intake, surveys) = Intake(requireProofOfWork: false);

        Assert.False(await intake.RequiresProofOfWorkAsync(PublicId));

        // **画面の言い分ではなく行を読んでいる**ことを見る
        Assert.Equal(1, surveys.Reads);
    }

    [Fact]
    public async Task 有効なアンケートでは受付側も要ると答える()
    {
        var (intake, _) = Intake(requireProofOfWork: true);

        Assert.True(await intake.RequiresProofOfWorkAsync(PublicId));
    }

    [Fact]
    public async Task 実在しない公開IDには要ると答える()
    {
        // **ここが肝。** 「無いから要らない」にすると、解答を付けずに投げるだけで
        // 公開 ID の実在が応答から分かる（_documents/非機能設計.md 1 章「識別子の秘匿」）。
        // **切ってあるアンケートと同じ扱いにしない**
        var (intake, _) = Intake(requireProofOfWork: false);

        Assert.True(await intake.RequiresProofOfWorkAsync("pub-not-exists"));
    }
}
