using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>添付を受け取る入口の試験。**DB は使わない。**</summary>
public class ResponseIntakeAttachmentTests
{
    private const string PublicId = "pub-1";
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly Guid SurveyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly byte[] PngHeader =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private static AttachmentPolicy Policy(bool scanEnabled = false) =>
        AttachmentPolicy.Create(
            allowedExtensions: ["png", "pdf"],
            maxFileSizeBytes: 1024,
            maxFileCount: 3,
            virusScanEnabled: scanEnabled,
            maxTotalBytes: 2048);

    private sealed class FakeSurveys(SurveyRecord? survey) : ISurveyRepository
    {
        public Task<SurveyRecord?> FindByPublicIdAsync(
            string publicId, CancellationToken cancellationToken = default) =>
            Task.FromResult(survey);

        public Task<SurveyRecord?> FindBySurveyIdAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(survey);

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

    private sealed class FakeOutbox : IResponseOutbox
    {
        public string? SavedPayload { get; private set; }

        public Task SaveAsync(
            string responseToken,
            Guid surveyId,
            int surveyVersion,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            SavedPayload = payloadJson;
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
            Task.FromResult(SavedPayload);

        public Task<int> CountPendingAsync(
            Guid? surveyId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(SavedPayload is null ? 0 : 1);

        // **滞留の見張りはここでは動かさない**（Issue #72）。
        // 見張りを渡していない試験なので呼ばれない
        public Task<PendingBacklog> CountBacklogAsync(
            int perSurveyAtLeast, CancellationToken cancellationToken = default) =>
            Task.FromResult(PendingBacklog.Empty with { Total = SavedPayload is null ? 0 : 1 });

        // ---- 管理画面から読む口。**受付の試験では使わない** --------------------

        public Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboxStatus(SavedPayload is null ? 0 : 1, null, 0, null));

        public Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
            DeadLetterQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterView>>([]);

        public Task<Guid?> RequeueDeadLetterAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class FakeTokens : IResponseTokenStore
    {
        public Task<long?> FindReferenceIdAsync(
            string responseToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<bool> EnsureAsync(
            string responseToken, Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> CountAcceptedAsync(
            Guid surveyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task SaveAsync(
            string responseToken,
            Guid surveyId,
            long? referenceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnavailableScanner : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
            throw new VirusScannerUnavailableException("到達できない");
    }

    private sealed class InfectedScanner : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken ct) =>
            Task.FromResult(ScanVerdict.Infected);
    }

    private static SurveyDefinition Definition(
        bool fileRequired = false,
        int? maxFileCount = null) => new()
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
                        new Question
                        {
                            QuestionId = "qf",
                            Type = QuestionType.File,
                            Title = LocalizedText.Japanese("添付"),
                            IsRequired = fileRequired,
                            Settings = new QuestionSettings { MaxFileCount = maxFileCount },
                        },
                    ],
                },
            ],
        };

    /// <summary>弾いた記録を覚えるだけの偽物（Issue #39）。</summary>
    private sealed class FakeRejections : IAttachmentRejectionStore
    {
        public List<AttachmentRejectionEntry> Written { get; } = [];

        public Exception? Throws { get; set; }

        public Task WriteAsync(
            IReadOnlyCollection<AttachmentRejectionEntry> entries,
            CancellationToken cancellationToken = default)
        {
            if (Throws is not null)
            {
                return Task.FromException(Throws);
            }

            Written.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AttachmentRejectionView>> ListAsync(
            AttachmentRejectionQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountSinceAsync(
            DateTime since, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOlderThanAsync(
            DateTime threshold, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static (ResponseIntake Intake, FakeOutbox Outbox, FakeRejections Rejections) Intake(
        SurveyDefinition? definition = null,
        AttachmentPolicy? policy = null,
        IVirusScanner? scanner = null,
        bool withInspector = true,
        FakeRejections? rejections = null)
    {
        var snapshot = new SurveySnapshot(
            definition ?? Definition(), new MappingDefinition(), 1, "DescriptionA");
        var outbox = new FakeOutbox();
        var survey = new SurveyRecord(
            SurveyId, PublicId, "検証用", 1, "DescriptionA", (int)SurveyStatus.Published, 1);

        var store = rejections ?? new FakeRejections();

        var intake = new ResponseIntake(
            new FakeSurveys(survey),
            new FakeSnapshots(snapshot),
            outbox,
            new FakeTokens(),
            withInspector ? new AttachmentInspector(policy ?? Policy(), scanner) : null,
            rejections: store);

        return (intake, outbox, store);
    }

    private static AnsweredAttachment Png(string name = "a.png", int size = 16) =>
        new("qf", new IncomingAttachment(name, Bytes(size)));

    private static byte[] Bytes(int size)
    {
        var content = new byte[size];
        PngHeader.CopyTo(content.AsSpan());
        return content;
    }

    [Fact]
    public async Task 隠れている設問の回答は送信待ちへ入れない()
    {
        // **落とさないと、画面を通さずに送るだけで隠した設問へ書き込める**（Issue #41）
        var definition = new SurveyDefinition
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
                            Type = QuestionType.Radio,
                            Title = LocalizedText.Japanese("利用中か"),
                            Choices =
                            [
                                new Choice("yes", LocalizedText.Japanese("はい")),
                                new Choice("no", LocalizedText.Japanese("いいえ")),
                            ],
                        },
                        new Question
                        {
                            QuestionId = "q2",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("サービス名"),
                            VisibleWhen = new VisibilityCondition
                            {
                                Rules = [new ConditionRule("q1", ConditionOperator.Equals, "yes")],
                            },
                        },
                    ],
                },
            ],
        };

        var (intake, outbox, _) = Intake(definition);

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "no"), Answer.Of("q2", "入ってはいけない値")],
            []);

        Assert.True(result.Accepted);
        Assert.NotNull(outbox.SavedPayload);
        Assert.DoesNotContain("入ってはいけない値", outbox.SavedPayload, StringComparison.Ordinal);
        Assert.Contains("q1", outbox.SavedPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 見えている設問の回答はそのまま入る()
    {
        var definition = new SurveyDefinition
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
                            Type = QuestionType.Radio,
                            Title = LocalizedText.Japanese("利用中か"),
                            Choices =
                            [
                                new Choice("yes", LocalizedText.Japanese("はい")),
                                new Choice("no", LocalizedText.Japanese("いいえ")),
                            ],
                        },
                        new Question
                        {
                            QuestionId = "q2",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("サービス名"),
                            VisibleWhen = new VisibilityCondition
                            {
                                Rules = [new ConditionRule("q1", ConditionOperator.Equals, "yes")],
                            },
                        },
                    ],
                },
            ],
        };

        var (intake, outbox, _) = Intake(definition);

        var result = await intake.SubmitAsync(
            PublicId, Token, [Answer.Of("q1", "yes"), Answer.Of("q2", "ある社のもの")], []);

        Assert.True(result.Accepted);
        Assert.Contains("ある社のもの", outbox.SavedPayload!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 添付は検査を通ってから送信待ちへ入る()
    {
        var (intake, outbox, _) = Intake();

        var result = await intake.SubmitAsync(PublicId, Token, [Answer.Of("q1", "満足")], [Png()]);

        Assert.True(result.Accepted);
        var payload = ResponsePayload.FromJson(outbox.SavedPayload!);
        var answer = payload!.Answers.Single(a => a.QuestionId == "qf");
        Assert.Equal("a.png", answer.Files.Single().Name);
        Assert.Equal(Convert.ToBase64String(Bytes(16)), answer.Files.Single().Base64);
        // マッピングの入力になるファイル名も入っていること
        Assert.Equal("a.png", answer.FileNames.Single());
    }

    [Fact]
    public async Task 許可していない拡張子は受け付けず送信待ちにも入れない()
    {
        var (intake, outbox, _) = Intake();

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [new AnsweredAttachment("qf", new IncomingAttachment("a.exe", Bytes(16)))]);

        Assert.Equal(IntakeRejection.AttachmentRejected, result.Rejection);
        Assert.Equal(
            AttachmentRejectionReason.ExtensionNotAllowed, result.Attachments.Single().Reason);
        // **未検査のバイナリを DB に載せない**
        Assert.Null(outbox.SavedPayload);
    }

    [Fact]
    public async Task 拡張子を偽った添付は受け付けない()
    {
        var (intake, _, _) = Intake();

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [
                new AnsweredAttachment(
                    "qf", new IncomingAttachment("a.png", new byte[] { 0x4D, 0x5A, 0x00 })),
            ]);

        Assert.Equal(
            AttachmentRejectionReason.ContentDoesNotMatchExtension,
            result.Attachments.Single().Reason);
    }

    [Fact]
    public async Task 検出したら回答ごと拒否する()
    {
        var (intake, outbox, _) = Intake(policy: Policy(scanEnabled: true), scanner: new InfectedScanner());

        var result = await intake.SubmitAsync(PublicId, Token, [Answer.Of("q1", "満足")], [Png()]);

        // **添付だけでなく回答ごと拒否する**
        Assert.Equal(IntakeRejection.AttachmentRejected, result.Rejection);
        Assert.Equal(AttachmentRejectionReason.Infected, result.Attachments.Single().Reason);
        Assert.Null(outbox.SavedPayload);
    }

    [Fact]
    public async Task スキャナへ到達できなければ受け付けない()
    {
        var (intake, outbox, _) = Intake(
            policy: Policy(scanEnabled: true), scanner: new UnavailableScanner());

        var result = await intake.SubmitAsync(PublicId, Token, [Answer.Of("q1", "満足")], [Png()]);

        Assert.Equal(
            AttachmentRejectionReason.ScannerUnavailable, result.Attachments.Single().Reason);
        Assert.Null(outbox.SavedPayload);
    }

    [Fact]
    public async Task 検査の口が無いのに添付が来たら受け付けない()
    {
        // **素通しにしない。** 設定漏れで未検査のバイナリが通る方が危ない
        var (intake, outbox, _) = Intake(withInspector: false);

        var result = await intake.SubmitAsync(PublicId, Token, [Answer.Of("q1", "満足")], [Png()]);

        Assert.Equal(IntakeRejection.AttachmentRejected, result.Rejection);
        Assert.Null(outbox.SavedPayload);
    }

    [Fact]
    public async Task 添付を受け付けない設問へは添付できない()
    {
        var (intake, _, _) = Intake();

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [new AnsweredAttachment("q1", new IncomingAttachment("a.png", Bytes(16)))]);

        Assert.Equal(IntakeRejection.Invalid, result.Rejection);
        Assert.Equal(ValidationErrorCode.UnknownQuestion, result.Errors.Single().Code);
    }

    [Fact]
    public async Task 設問ごとの個数上限が効く()
    {
        var (intake, _, _) = Intake(Definition(maxFileCount: 1));

        var result = await intake.SubmitAsync(
            PublicId, Token, [Answer.Of("q1", "満足")], [Png("a.png"), Png("b.png")]);

        Assert.Equal(AttachmentRejectionReason.TooMany, result.Attachments.Single().Reason);
    }

    [Fact]
    public async Task 添付が必須の設問はファイルが無ければ受け付けない()
    {
        var (intake, _, _) = Intake(Definition(fileRequired: true));

        var result = await intake.SubmitAsync(PublicId, Token, [Answer.Of("q1", "満足")]);

        Assert.Equal(IntakeRejection.Invalid, result.Rejection);
        Assert.Contains(result.Errors, error => error.Code == ValidationErrorCode.Required);
    }

    [Fact]
    public async Task 受け取っていないファイル名は回答に残さない()
    {
        // **画面が名乗っただけの名前を信用しない**
        var (intake, outbox, _) = Intake();

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [new Answer("qf", []) { FileNames = ["偽物.png"] }, Answer.Of("q1", "満足")]);

        Assert.True(result.Accepted);
        var payload = ResponsePayload.FromJson(outbox.SavedPayload!);
        Assert.Empty(payload!.Answers.Single(answer => answer.QuestionId == "qf").FileNames);
    }

    // ---- 弾いた記録（Issue #39）------------------------------------------------

    [Fact]
    public async Task 弾いたら理由と件数を記録する()
    {
        // **対策が効いているか、設定が厳しすぎないかは、記録が無いと分からない**
        var (intake, _, rejections) = Intake();

        await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [new AnsweredAttachment("qf", new IncomingAttachment("a.exe", Bytes(16)))]);

        var entry = Assert.Single(rejections.Written);

        Assert.Equal(SurveyId, entry.SurveyId);
        Assert.Equal("qf", entry.QuestionId);
        Assert.Equal((int)AttachmentRejectionReason.ExtensionNotAllowed, entry.Reason);
        Assert.Equal(1, entry.FileCount);
    }

    [Fact]
    public async Task 同じ理由はまとめて一行にする()
    {
        // **1 件ずつ入れると、添付を並べて送るだけで行を好きなだけ増やせる**
        var (intake, _, rejections) = Intake();

        await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [
                new AnsweredAttachment("qf", new IncomingAttachment("a.exe", Bytes(16))),
                new AnsweredAttachment("qf", new IncomingAttachment("b.exe", Bytes(16))),
                new AnsweredAttachment("qf", new IncomingAttachment("c.exe", Bytes(16))),
            ]);

        var entry = Assert.Single(rejections.Written);

        Assert.Equal(3, entry.FileCount);
    }

    [Fact]
    public async Task 受け付けたときは記録しない()
    {
        var (intake, _, rejections) = Intake();

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [Png()]);

        Assert.True(result.Accepted);
        Assert.Empty(rejections.Written);
    }

    [Fact]
    public async Task 検査の口が無いときも記録する()
    {
        // **設定の誤りで全部弾いている状態こそ気付きたい**
        var (intake, _, rejections) = Intake(withInspector: false);

        await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [Png()]);

        var entry = Assert.Single(rejections.Written);

        Assert.Equal((int)AttachmentRejectionReason.ScannerUnavailable, entry.Reason);
        // **設問に紐づかない理由**
        Assert.Null(entry.QuestionId);
    }

    [Fact]
    public async Task 記録に失敗しても受付の結果は変えない()
    {
        // **記録は運用のためのもの。** これが落ちたせいで回答者への応答が変わってはいけない
        var failing = new FakeRejections { Throws = new InvalidOperationException("DB が応えない") };
        var (intake, outbox, _) = Intake(rejections: failing);

        var result = await intake.SubmitAsync(
            PublicId,
            Token,
            [Answer.Of("q1", "満足")],
            [new AnsweredAttachment("qf", new IncomingAttachment("a.exe", Bytes(16)))]);

        Assert.Equal(IntakeRejection.AttachmentRejected, result.Rejection);
        Assert.Null(outbox.SavedPayload);
    }
}
