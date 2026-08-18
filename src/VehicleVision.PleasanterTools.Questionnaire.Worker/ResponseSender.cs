using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>1 件処理した結果。</summary>
public enum SendOutcome
{
    /// <summary>送るものが無かった。</summary>
    Idle,

    /// <summary>送れた。</summary>
    Sent,

    /// <summary>後で再送する。</summary>
    Rescheduled,

    /// <summary>再送しても通らないので分離した。**管理者へ通知すること。**</summary>
    DeadLettered,
}

/// <summary>送信待ちを 1 件ずつ Pleasanter へ送る。</summary>
/// <remarks>
/// <para>
/// **常時アウトボックスの本体**（<c>_documents/アーキテクチャ方針.md</c> 10 章）。
/// 回答は必ず送信待ちテーブルを経由してから送られる。
/// </para>
/// <para>
/// **回答本文をログへ出さないこと。**
/// </para>
/// </remarks>
public sealed class ResponseSender(
    IResponseOutbox outbox,
    IResponseTokenStore tokens,
    ISurveySnapshotStore snapshots,
    PleasanterApiClient pleasanter,
    PleasanterRecordBuilder recordBuilder,
    MappingEvaluator mappingEvaluator,
    ResponseSenderOptions options,
    ILogger<ResponseSender> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>1 件だけ処理する。</summary>
    public async Task<SendOutcome> SendOnceAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await outbox
            .ClaimAsync(options.WorkerName, options.LockDuration, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return SendOutcome.Idle;
        }

        try
        {
            return await ProcessAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // **確保したまま落ちても、期限切れで解放される。** 回答は失われない
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "回答の送信で想定外の失敗が起きた");
            await RescheduleAsync(claimed, "想定外の失敗", cancellationToken).ConfigureAwait(false);
            return SendOutcome.Rescheduled;
        }
    }

    private async Task<SendOutcome> ProcessAsync(
        PendingResponse claimed,
        CancellationToken cancellationToken)
    {
        var payload = ResponsePayload.FromJson(claimed.PayloadJson);
        if (payload is null)
        {
            return await DeadLetterAsync(claimed, "回答の中身を読めない", cancellationToken)
                .ConfigureAwait(false);
        }

        var snapshot = await snapshots
            .FindAsync(claimed.SurveyId, claimed.SurveyVersion, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            // 版が消えている。**設計上あってはならない**（SurveyVersions は消さない）
            return await DeadLetterAsync(
                claimed,
                $"アンケートの版が見つからない（version={claimed.SurveyVersion}）",
                cancellationToken).ConfigureAwait(false);
        }

        var mapped = mappingEvaluator.Evaluate(snapshot.Mapping, payload.ToAnswers());
        if (!mapped.Problems.IsEmpty)
        {
            // **回答そのものは捨てない。** 人が対処できるようデッドレターへ
            return await DeadLetterAsync(
                claimed,
                $"マッピングの不備: {string.Join(" / ", mapped.Problems.Select(p => $"{p.TargetColumn}:{p.Reason}"))}",
                cancellationToken).ConfigureAwait(false);
        }

        var record = recordBuilder.Build(
            mapped.Columns,
            snapshot.ResponseJsonColumn,
            snapshot.ResponseJsonColumn is null ? null : claimed.PayloadJson);

        if (!record.Problems.IsEmpty)
        {
            return await DeadLetterAsync(
                claimed,
                $"列へ写せない: {string.Join(" / ", record.Problems.Select(p => $"{p.ColumnName}:{p.Reason}"))}",
                cancellationToken).ConfigureAwait(false);
        }

        var referenceId = await tokens
            .FindReferenceIdAsync(claimed.ResponseToken, cancellationToken)
            .ConfigureAwait(false);

        var response = referenceId is null
            ? await pleasanter.CreateAsync(snapshot.PleasanterSiteId, record.Body, cancellationToken)
                .ConfigureAwait(false)
            : await pleasanter.UpdateAsync(referenceId.Value, record.Body, cancellationToken)
                .ConfigureAwait(false);

        return response.ErrorKind switch
        {
            PleasanterErrorKind.None =>
                await CompleteAsync(claimed, snapshot, response.Id ?? referenceId, cancellationToken)
                    .ConfigureAwait(false),

            // **できたかどうか分からない。照合してから判断する**
            PleasanterErrorKind.Unknown =>
                await ReconcileAsync(claimed, snapshot, payload.Token, cancellationToken)
                    .ConfigureAwait(false),

            PleasanterErrorKind.Permanent =>
                await DeadLetterAsync(
                    claimed,
                    $"Pleasanter が受け付けない（{response.StatusCode}）: {response.Message}",
                    cancellationToken).ConfigureAwait(false),

            // 認証は人が直すまで通らないが、直れば同じ回答が送れる
            PleasanterErrorKind.Unauthorized => await HandleUnauthorizedAsync(claimed, cancellationToken)
                .ConfigureAwait(false),

            _ => await RescheduleAsync(
                claimed,
                $"一時的な失敗（{response.StatusCode}）: {response.Message}",
                cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>応答が返らなかった <c>Create</c> を照合する。</summary>
    /// <remarks>
    /// **回答の正本 JSON に埋めたトークンを部分一致で引く**
    /// （<c>_documents/実機検証結果.md</c> 7 章。実機で確認済み）。
    /// </remarks>
    private async Task<SendOutcome> ReconcileAsync(
        PendingResponse claimed,
        SurveySnapshot snapshot,
        string responseToken,
        CancellationToken cancellationToken)
    {
        if (snapshot.ResponseJsonColumn is null)
        {
            // **照合できないので自動再送しない。** 二重登録を作らない側に倒す
            return await DeadLetterAsync(
                claimed,
                "応答が返らず、回答 JSON 列が無いため照合できない。二重登録を避けるため人が確認する",
                cancellationToken).ConfigureAwait(false);
        }

        var found = await pleasanter.FindByResponseTokenAsync(
            snapshot.PleasanterSiteId,
            snapshot.ResponseJsonColumn,
            responseToken,
            cancellationToken).ConfigureAwait(false);

        if (!found.IsSuccess)
        {
            // 照合そのものが失敗した。まだ分からないので再送はしない
            return await RescheduleAsync(claimed, "応答が返らず、照合もできなかった", cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = found.Body?["Response"]?["Data"]?.AsArray();
        switch (rows?.Count ?? 0)
        {
            case 0:
                // **レコードはできていない。再送してよい**
                logger.LogInformation("応答不明だがレコードは無かったので再送する");
                return await RescheduleAsync(claimed, "応答が返らなかった（未作成）", cancellationToken)
                    .ConfigureAwait(false);

            case 1:
                var id = rows![0]!["ResultId"]?.GetValue<long>();
                logger.LogInformation("応答不明だがレコードはできていた。照合して完了させる");
                return await CompleteAsync(claimed, snapshot, id, cancellationToken)
                    .ConfigureAwait(false);

            default:
                // **既に二重登録されている**
                return await DeadLetterAsync(
                    claimed,
                    $"同じトークンのレコードが {rows!.Count} 件ある。二重登録の可能性",
                    cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> HandleUnauthorizedAsync(
        PendingResponse claimed,
        CancellationToken cancellationToken)
    {
        // **キーの失効・設定ミスは人が直す必要がある。** 通知の対象
        logger.LogError("Pleasanter の認証に失敗した。API キーの失効か設定ミスの可能性がある");
        return await RescheduleAsync(claimed, "Pleasanter の認証に失敗", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SendOutcome> CompleteAsync(
        PendingResponse claimed,
        SurveySnapshot snapshot,
        long? referenceId,
        CancellationToken cancellationToken)
    {
        if (referenceId is not null)
        {
            // **次回の編集で Update に回すため、対応を残す**
            await tokens
                .SaveAsync(claimed.ResponseToken, claimed.SurveyId, referenceId, cancellationToken)
                .ConfigureAwait(false);
        }

        // **送信できたら消す。** 回答本文を残さない
        await outbox.CompleteAsync(claimed.ResponseToken, cancellationToken).ConfigureAwait(false);
        return SendOutcome.Sent;
    }

    private async Task<SendOutcome> RescheduleAsync(
        PendingResponse claimed,
        string error,
        CancellationToken cancellationToken)
    {
        if (claimed.RetryCount >= options.MaxRetryCount)
        {
            return await DeadLetterAsync(
                claimed,
                $"再送の上限に達した（{claimed.RetryCount} 回）: {error}",
                cancellationToken).ConfigureAwait(false);
        }

        var next = options.NextAttemptAt(_time.GetUtcNow().UtcDateTime, claimed.RetryCount);
        await outbox
            .RescheduleAsync(claimed.ResponseToken, next, error, cancellationToken)
            .ConfigureAwait(false);
        return SendOutcome.Rescheduled;
    }

    private async Task<SendOutcome> DeadLetterAsync(
        PendingResponse claimed,
        string error,
        CancellationToken cancellationToken)
    {
        // **黙って溜め続けない。** 通知の対象
        logger.LogError("回答をデッドレターへ回した: {Reason}", error);
        await outbox.DeadLetterAsync(claimed.ResponseToken, error, cancellationToken)
            .ConfigureAwait(false);
        return SendOutcome.DeadLettered;
    }
}
