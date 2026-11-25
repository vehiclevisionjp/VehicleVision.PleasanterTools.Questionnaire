using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Core.Web;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using PasswordHasher = VehicleVision.PleasanterTools.Questionnaire.Web.Services.PasswordHasher;
using TotpService = VehicleVision.PleasanterTools.Questionnaire.Web.Services.TotpService;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// パスキーの契約（Core の P1〜P10）を、本体の HTTP パイプラインと SQLite で流すためのホスト（Issue #1082）。
/// </summary>
/// <remarks>
/// <para>
/// 本体の起動をそのまま使い（<see cref="AdminContractHost"/>）、次の 3 点だけ試験のために差し替える。
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>時計</b>を <see cref="FakeTimeProvider"/> にする（チャレンジの期限・締め出しの期間を進めて確かめる）。
/// </description></item>
/// <item><description>
/// <b>IP ごとのレート制限の窓</b>を、その時計で区切る（本番は実時刻の固定窓。試験が窓を進められるようにする）。
/// 枠の共有（パスワードとパスキーが同じ <c>admin-login</c>）は本番の配線のまま。
/// </description></item>
/// <item><description>
/// <b>パスキーの有効・無効</b>を、試験の途中で切り替えられるようにする（本番は起動時に決まる）。
/// </description></item>
/// </list>
/// </remarks>
internal sealed class AdminPasskeyContractHost : IAdminPasskeyContractHost
{
    /// <summary>レート制限の上限（5 分あたり）。小さくして、共有の確認を短くする。</summary>
    private const int LoginAttempts = 10;

    private readonly AdminContractHost inner;
    private readonly FakeTimeProvider time;
    private readonly OptionsHolder holder;
    private readonly Dictionary<string, Guid> ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Guid> invitations = [];

    private AdminPasskeyContractHost(AdminContractHost inner, FakeTimeProvider time, OptionsHolder holder)
    {
        this.inner = inner;
        this.time = time;
        this.holder = holder;
    }

    public AdminPasskeyContractSettings Settings { get; } = new();

    public string Origin => "https://localhost";

    public int LoginAttemptsPer5Minutes => LoginAttempts;

    public static async Task<AdminPasskeyContractHost> StartAsync()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var holder = new OptionsHolder();
        var inner = await AdminContractHost.StartAsync(
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<ISharedRateLimitStore>();
                services.AddSingleton<ISharedRateLimitStore>(new FakeClockRateLimitStore(time));

                // 試験の途中で有効・無効を切り替えるため、要求ごとに現在の設定で作る（本番は起動時の設定を 1 度だけ登録する）
                var configured = services.Single(service => service.ServiceType == typeof(AdminPasskeyOptions));
                holder.Options = (AdminPasskeyOptions)configured.ImplementationInstance!;
                services.RemoveAll<AdminPasskeyOptions>();
                services.RemoveAll<AdminPasskeyService>();
                services.AddScoped(_ => holder.Options);
                services.AddScoped(provider => new AdminPasskeyService(
                    provider.GetRequiredService<IPasskeyCredentialStore>(),
                    provider.GetRequiredService<IPasskeyChallengeStore>(),
                    provider.GetRequiredService<IPasskeyAccounts>(),
                    provider.GetRequiredService<IPasskeyAuditSink>(),
                    provider.GetRequiredService<AdminPasskeyOptions>(),
                    provider.GetRequiredService<TimeProvider>()));
            },
            new Dictionary<string, string>
            {
                ["QUESTIONNAIRE_ADMIN_PASSKEY"] = "true",
                ["QUESTIONNAIRE_ADMIN_PASSKEY_RPID"] = "localhost",
                ["QUESTIONNAIRE_ADMIN_PASSKEY_ORIGINS"] = "https://localhost",
                ["QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN"] = LoginAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // 窓を進める試験は、全体の枠（1 分あたり）に当たらないようにする
                ["QUESTIONNAIRE_REQUESTS_PER_MIN"] = "100000",
            });
        return new AdminPasskeyContractHost(inner, time, holder);
    }

    public HttpClient CreateClient() => inner.CreateClient();

    public async Task<SeededAdmin> SeedAdminAsync(PasskeySeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var loginId = $"user-{Guid.NewGuid():N}@example.test";
        var password = $"Pw-{Guid.NewGuid():N}";
        var users = Service<IAdminUserStore>();
        var role = seed.Permissions?.Contains(Settings.ResetPermission) == true ? "UserAdministrator" : "Editor";
        var created = await users.CreateAsync(loginId, Service<PasswordHasher>().Hash(password), role, "en")
            ?? throw new InvalidOperationException("利用者を作れませんでした。");
        ids[loginId] = created.Id;
        if (seed.Disabled)
        {
            await users.SetDisabledAsync(created.Id, true);
        }

        var admin = new SeededAdmin(loginId, password);
        if (!seed.PasswordUsable)
        {
            await SetPasswordUsableAsync(admin, false);
        }

        return admin;
    }

    public Task<Guid> UserIdAsync(SeededAdmin admin) => Task.FromResult(ids[admin.LoginId]);

    public Task<HttpResponseMessage> PasswordLoginAsync(HttpClient client, string loginId, string password) =>
        client.PostAsJsonAsync("/api/admin/login", new { loginId, password });

    public Task SetPasskeyEnabledAsync(bool enabled)
    {
        var current = holder.Options;
        holder.Options = new AdminPasskeyOptions
        {
            Enabled = enabled,
            RpId = current.RpId,
            Origins = current.Origins,
            RpName = current.RpName,
            MaxPasskeysPerUser = current.MaxPasskeysPerUser,
        };
        return Task.CompletedTask;
    }

    public Task AdvanceTimeAsync(TimeSpan span)
    {
        time.Advance(span);
        return Task.CompletedTask;
    }

    public async Task EnableTotpAsync(SeededAdmin admin) =>
        await Service<IAdminUserStore>().EnableTotpAsync(
            ids[admin.LoginId], Service<TotpService>().GenerateSecret(), timeStep: 0, []);

    public async Task SetLockoutAsync(SeededAdmin admin, bool locked)
    {
        var users = Service<IAdminUserStore>();
        if (locked)
        {
            await users.RecordFailureAsync(ids[admin.LoginId], 1, TimeSpan.FromMinutes(15));
        }
        else
        {
            await users.RecordSuccessAsync(ids[admin.LoginId]);
        }
    }

    public async Task SetDisabledAsync(SeededAdmin admin, bool disabled) =>
        await Service<IAdminUserStore>().SetDisabledAsync(ids[admin.LoginId], disabled);

    /// <summary>
    /// パスワードで入れるかを切り替える。偽は「招待を受け取っていない」状態（パスワードを誰も知らない）にする。
    /// SAML・Pleasanter のログインはこのホストでは無効なので、使えるログイン方法がパスキーだけになる。
    /// </summary>
    public async Task SetPasswordUsableAsync(SeededAdmin admin, bool usable)
    {
        var userId = ids[admin.LoginId];
        var store = Service<IAdminInvitationStore>();
        if (!usable && !invitations.ContainsKey(userId))
        {
            var invitationId = Guid.NewGuid();
            await store.ReplaceAsync(new AdminInvitation(
                invitationId, userId, $"hash-{invitationId:N}", time.GetUtcNow().AddHours(48), CreatedBy: userId));
            invitations[userId] = invitationId;
        }
        else if (usable && invitations.Remove(userId, out var pending))
        {
            await store.TryConsumeAsync(pending);
        }
    }

    public async Task<IReadOnlyList<PasskeyAuditRecord>> AuditAsync() =>
        [.. (await Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Limit = AuditLogQuery.MaxLimit }))
            .Where(entry => entry.Action.StartsWith("Passkey.", StringComparison.Ordinal))
            .Reverse()
            .Select(entry => new PasskeyAuditRecord(entry.Action, entry.Succeeded, entry.TargetId, entry.DetailJson))];

    /// <summary>監査ログの操作の名前をすべて（パスキー以外も。管理操作の監査フィルタが二重に残していないかを見るため）。</summary>
    public async Task<IReadOnlyList<string>> AllAuditActionsAsync() =>
        [.. (await Service<IAuditLogStore>().ListAsync(new AuditLogQuery { Limit = AuditLogQuery.MaxLimit }))
            .Select(entry => entry.Action)];

    public async Task<bool?> HasSessionViaAsync(SeededAdmin admin, string via) =>
        (await Service<IAdminSessionStore>().ListForUserAsync(ids[admin.LoginId])).Any(session => session.Via == via);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private T Service<T>()
        where T : notnull =>
        inner.Services.GetRequiredService<T>();

    private sealed class OptionsHolder
    {
        public AdminPasskeyOptions Options { get; set; } = AdminPasskeyOptions.Disabled;
    }

    /// <summary>窓を試験の時計で区切る固定窓（<see cref="ISharedRateLimitStore"/>）。</summary>
    private sealed class FakeClockRateLimitStore(TimeProvider clock) : ISharedRateLimitStore
    {
        private readonly Dictionary<(string Partition, long Window), int> counts = [];
        private readonly Lock gate = new();

        public ValueTask<SharedRateLimitResult> AcquireAsync(
            string partition,
            int permitCount,
            int permitLimit,
            TimeSpan window,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var index = clock.GetUtcNow().Ticks / window.Ticks;
                var key = (partition, index);
                counts.TryGetValue(key, out var used);
                if (used + permitCount > permitLimit)
                {
                    return ValueTask.FromResult(new SharedRateLimitResult(false, window));
                }

                counts[key] = used + permitCount;
                return ValueTask.FromResult(new SharedRateLimitResult(true, TimeSpan.Zero));
            }
        }
    }
}
