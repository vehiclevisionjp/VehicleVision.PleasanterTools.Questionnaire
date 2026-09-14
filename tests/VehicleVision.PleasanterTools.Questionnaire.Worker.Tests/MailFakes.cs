using System.Collections.Concurrent;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker.Tests;

/// <summary>メールの送信待ての代わり。**DB を使わずに送信の流れだけを確かめる。**</summary>
public sealed class FakeMailOutbox : IMailOutbox
{
    private readonly ConcurrentQueue<PendingMail> _pending = new();

    public List<Guid> Completed { get; } = [];

    public List<(Guid MailId, DateTime NextAttemptAt, string? Error)> Rescheduled { get; } = [];

    public List<(Guid MailId, string Error)> DeadLettered { get; } = [];

    public void Enqueue(PendingMail mail) => _pending.Enqueue(mail);

    public Task<bool> EnqueueAsync(
        Guid mailId,
        int kind,
        Guid surveyId,
        string payloadProtected,
        CancellationToken cancellationToken = default)
    {
        _pending.Enqueue(new PendingMail(mailId, kind, surveyId, payloadProtected, 0));
        return Task.FromResult(true);
    }

    public Task<PendingMail?> ClaimAsync(
        string lockedBy, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pending.TryDequeue(out var next) ? next : null);

    public Task CompleteAsync(Guid mailId, CancellationToken cancellationToken = default)
    {
        Completed.Add(mailId);
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(
        Guid mailId,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default)
    {
        Rescheduled.Add((mailId, nextAttemptAtUtc, error));
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(Guid mailId, string error, CancellationToken cancellationToken = default)
    {
        DeadLettered.Add((mailId, error));
        return Task.CompletedTask;
    }

    public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MailOutboxStatus(_pending.Count, null, DeadLettered.Count));
}

/// <summary>送信経路の代わり。**網へ出ない。**</summary>
public sealed class FakeMailTransport : IMailTransport
{
    /// <summary>送ろうとしたときに投げる失敗。**<c>null</c> なら成功する。**</summary>
    public Func<Exception>? Failure { get; set; }

    public List<OutgoingMail> Sent { get; } = [];

    public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            throw Failure();
        }

        Sent.Add(mail);
        return Task.CompletedTask;
    }
}

/// <summary>暗号化の代わり。**そのまま JSON を通す。**</summary>
/// <remarks>
/// **暗号化そのものは <c>SecretProtector</c> 側で確かめてある。**
/// ここで見たいのは「復号できない行をどう扱うか」なので、
/// <see cref="Broken"/> で読めない状態を作れるようにしてある。
/// </remarks>
public sealed class FakeMailPayloadProtector : IMailPayloadProtector
{
    /// <summary>復号に失敗する状態にする（鍵の変更・破損を模す）。</summary>
    public bool Broken { get; set; }

    public string Protect(OutgoingMail mail) => MailPayload.ToJson(mail);

    public OutgoingMail? Unprotect(string protectedPayload) =>
        Broken ? null : MailPayload.FromJson(protectedPayload);
}
