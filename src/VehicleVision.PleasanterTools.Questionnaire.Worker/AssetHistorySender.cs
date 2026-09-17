using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>配布資料の出来事を、回答の投射後に別サイトへ送る。</summary>
public sealed class AssetHistorySender(
    IAssetHistoryOutbox outbox,
    IResponseTokenStore tokens,
    ISurveySnapshotStore snapshots,
    PleasanterApiClient pleasanter,
    PleasanterRecordBuilder recordBuilder,
    MappingEvaluator mappingEvaluator,
    ResponseSenderOptions options,
    ILogger<AssetHistorySender> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<SendOutcome> SendOnceAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await outbox.ClaimAsync(
            $"{options.WorkerName}:history",
            options.LockDuration,
            cancellationToken).ConfigureAwait(false);
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
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "配布資料の受取履歴の送信で想定外の失敗が起きた");
            return await RescheduleAsync(claimed, "想定外の失敗", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> ProcessAsync(
        PendingAssetHistory claimed,
        CancellationToken cancellationToken)
    {
        var referenceId = await tokens.FindReferenceIdAsync(
            claimed.ResponseToken, cancellationToken).ConfigureAwait(false);
        if (referenceId is null)
        {
            // 回答の投射が先に完了するまで待つ。これは送信失敗ではないので再試行回数を増やさない。
            await outbox.WaitAsync(
                claimed.EventId,
                _time.GetUtcNow().UtcDateTime.Add(options.RetryBaseDelay),
                cancellationToken).ConfigureAwait(false);
            return SendOutcome.Rescheduled;
        }

        var snapshot = await snapshots.FindAsync(
            claimed.SurveyId, claimed.SurveyVersion, cancellationToken).ConfigureAwait(false);
        if (snapshot?.AssetHistoryMapping is null || !snapshot.IsAssetHistoryEnabled)
        {
            return await DeadLetterAsync(claimed, "公開版の履歴投射設定が見つからない", cancellationToken)
                .ConfigureAwait(false);
        }

        var values = new Dictionary<MappingSystemValue, ImmutableArray<string>>
        {
            [MappingSystemValue.EventType] = [((AssetHistoryEventType)claimed.EventType).ToString()],
            [MappingSystemValue.OccurredAt] =
                [DateTime.SpecifyKind(claimed.OccurredAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)],
            [MappingSystemValue.ReferenceId] = [referenceId.Value.ToString(CultureInfo.InvariantCulture)],
            [MappingSystemValue.SurveyTitle] =
                [snapshot.Definition.Title.Get(LocalizedText.DefaultLanguage)],
        };
        if (claimed.AssetId is { } assetId)
        {
            values[MappingSystemValue.AssetId] = [assetId.ToString()];
        }

        if (!string.IsNullOrEmpty(claimed.AssetFileName))
        {
            values[MappingSystemValue.AssetFileName] = [claimed.AssetFileName];
        }

        var mapped = mappingEvaluator.Evaluate(
            snapshot.AssetHistoryMapping,
            Array.Empty<Answer>(),
            values);
        if (!mapped.Problems.IsEmpty)
        {
            return await DeadLetterAsync(claimed, "履歴マッピングを評価できない", cancellationToken)
                .ConfigureAwait(false);
        }

        var record = recordBuilder.Build(mapped.Columns);
        if (!record.Problems.IsEmpty)
        {
            return await DeadLetterAsync(claimed, "履歴を列へ写せない", cancellationToken)
                .ConfigureAwait(false);
        }

        var response = await pleasanter.CreateAsync(
            snapshot.AssetHistorySiteId, record.Body, cancellationToken).ConfigureAwait(false);
        return response.ErrorKind switch
        {
            PleasanterErrorKind.None => await CompleteAsync(claimed, cancellationToken).ConfigureAwait(false),
            PleasanterErrorKind.Permanent => await DeadLetterAsync(
                claimed, $"Pleasanter が履歴を受け付けない（{response.StatusCode}）", cancellationToken)
                .ConfigureAwait(false),
            PleasanterErrorKind.Unknown => await DeadLetterAsync(
                claimed, "履歴の作成結果を確認できない。二重登録を避けるため人が確認する", cancellationToken)
                .ConfigureAwait(false),
            _ => await RescheduleAsync(claimed, "履歴の送信に一時的に失敗", cancellationToken)
                .ConfigureAwait(false),
        };
    }

    private async Task<SendOutcome> CompleteAsync(
        PendingAssetHistory claimed,
        CancellationToken cancellationToken)
    {
        await outbox.CompleteAsync(claimed.EventId, cancellationToken).ConfigureAwait(false);
        return SendOutcome.Sent;
    }

    private async Task<SendOutcome> RescheduleAsync(
        PendingAssetHistory claimed,
        string error,
        CancellationToken cancellationToken)
    {
        if (claimed.RetryCount >= options.MaxRetryCount)
        {
            return await DeadLetterAsync(claimed, "再送の上限に達した", cancellationToken)
                .ConfigureAwait(false);
        }

        await outbox.RescheduleAsync(
            claimed.EventId,
            options.NextAttemptAt(_time.GetUtcNow().UtcDateTime, claimed.RetryCount),
            error,
            cancellationToken).ConfigureAwait(false);
        return SendOutcome.Rescheduled;
    }

    private async Task<SendOutcome> DeadLetterAsync(
        PendingAssetHistory claimed,
        string error,
        CancellationToken cancellationToken)
    {
        await outbox.DeadLetterAsync(claimed.EventId, error, cancellationToken).ConfigureAwait(false);
        logger.LogError("配布資料の受取履歴をデッドレターへ移した。理由: {Reason}", error);
        return SendOutcome.DeadLettered;
    }
}
