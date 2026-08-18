using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>記憶の上だけで動く <see cref="IAdminUserStore"/>。</summary>
/// <remarks>
/// <para>
/// **DB を立てずに、締め出し事故を防ぐ決まりだけを確かめるため。**
/// 3 RDBMS で同じ SQL が動くことは結合テストの受け持ち。
/// </para>
/// <para>
/// **<see cref="TryDisableAsync"/> と <see cref="TrySetRoleAsync"/> は
/// 本物の SQL と同じ条件を書く。** ここが緩いと、試験が通っても本物が守らない。
/// </para>
/// </remarks>
internal sealed class FakeAdminUserStore(TimeProvider timeProvider) : IAdminUserStore
{
    private readonly Dictionary<Guid, AdminUser> users = [];
    private readonly Dictionary<Guid, List<RecoveryCode>> recoveryCodes = [];

    private sealed record RecoveryCode(Guid RecoveryCodeId, string CodeHash, DateTime? UsedAt);

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public Task<AdminUser?> FindByLoginIdAsync(string loginId, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.Values.FirstOrDefault(
            user => string.Equals(user.LoginId, loginId, StringComparison.Ordinal)));

    public Task<AdminUser?> FindByIdAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(adminUserId, out var user) ? user : null);

    public Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdminUser>>(
            users.Values.OrderBy(user => user.LoginId, StringComparer.Ordinal).ToList());

    public Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(users.Count == 0);

    public Task CreateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        // **ログイン ID は一意。** 本物は索引で守っている
        if (users.Values.Any(other => string.Equals(other.LoginId, user.LoginId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"ログイン ID が重複している: {user.LoginId}");
        }

        users[user.AdminUserId] = user with { CreatedAt = Now, UpdatedAt = Now };
        return Task.CompletedTask;
    }

    public Task UpdatePasswordHashAsync(
        Guid adminUserId,
        string passwordHash,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(adminUserId, user => user with { PasswordHash = passwordHash });

    public Task EnableTotpAsync(
        Guid adminUserId,
        string secretEncrypted,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(adminUserId, user => user with
        {
            TotpSecretEncrypted = secretEncrypted,
            TotpEnabledAt = Now,
        });

    public Task SetDisabledAsync(
        Guid adminUserId,
        bool isDisabled,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(adminUserId, user => user with { IsDisabled = isDisabled });

    public Task<bool> TryDisableAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        if (!users.TryGetValue(adminUserId, out var user))
        {
            return Task.FromResult(false);
        }

        if (user.Role is AdminRole.Administrator && !user.IsDisabled && !OtherAdministratorExists(adminUserId))
        {
            return Task.FromResult(false);
        }

        users[adminUserId] = user with { IsDisabled = true, UpdatedAt = Now };
        return Task.FromResult(true);
    }

    public Task<bool> TrySetRoleAsync(
        Guid adminUserId,
        AdminRole role,
        CancellationToken cancellationToken = default)
    {
        if (!users.TryGetValue(adminUserId, out var user))
        {
            return Task.FromResult(false);
        }

        var demoting = role is not AdminRole.Administrator
            && user.Role is AdminRole.Administrator
            && !user.IsDisabled;

        if (demoting && !OtherAdministratorExists(adminUserId))
        {
            return Task.FromResult(false);
        }

        users[adminUserId] = user with { Role = role, UpdatedAt = Now };
        return Task.FromResult(true);
    }

    public Task RecordSuccessAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        UpdateAsync(adminUserId, user => user with
        {
            LastLoginAt = Now,
            FailedLoginCount = 0,
            LockedUntil = null,
        });

    public async Task<int> RecordFailureAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(adminUserId, user => user with { FailedLoginCount = user.FailedLoginCount + 1 });
        return users[adminUserId].FailedLoginCount;
    }

    public Task LockAsync(
        Guid adminUserId,
        DateTime lockedUntilUtc,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(adminUserId, user => user with { LockedUntil = lockedUntilUtc });

    public Task ReplaceRecoveryCodesAsync(
        Guid adminUserId,
        IReadOnlyList<string> codeHashes,
        CancellationToken cancellationToken = default)
    {
        recoveryCodes[adminUserId] = codeHashes
            .Select(hash => new RecoveryCode(Guid.NewGuid(), hash, null)).ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecoveryCodeRow>> ListUnusedRecoveryCodesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecoveryCodeRow>>(
            recoveryCodes.TryGetValue(adminUserId, out var codes)
                ? codes.Where(code => code.UsedAt is null)
                    .Select(code => new RecoveryCodeRow
                    {
                        RecoveryCodeId = code.RecoveryCodeId,
                        CodeHash = code.CodeHash,
                    }).ToList()
                : []);

    public Task<bool> TryConsumeRecoveryCodeAsync(
        Guid recoveryCodeId,
        CancellationToken cancellationToken = default)
    {
        foreach (var (adminUserId, codes) in recoveryCodes)
        {
            var index = codes.FindIndex(code => code.RecoveryCodeId == recoveryCodeId);
            if (index < 0 || codes[index].UsedAt is not null)
            {
                continue;
            }

            recoveryCodes[adminUserId][index] = codes[index] with { UsedAt = Now };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> TryConsumeTotpTimeStepAsync(
        Guid adminUserId,
        long timeStep,
        CancellationToken cancellationToken = default)
    {
        if (!users.TryGetValue(adminUserId, out var user)
            || (user.TotpLastTimeStep is { } last && last >= timeStep))
        {
            return Task.FromResult(false);
        }

        users[adminUserId] = user with { TotpLastTimeStep = timeStep };
        return Task.FromResult(true);
    }

    private bool OtherAdministratorExists(Guid exceptId) => users.Values.Any(
        other => other.AdminUserId != exceptId
            && other.Role is AdminRole.Administrator
            && !other.IsDisabled);

    private Task UpdateAsync(Guid adminUserId, Func<AdminUser, AdminUser> change)
    {
        if (users.TryGetValue(adminUserId, out var user))
        {
            users[adminUserId] = change(user) with { UpdatedAt = Now };
        }

        return Task.CompletedTask;
    }
}

/// <summary>記憶の上だけで動く <see cref="IAdminInvitationStore"/>。</summary>
internal sealed class FakeAdminInvitationStore(TimeProvider timeProvider) : IAdminInvitationStore
{
    private readonly List<AdminInvitation> invitations = [];

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public Task ReplaceAsync(AdminInvitation invitation, CancellationToken cancellationToken = default)
    {
        // **未使用の分だけ消す。** 使用済みの行は残す
        invitations.RemoveAll(
            existing => existing.AdminUserId == invitation.AdminUserId && existing.UsedAt is null);
        invitations.Add(invitation with { CreatedAt = Now });
        return Task.CompletedTask;
    }

    public Task<AdminInvitation?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(invitations.FirstOrDefault(
            invitation => string.Equals(invitation.TokenHash, tokenHash, StringComparison.Ordinal)));

    public Task<bool> TryConsumeAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var index = invitations.FindIndex(invitation => invitation.InvitationId == invitationId);
        if (index < 0 || invitations[index].UsedAt is not null)
        {
            return Task.FromResult(false);
        }

        invitations[index] = invitations[index] with { UsedAt = Now };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Guid>> ListPendingAdminUserIdsAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(invitations
            .Where(invitation => invitation.UsedAt is null && invitation.ExpiresAt > nowUtc)
            .Select(invitation => invitation.AdminUserId).ToList());

    public Task RevokeUnusedAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        invitations.RemoveAll(
            invitation => invitation.AdminUserId == adminUserId && invitation.UsedAt is null);
        return Task.CompletedTask;
    }
}
