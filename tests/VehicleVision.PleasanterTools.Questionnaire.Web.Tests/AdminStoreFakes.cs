using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 記憶の上だけで動く、Core.Data の <see cref="IAdminUserStore"/> と、本アプリの <see cref="IAdminUserSettingsStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// **DB を立てずに、本アプリの呼び出し側の決まり（締め出し事故を防ぐなど）だけを確かめるため。**
/// 本物（Core の <c>AdminUserStore</c>）が 4 RDBMS で動くことは、Core の試験と本アプリの結合試験の受け持ち。
/// </para>
/// <para>
/// **<see cref="SetDisabledAsync"/> と <see cref="SetRoleAsync"/> は、本物の SQL と同じ条件を書く。**
/// ここが緩いと、試験が通っても本物が守らない。
/// </para>
/// </remarks>
internal sealed class FakeAdminUserStore(TimeProvider timeProvider) : IAdminUserStore, IAdminUserSettingsStore
{
    private sealed class Entry
    {
        public required Guid Id { get; init; }

        public required string LoginId { get; init; }

        public required string PasswordHash { get; set; }

        public required string Role { get; set; }

        public bool IsDisabled { get; set; }

        public int FailedLoginCount { get; set; }

        public DateTimeOffset? LockedUntil { get; set; }

        public DateTimeOffset? LastLoginAt { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public string? TotpSecret { get; set; }

        public string? TotpPending { get; set; }

        public long? TotpLastTimeStep { get; set; }

        public string Language { get; set; } = "";

        public bool ResponseNotificationEnabled { get; set; }

        public List<(string Hash, bool Used)> Codes { get; } = [];

        public AdminUser ToUser() => new(
            Id,
            LoginId,
            Role,
            IsDisabled,
            FailedLoginCount,
            LockedUntil,
            LastLoginAt,
            CreatedAt,
            !string.IsNullOrEmpty(TotpSecret),
            Language);
    }

    private readonly Dictionary<Guid, Entry> users = [];

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    private static string KeyOf(string loginId) => loginId.Trim().ToLowerInvariant();

    // ---- IAdminUserStore（Core.Data） ----------------------------------------------

    public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(users.Count);

    public Task<AdminUser?> CreateFirstAsync(
        string loginId, string passwordHash, string language, CancellationToken cancellationToken = default)
    {
        // **印は 1 回だけ。** 本物は印の表の主キーで排他する
        if (users.Count > 0)
        {
            return Task.FromResult<AdminUser?>(null);
        }

        return Task.FromResult<AdminUser?>(
            Add(loginId, passwordHash, AdminRoleNames.Administrator, language).ToUser());
    }

    public Task<(AdminUser User, string PasswordHash)?> FindForLoginAsync(
        string loginId, CancellationToken cancellationToken = default)
    {
        var entry = users.Values.FirstOrDefault(user => KeyOf(user.LoginId) == KeyOf(loginId));
        return Task.FromResult<(AdminUser User, string PasswordHash)?>(
            entry is null ? null : (entry.ToUser(), entry.PasswordHash));
    }

    public Task<AdminUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(id, out var entry) ? entry.ToUser() : null);

    public Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdminUser>>(
            users.Values.OrderBy(user => user.CreatedAt).ThenBy(user => user.Id).Select(user => user.ToUser()).ToList());

    public Task<AdminUser?> CreateAsync(
        string loginId, string passwordHash, string role, string language, CancellationToken cancellationToken = default)
    {
        // **ログイン ID は、大文字小文字を区別せず一意。** 本物は LoginIdKey の一意索引で守っている
        if (users.Values.Any(user => KeyOf(user.LoginId) == KeyOf(loginId)))
        {
            return Task.FromResult<AdminUser?>(null);
        }

        return Task.FromResult<AdminUser?>(Add(loginId, passwordHash, role, language).ToUser());
    }

    public Task<AdminChangeResult> SetDisabledAsync(
        Guid id, bool disabled, CancellationToken cancellationToken = default) =>
        Task.FromResult(Change(id, leavesAdministrator: disabled, entry => entry.IsDisabled = disabled));

    public Task<AdminChangeResult> SetRoleAsync(Guid id, string role, CancellationToken cancellationToken = default) =>
        Task.FromResult(Change(
            id,
            leavesAdministrator: role != AdminRoleNames.Administrator,
            entry => entry.Role = role));

    public Task<AdminFailure> RecordFailureAsync(
        Guid id, int threshold, TimeSpan lockout, CancellationToken cancellationToken = default)
    {
        var entry = users[id];
        entry.FailedLoginCount++;
        if (entry.FailedLoginCount < threshold)
        {
            return Task.FromResult(new AdminFailure(entry.FailedLoginCount, null));
        }

        // 本物と同じ: 締め出したら回数は戻す
        entry.FailedLoginCount = 0;
        entry.LockedUntil = Now + lockout;
        return Task.FromResult(new AdminFailure(0, entry.LockedUntil));
    }

    public Task RecordSuccessAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (users.TryGetValue(id, out var entry))
        {
            entry.LastLoginAt = Now;
            entry.FailedLoginCount = 0;
            entry.LockedUntil = null;
        }

        return Task.CompletedTask;
    }

    public Task SetPasswordHashAsync(Guid id, string passwordHash, CancellationToken cancellationToken = default)
    {
        if (users.TryGetValue(id, out var entry))
        {
            entry.PasswordHash = passwordHash;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetTotpSecretAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(id, out var entry) ? entry.TotpSecret : null);

    public Task SetTotpPendingAsync(Guid id, string secret, CancellationToken cancellationToken = default)
    {
        users[id].TotpPending = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetTotpPendingAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(id, out var entry) ? entry.TotpPending : null);

    public Task EnableTotpAsync(
        Guid id,
        string secret,
        long timeStep,
        IReadOnlyList<string> recoveryCodeHashes,
        CancellationToken cancellationToken = default)
    {
        var entry = users[id];
        entry.TotpSecret = secret;
        entry.TotpPending = null;
        entry.TotpLastTimeStep = timeStep;
        entry.Codes.Clear();
        entry.Codes.AddRange(recoveryCodeHashes.Select(hash => (hash, false)));
        return Task.CompletedTask;
    }

    public Task DisableTotpAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = users[id];
        entry.TotpSecret = null;
        entry.TotpPending = null;
        entry.TotpLastTimeStep = null;
        entry.Codes.Clear();
        return Task.CompletedTask;
    }

    public Task<bool> TryUseTotpStepAsync(Guid id, long timeStep, CancellationToken cancellationToken = default)
    {
        if (!users.TryGetValue(id, out var entry) || (entry.TotpLastTimeStep is { } last && last >= timeStep))
        {
            return Task.FromResult(false);
        }

        entry.TotpLastTimeStep = timeStep;
        return Task.FromResult(true);
    }

    public Task<bool> TryUseRecoveryCodeAsync(Guid id, string codeHash, CancellationToken cancellationToken = default)
    {
        if (!users.TryGetValue(id, out var entry))
        {
            return Task.FromResult(false);
        }

        var index = entry.Codes.FindIndex(code => code.Hash == codeHash && !code.Used);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        entry.Codes[index] = (codeHash, true);
        return Task.FromResult(true);
    }

    public Task<int> CountRecoveryCodesAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(id, out var entry) ? entry.Codes.Count(code => !code.Used) : 0);

    // ---- IAdminUserSettingsStore（本アプリ） ---------------------------------------

    public Task<bool> GetResponseNotificationEnabledAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.TryGetValue(adminUserId, out var entry) && entry.ResponseNotificationEnabled);

    public Task SetResponseNotificationEnabledAsync(
        Guid adminUserId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        users[adminUserId].ResponseNotificationEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ResponseNotificationRecipient>> ListResponseNotificationRecipientsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ResponseNotificationRecipient>>(
            users.Values
                .Where(user => user.ResponseNotificationEnabled && !user.IsDisabled)
                .OrderBy(user => user.Id)
                .Select(user => new ResponseNotificationRecipient(
                    user.Id, user.LoginId, string.IsNullOrEmpty(user.Language) ? null : user.Language))
                .ToList());

    public Task SetLanguageAsync(Guid adminUserId, string? language, CancellationToken cancellationToken = default)
    {
        users[adminUserId].Language = language ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <summary>Core の窓口（<see cref="IAdminUserStore"/>）側。設定の窓口と、引数が同じで戻り値だけが違うので、明示的に実装する。</summary>
    Task<AdminChangeResult> IAdminUserStore.SetLanguageAsync(
        Guid id, string language, CancellationToken cancellationToken)
    {
        if (!users.TryGetValue(id, out var entry))
        {
            return Task.FromResult(AdminChangeResult.NotFound);
        }

        entry.Language = language;
        return Task.FromResult(AdminChangeResult.Updated);
    }

    public Task<IReadOnlyList<string>> ListUnusedRecoveryCodeHashesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            users.TryGetValue(adminUserId, out var entry)
                ? entry.Codes.Where(code => !code.Used).Select(code => code.Hash).ToList()
                : []);

    // ---- 試験が使う口 ------------------------------------------------------------------

    /// <summary>指定の ID の管理者を作る（ID を決めたい試験用）。</summary>
    public AdminUser Seed(
        Guid id,
        string loginId = "admin",
        string role = AdminRoleNames.Administrator,
        bool isDisabled = false,
        string language = "")
    {
        var entry = Add(loginId, "hash", role, language, id);
        entry.IsDisabled = isDisabled;
        return entry.ToUser();
    }

    /// <summary>平文のまま持っている共有鍵を読む（本物は暗号化して持つ）。</summary>
    public string? TotpSecretOf(Guid id) => users[id].TotpSecret;

    /// <summary>締め出しの解除の時刻を直接置く。</summary>
    public void SetLockedUntil(Guid id, DateTimeOffset? lockedUntil) => users[id].LockedUntil = lockedUntil;

    private Entry Add(string loginId, string passwordHash, string role, string language, Guid? id = null)
    {
        var entry = new Entry
        {
            Id = id ?? Guid.NewGuid(),
            LoginId = loginId.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            Language = language,
            CreatedAt = Now,
        };
        users[entry.Id] = entry;
        return entry;
    }

    /// <summary>
    /// 本物と同じ条件で変える。<paramref name="leavesAdministrator"/> のときは、
    /// **ほかに有効（無効でなく、一度でもログインした）な Administrator が居る場合だけ**通す。
    /// </summary>
    private AdminChangeResult Change(Guid id, bool leavesAdministrator, Action<Entry> change)
    {
        if (!users.TryGetValue(id, out var entry))
        {
            return AdminChangeResult.NotFound;
        }

        var counted = entry.Role == AdminRoleNames.Administrator && !entry.IsDisabled && entry.LastLoginAt is not null;
        if (leavesAdministrator && counted && !OtherAdministratorExists(id))
        {
            return AdminChangeResult.LastAdministrator;
        }

        change(entry);
        return AdminChangeResult.Updated;
    }

    /// <summary>
    /// 他に**実際に入れる**管理者が居るか。**一度もログインしていない管理者は当てにしない。**
    /// 招待しただけの相手を頭数に入れると、それを頼りに最後の 1 人を止められてしまう。
    /// </summary>
    private bool OtherAdministratorExists(Guid exceptId) => users.Values.Any(
        other => other.Id != exceptId
            && other.Role == AdminRoleNames.Administrator
            && !other.IsDisabled
            && other.LastLoginAt is not null);
}

/// <summary>記憶の上だけで動く、Core.Data の <see cref="IAdminSessionStore"/>。</summary>
/// <remarks>**ID を作り、SHA-256 を識別子にする作りは本物と同じ。**</remarks>
internal sealed class FakeAdminSessionStore(TimeProvider timeProvider) : IAdminSessionStore
{
    private sealed record Row(
        string Handle,
        Guid UserId,
        string Kind,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        string? IpAddress,
        string? UserAgent,
        SessionSso? Sso);

    private readonly List<Row> rows = [];

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>いま残っている行の数（期限切れも含む）。</summary>
    public int Count => rows.Count;

    public string HandleOf(string sessionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    public Task<string> CreateAsync(
        Guid userId,
        string kind,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null,
        SessionSso? sso = null)
    {
        var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        rows.Add(new Row(HandleOf(sessionId), userId, kind, Now, Now + lifetime, ipAddress, userAgent, sso));
        return Task.FromResult(sessionId);
    }

    public Task<AdminSession?> FindAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var handle = HandleOf(sessionId);
        var row = rows.FirstOrDefault(candidate => candidate.Handle == handle && candidate.ExpiresAt > Now);
        return Task.FromResult(
            row is null ? null : new AdminSession(row.UserId, row.Kind, row.ExpiresAt, row.Sso?.Pleasanter));
    }

    public Task<SessionSso?> GetSsoAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RowOf(sessionId)?.Sso);

    public Task TouchPleasanterVerifiedAsync(
        string sessionId,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        var row = RowOf(sessionId);
        if (row?.Sso?.Pleasanter is { } keys)
        {
            rows[rows.IndexOf(row)] = row with
            {
                Sso = row.Sso with { Pleasanter = keys with { VerifiedAt = verifiedAt } },
            };
        }

        return Task.CompletedTask;
    }

    public Task<SamlSessionKeys?> GetSamlKeysAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RowOf(sessionId)?.Sso?.Saml);

    public Task<int> DeleteBySamlKeysAsync(
        string nameId,
        string? sessionIndex,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(rows.RemoveAll(row =>
            row.Sso?.Saml is { } saml
            && saml.NameId == nameId
            && (string.IsNullOrEmpty(sessionIndex) || saml.SessionIndex == sessionIndex)));

    public Task<IReadOnlyList<AdminSessionInfo>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdminSessionInfo>>(
            rows.Where(row => row.UserId == userId && row.Kind == AdminSessionKinds.Session && row.ExpiresAt > Now)
                .OrderByDescending(row => row.CreatedAt)
                .Select(row => new AdminSessionInfo(
                    row.Handle,
                    row.CreatedAt,
                    row.ExpiresAt,
                    row.IpAddress,
                    row.UserAgent,
                    row.Sso?.Saml is not null ? SessionVia.Saml
                    : row.Sso?.Pleasanter is not null ? SessionVia.Pleasanter
                    : SessionVia.Password))
                .ToList());

    public Task<bool> DeleteByHandleAsync(Guid userId, string handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(rows.RemoveAll(row => row.Handle == handle && row.UserId == userId) > 0);

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var handle = HandleOf(sessionId);
        rows.RemoveAll(row => row.Handle == handle);
        return Task.CompletedTask;
    }

    public Task DeleteForUserAsync(
        Guid userId,
        string? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var except = exceptSessionId is null ? null : HandleOf(exceptSessionId);
        rows.RemoveAll(row => row.UserId == userId && row.Handle != except);
        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(rows.RemoveAll(row => row.ExpiresAt <= Now));

    private Row? RowOf(string sessionId)
    {
        var handle = HandleOf(sessionId);
        return rows.FirstOrDefault(row => row.Handle == handle);
    }
}

/// <summary>記憶の上だけで動く <see cref="IAdminInvitationStore"/>。</summary>
internal sealed class FakeAdminInvitationStore(TimeProvider timeProvider) : IAdminInvitationStore
{
    private readonly List<AdminInvitation> invitations = [];

    private DateTimeOffset Now => timeProvider.GetUtcNow();

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
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(invitations
            .Where(invitation => invitation.UsedAt is null && invitation.ExpiresAt > Now)
            .Select(invitation => invitation.AdminUserId).ToList());

    public Task RevokeUnusedAsync(Guid adminUserId, CancellationToken cancellationToken = default)
    {
        invitations.RemoveAll(
            invitation => invitation.AdminUserId == adminUserId && invitation.UsedAt is null);
        return Task.CompletedTask;
    }
}

/// <summary>管理者への知らせの代わり（Issue #80）。**立った知らせを覚えておくだけ。**</summary>
/// <remarks>
/// <c>Throws</c> を立てると書き込みで例外を投げる。
/// **知らせに失敗しても本来の処理が変わらないこと**を確かめるために使う。
/// </remarks>
internal sealed class FakeAdminNotificationStore : IAdminNotificationStore
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
        CancellationToken cancellationToken = default)
    {
        var count = Raised.Count;
        Raised.Clear();
        return Task.FromResult(count);
    }

    public Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
