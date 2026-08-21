using System.Collections.Concurrent;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker.Tests;

/// <summary>送信待ちテーブルの代わり。**DB を使わずに送信の流れだけを検証する。**</summary>
public sealed class FakeOutbox : IResponseOutbox
{
    private readonly ConcurrentQueue<PendingResponse> _pending = new();

    public List<string> Completed { get; } = [];

    public List<(string Token, DateTime NextAttemptAt, string? Error)> Rescheduled { get; } = [];

    public List<(string Token, string Error)> DeadLettered { get; } = [];

    public void Enqueue(PendingResponse response) => _pending.Enqueue(response);

    public Task<PendingResponse?> ClaimAsync(
        string lockedBy, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pending.TryDequeue(out var next) ? next : null);

    public Task CompleteAsync(string responseToken, CancellationToken cancellationToken = default)
    {
        Completed.Add(responseToken);
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(
        string responseToken,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default)
    {
        Rescheduled.Add((responseToken, nextAttemptAtUtc, error));
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(
        string responseToken, string error, CancellationToken cancellationToken = default)
    {
        DeadLettered.Add((responseToken, error));
        return Task.CompletedTask;
    }

    public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<string?> FindPayloadAsync(
        string responseToken, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<int> CountPendingAsync(
        Guid? surveyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pending.Count);

    // **滞留の見張りはここでは動かさない**（Issue #72）。
    // 見張りを渡していない試験なので呼ばれない
    public Task<PendingBacklog> CountBacklogAsync(
        int perSurveyAtLeast, CancellationToken cancellationToken = default) =>
        Task.FromResult(PendingBacklog.Empty with { Total = _pending.Count });

    public Task SaveAsync(
        string responseToken,
        Guid surveyId,
        int surveyVersion,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        Enqueue(new PendingResponse(responseToken, surveyId, surveyVersion, payloadJson, 0));
        return Task.CompletedTask;
    }

    // ---- 管理画面から読む口。**送信の流れの試験では使わない** ------------------

    public Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new OutboxStatus(_pending.Count, null, DeadLettered.Count, null));

    public Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
        DeadLetterQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterView>>([]);

    public Task<int> DeleteDeadLettersOlderThanAsync(
        DateTime threshold, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<Guid?> RequeueDeadLetterAsync(
        string responseToken, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);
}

/// <summary>トークン対応表の代わり。</summary>
public sealed class FakeTokenStore : IResponseTokenStore
{
    public Dictionary<string, long?> Map { get; } = [];

    public Task<long?> FindReferenceIdAsync(
        string responseToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(Map.TryGetValue(responseToken, out var id) ? id : null);

    public Task<bool> EnsureAsync(
        string responseToken, Guid surveyId, CancellationToken cancellationToken = default)
    {
        // **既にある ReferenceId は触らない**
        return Task.FromResult(Map.TryAdd(responseToken, null));
    }

    public Task<int> CountAcceptedAsync(
        Guid surveyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Map.Count);

    public Task SaveAsync(
        string responseToken,
        Guid surveyId,
        long? referenceId,
        CancellationToken cancellationToken = default)
    {
        Map[responseToken] = referenceId;
        return Task.CompletedTask;
    }
}

/// <summary>スナップショットの代わり。</summary>
public sealed class FakeSnapshotStore(SurveySnapshot? snapshot) : ISurveySnapshotStore
{
    public Task<SurveySnapshot?> FindAsync(
        Guid surveyId, int version, CancellationToken cancellationToken = default) =>
        Task.FromResult(snapshot);
}

/// <summary>管理者への知らせの代わり（Issue #80）。**立った知らせを覚えておくだけ。**</summary>
/// <remarks>
/// <c>Throws</c> を立てると書き込みで例外を投げる。
/// **知らせに失敗しても送信の結果が変わらないこと**を確かめるために使う。
/// </remarks>
public sealed class FakeAdminNotificationStore : IAdminNotificationStore
{
    public List<(int Kind, Guid SurveyId)> Raised { get; } = [];

    public bool Throws { get; set; }

    public Task RaiseAsync(
        int kind,
        Guid surveyId,
        DateTime occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (Throws)
        {
            throw new InvalidOperationException("知らせを書けない");
        }

        Raised.Add((kind, surveyId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminNotificationView>> ListAsync(
        AdminNotificationQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdminNotificationView>>([]);

    public Task<int> UnreadCountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Raised.Count);

    public Task<int> MarkAllReadAsync(
        DateTime readAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
