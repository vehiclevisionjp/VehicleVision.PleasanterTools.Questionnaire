using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>受付を断る理由。</summary>
public enum IntakeRejection
{
    /// <summary>そのアンケートが無い。**存在しない公開 ID と同じ扱いにする。**</summary>
    NotFound,

    /// <summary>まだ受付が始まっていない。</summary>
    NotStarted,

    /// <summary>受付が終わっている。</summary>
    Closed,

    /// <summary>停止中。</summary>
    Suspended,

    /// <summary>回答の中身が定義に合わない。</summary>
    Invalid,

    /// <summary>
    /// 添付を受け付けられない。**添付だけでなく回答ごと拒否する**
    /// （<c>_documents/添付ファイル検査-運用手順書.md</c> 6 章）。
    /// </summary>
    AttachmentRejected,
}

/// <summary>受付の結果。</summary>
/// <param name="Rejection">断った理由。受け付けたら <c>null</c>。</param>
/// <param name="Errors">検証エラー。</param>
/// <param name="Attachments">添付を受け付けなかった理由。</param>
public sealed record IntakeResult(
    IntakeRejection? Rejection = null,
    ImmutableArray<ValidationError> Errors = default,
    ImmutableArray<AttachmentRejection> Attachments = default)
{
    public bool Accepted => Rejection is null;

    public static IntakeResult Ok() => new();

    public static IntakeResult Reject(IntakeRejection rejection) => new(rejection);

    public static IntakeResult Invalid(ImmutableArray<ValidationError> errors) =>
        new(IntakeRejection.Invalid, errors);

    public static IntakeResult AttachmentsRejected(
        ImmutableArray<AttachmentRejection> rejections) =>
        new(IntakeRejection.AttachmentRejected, Attachments: rejections);
}

/// <summary>回答を受け付けて送信待ちへ入れる。</summary>
/// <remarks>
/// <para>
/// **受付完了を返すのは送信待ちへ書けた後**（<c>_documents/アプリケーション設計.md</c> 2 章）。
/// ここより前で失敗したら回答者にエラーを返して再送してもらう。
/// **コミット後は失われない。**
/// </para>
/// <para>
/// **Pleasanter へは送らない。** 送信は非同期でワーカーが行う。
/// </para>
/// </remarks>
public sealed class ResponseIntake(
    ISurveyRepository surveys,
    ISurveySnapshotStore snapshots,
    IResponseOutbox outbox,
    IResponseTokenStore tokens,
    AttachmentInspector? inspector = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>公開中の定義を返す。回答画面が使う。</summary>
    /// <remarks>**下書きは絶対に返さない。** 公開済みの版だけを返す。</remarks>
    public async Task<(SurveyDefinition? Definition, IntakeRejection? Rejection)> GetPublishedAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var survey = await surveys.FindByPublicIdAsync(publicId, cancellationToken)
            .ConfigureAwait(false);

        var rejection = CheckAcceptable(survey);
        if (rejection is not null || survey?.PublishedVersion is null)
        {
            return (null, rejection ?? IntakeRejection.NotFound);
        }

        var snapshot = await snapshots
            .FindAsync(survey.SurveyId, survey.PublishedVersion.Value, cancellationToken)
            .ConfigureAwait(false);

        return snapshot is null
            ? (null, IntakeRejection.NotFound)
            : (snapshot.Definition, null);
    }

    /// <summary>回答を受け付ける。</summary>
    /// <param name="publicId">アンケートの公開 ID。</param>
    /// <param name="responseToken">回答トークン。</param>
    /// <param name="answers">回答。</param>
    /// <param name="attachments">
    /// 添付ファイル。**送信待ちへ保存する前に検査する**
    /// （<c>_documents/添付ファイル検査-運用手順書.md</c> 7 章）。
    /// 保存してからでは未検査のバイナリが DB に載る。
    /// </param>
    /// <param name="cancellationToken">中断。</param>
    public async Task<IntakeResult> SubmitAsync(
        string publicId,
        string responseToken,
        IReadOnlyCollection<Answer> answers,
        IReadOnlyList<AnsweredAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var survey = await surveys.FindByPublicIdAsync(publicId, cancellationToken)
            .ConfigureAwait(false);

        var rejection = CheckAcceptable(survey);
        if (rejection is not null || survey?.PublishedVersion is null)
        {
            return IntakeResult.Reject(rejection ?? IntakeRejection.NotFound);
        }

        var version = survey.PublishedVersion.Value;
        var snapshot = await snapshots.FindAsync(survey.SurveyId, version, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return IntakeResult.Reject(IntakeRejection.NotFound);
        }

        var files = attachments ?? [];

        // **画面が名乗ったファイル名ではなく、実際に受け取ったものを正とする。**
        // 名前だけ差し替えて中身を偽られないようにする
        var answersWithFiles = ApplyFileNames(answers, files);

        // **サーバ側で必ず検証する。** 画面側の検証は体験のためだけ
        var errors = AnswerValidator.Validate(snapshot.Definition, answersWithFiles)
            .AddRange(CheckAttachmentTargets(snapshot.Definition, files));
        if (!errors.IsEmpty)
        {
            // **検証で落ちるものをスキャナへ流さない。** 重い検査を無駄に走らせない
            return IntakeResult.Invalid(errors);
        }

        if (files.Count > 0)
        {
            if (inspector is null)
            {
                // 添付が来たのに検査の口が無い。**素通しにしない**
                return IntakeResult.AttachmentsRejected(
                [
                    new AttachmentRejection(null, AttachmentRejectionReason.ScannerUnavailable),
                ]);
            }

            var rejections = await inspector.InspectSubmissionAsync(
                files,
                questionId => PolicyFor(snapshot.Definition, questionId),
                cancellationToken).ConfigureAwait(false);

            if (!rejections.IsEmpty)
            {
                // **添付だけでなく回答ごと拒否する**
                return IntakeResult.AttachmentsRejected(rejections);
            }
        }

        // **トークンの行を先に用意する。** 送信待ちだけがあって対応表が無い状態を作らない。
        // **既にある ReferenceId は触らない。** 消すと編集が新規作成になり二重登録になる
        await tokens
            .EnsureAsync(responseToken, survey.SurveyId, cancellationToken)
            .ConfigureAwait(false);

        // **通らなかったページ・出していない設問の回答は落とす**（Issue #41）。
        // 落とさないと、画面を通さずに送るだけで隠した設問へ書き込める。
        // **検証の後で落とす。** 先に落とすと「知らない設問」の指摘が出せなくなる
        var visible = SurveyFlow.Trace(snapshot.Definition, answersWithFiles);
        var kept = answersWithFiles
            .Where(answer => visible.Visible(answer.QuestionId))
            .ToList();

        var payload = ResponsePayload.Create(responseToken, kept, files);
        await outbox
            .SaveAsync(responseToken, survey.SurveyId, version, payload.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        return IntakeResult.Ok();
    }

    /// <summary>自分の回答を読む。</summary>
    /// <remarks>
    /// **送信待ちを先に見る。** 未送信の回答は Pleasanter にまだ無いので、
    /// Pleasanter だけを見ると自分がさっき送った内容が消えて見える
    /// （<c>_documents/アーキテクチャ方針.md</c> 10 章）。
    /// </remarks>
    public async Task<ResponsePayload?> FindPendingAsync(
        string responseToken,
        CancellationToken cancellationToken = default)
    {
        var json = await outbox.FindPayloadAsync(responseToken, cancellationToken)
            .ConfigureAwait(false);
        return json is null ? null : ResponsePayload.FromJson(json);
    }

    /// <summary>回答のファイル名を、実際に受け取った添付で置き換える。</summary>
    /// <remarks>
    /// 添付が付いた設問に回答の行が無いこともある（値を持たない設問のため）。
    /// **その場合は行を足す。** 足さないと必須チェックが未回答として落ちる。
    /// </remarks>
    private static IReadOnlyCollection<Answer> ApplyFileNames(
        IReadOnlyCollection<Answer> answers,
        IReadOnlyList<AnsweredAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            // **添付が無いなら、画面が名乗ったファイル名も落とす。**
            // 受け取っていないファイルが回答に載ったままになるのを防ぐ
            return [.. answers.Select(answer => answer with { FileNames = [] })];
        }

        var namesByQuestion = attachments
            .GroupBy(attachment => attachment.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(attachment => attachment.File.FileName).ToImmutableArray(),
                StringComparer.Ordinal);

        var updated = answers
            .Select(answer => answer with
            {
                FileNames = namesByQuestion.TryGetValue(answer.QuestionId, out var names)
                    ? names
                    : [],
            })
            .ToList();

        var answered = updated.Select(answer => answer.QuestionId).ToHashSet(StringComparer.Ordinal);
        updated.AddRange(namesByQuestion
            .Where(pair => !answered.Contains(pair.Key))
            .Select(pair => new Answer(pair.Key, []) { FileNames = pair.Value }));

        return updated;
    }

    /// <summary>添付の宛先が定義に合っているかを見る。</summary>
    /// <remarks>**添付を受け付けない設問へ添付できないこと。**</remarks>
    private static ImmutableArray<ValidationError> CheckAttachmentTargets(
        SurveyDefinition definition,
        IReadOnlyList<AnsweredAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        var fileQuestions = definition.AllQuestions
            .Where(question => question.Type is QuestionType.File)
            .Select(question => question.QuestionId)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. attachments
                .Select(attachment => attachment.QuestionId)
                .Distinct(StringComparer.Ordinal)
                .Where(questionId => !fileQuestions.Contains(questionId))
                .Select(questionId =>
                    new ValidationError(questionId, ValidationErrorCode.UnknownQuestion)),
        ];
    }

    /// <summary>設問ごとの受け入れ条件を返す。</summary>
    private AttachmentPolicy PolicyFor(SurveyDefinition definition, string questionId)
    {
        var settings = definition.AllQuestions
            .FirstOrDefault(question => question.QuestionId == questionId)?
            .Settings;

        return inspector!.Policy.Tighten(settings?.MaxFileCount, settings?.MaxFileSizeBytes);
    }

    /// <summary>受け付けられる状態かを見る。</summary>
    private IntakeRejection? CheckAcceptable(SurveyRecord? survey)
    {
        if (survey is null || survey.PublishedVersion is null)
        {
            // **存在しない公開 ID と、未公開のアンケートを区別しない。**
            // 区別すると、公開 ID の総当たりで「実在するか」が分かってしまう
            return IntakeRejection.NotFound;
        }

        if (survey.Status == (int)SurveyStatus.Suspended)
        {
            return IntakeRejection.Suspended;
        }

        if (survey.Status != (int)SurveyStatus.Published)
        {
            return IntakeRejection.NotFound;
        }

        var now = _time.GetUtcNow().UtcDateTime;

        if (survey.AcceptFrom is { } from && now < from)
        {
            return IntakeRejection.NotStarted;
        }

        if (survey.AcceptTo is { } to && now > to)
        {
            return IntakeRejection.Closed;
        }

        return null;
    }
}
