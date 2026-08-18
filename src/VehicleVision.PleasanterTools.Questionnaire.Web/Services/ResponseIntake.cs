using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
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
}

/// <summary>受付の結果。</summary>
/// <param name="Rejection">断った理由。受け付けたら <c>null</c>。</param>
/// <param name="Errors">検証エラー。</param>
public sealed record IntakeResult(
    IntakeRejection? Rejection = null,
    ImmutableArray<ValidationError> Errors = default)
{
    public bool Accepted => Rejection is null;

    public static IntakeResult Ok() => new();

    public static IntakeResult Reject(IntakeRejection rejection) => new(rejection);

    public static IntakeResult Invalid(ImmutableArray<ValidationError> errors) =>
        new(IntakeRejection.Invalid, errors);
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
    public async Task<IntakeResult> SubmitAsync(
        string publicId,
        string responseToken,
        IReadOnlyCollection<Answer> answers,
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

        // **サーバ側で必ず検証する。** 画面側の検証は体験のためだけ
        var errors = AnswerValidator.Validate(snapshot.Definition, answers);
        if (!errors.IsEmpty)
        {
            return IntakeResult.Invalid(errors);
        }

        // **トークンの行を先に用意する。** 送信待ちだけがあって対応表が無い状態を作らない。
        // **既にある ReferenceId は触らない。** 消すと編集が新規作成になり二重登録になる
        await tokens
            .EnsureAsync(responseToken, survey.SurveyId, cancellationToken)
            .ConfigureAwait(false);

        var payload = ResponsePayload.Create(responseToken, answers);
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
