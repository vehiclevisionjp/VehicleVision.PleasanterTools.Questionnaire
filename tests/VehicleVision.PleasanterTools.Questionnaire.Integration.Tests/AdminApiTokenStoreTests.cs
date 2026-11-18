using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理 API の個人トークンの表（M0042）の読み書きを、対応する RDBMS で確かめる（Issue #1025）。</summary>
/// <remarks>
/// <b>環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。</b>
/// 設定していない場合は何も検証せずに終わる（緑を「通った」と読まないこと）。
/// RDBMS は <c>QUESTIONNAIRE_INTEGRATION_PROVIDER</c> で絞れる（未指定なら、起動している全部と SQLite）。
/// </remarks>
public class AdminApiTokenStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (AdminApiTokenStore Store, DbConnectionFactory Factory) Create(
        DatabaseProvider provider,
        string connectionString)
    {
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        return (new AdminApiTokenStore(factory), factory);
    }

    private static NewAdminApiToken NewToken(
        Guid owner,
        DateTime now,
        TimeSpan lifetime,
        string[]? ips = null,
        string name = "ci")
    {
        var secret = AdminApiTokenSecret.Create();
        return new NewAdminApiToken(
            Guid.NewGuid(),
            owner,
            name,
            secret.Prefix,
            secret.Hash,
            [AdminPermissions.SurveysRead, AdminPermissions.SurveysWrite],
            ips ?? [],
            now,
            now + lifetime);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 作ったトークンをハッシュで引け_行にはハッシュだけが入り平文は無い(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, factory) = Create(provider, connectionString);
        var owner = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var secret = AdminApiTokenSecret.Create();
        var token = NewToken(owner, now, TimeSpan.FromDays(90), ["203.0.113.0/24", "2001:db8::/32"]) with
        {
            Prefix = secret.Prefix,
            TokenHash = secret.Hash,
        };

        await store.CreateAsync(token);
        var found = await store.FindByHashAsync(secret.Hash);

        Assert.NotNull(found);
        Assert.Equal(token.TokenId, found.TokenId);
        Assert.Equal(owner, found.OwnerAdminUserId);
        Assert.Equal(["surveys.read", "surveys.write"], found.Scopes);
        Assert.Equal(["203.0.113.0/24", "2001:db8::/32"], found.IpAllowList);
        Assert.Equal(DateTimeKind.Utc, found.CreatedAt.Kind);
        Assert.InRange((found.ExpiresAt - now).TotalDays, 89.99, 90.01);
        Assert.Null(found.RevokedAt);
        Assert.Null(found.RevokedBy);
        Assert.Null(found.LastUsedAt);
        Assert.True(found.IsActiveAt(now));

        await using var connection = factory.Create();
        await connection.OpenAsync();
        var stored = await connection.QuerySingleAsync<string>(SqlDialect.Format(
            provider, "SELECT [TokenHash] FROM [AdminApiTokens] WHERE [TokenId] = @Id"),
            new { Id = token.TokenId });
        Assert.Equal(secret.Hash, stored);
        Assert.DoesNotContain(secret.Plain, stored, StringComparison.Ordinal);

        // 知らないハッシュ・空は見つからない
        Assert.Null(await store.FindByHashAsync(AdminApiTokenSecret.HashOf("qat_unknown")));
        Assert.Null(await store.FindByHashAsync(""));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 大文字小文字だけ違うハッシュでは引けない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var token = NewToken(Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromDays(1));
        await store.CreateAsync(token);

        // SQL Server・MySQL の既定の照合順序は大文字小文字を区別しない。引いたあとの定数時間の比べ直しが効いていること
        Assert.Null(await store.FindByHashAsync(token.TokenHash.ToUpperInvariant()));
        Assert.NotNull(await store.FindByHashAsync(token.TokenHash));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じハッシュは2本作れない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var token = NewToken(Guid.NewGuid(), DateTime.UtcNow, TimeSpan.FromDays(1));
        await store.CreateAsync(token);

        await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(token with { TokenId = Guid.NewGuid() }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 失効は持ち主の組で絞り_すでに失効したものは書き換えない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var owner = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var token = NewToken(owner, now, TimeSpan.FromDays(10));
        await store.CreateAsync(token);

        // 他人の組では失効しない
        Assert.False(await store.RevokeAsync(
            token.TokenId, Guid.NewGuid(), now, admin, AdminApiTokenRevokeReasons.Owner));
        Assert.Null((await store.FindAsync(token.TokenId))!.RevokedAt);

        Assert.True(await store.RevokeAsync(
            token.TokenId, owner, now, admin, AdminApiTokenRevokeReasons.Administrator));
        var revoked = (await store.FindAsync(token.TokenId))!;
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal(admin, revoked.RevokedBy);
        Assert.Equal(AdminApiTokenRevokeReasons.Administrator, revoked.RevokeReason);
        Assert.False(revoked.IsActiveAt(now));

        // 2 回目は何も起きず、最初の理由が残る
        Assert.False(await store.RevokeAsync(
            token.TokenId, owner, now.AddMinutes(1), null, AdminApiTokenRevokeReasons.Owner));
        Assert.Equal(
            AdminApiTokenRevokeReasons.Administrator, (await store.FindAsync(token.TokenId))!.RevokeReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task まとめて失効するのは持ち主のいま使えるものだけ(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var active = NewToken(owner, now, TimeSpan.FromDays(10), name: "active");
        var already = NewToken(owner, now, TimeSpan.FromDays(10), name: "already");
        var expired = NewToken(owner, now.AddDays(-20), TimeSpan.FromDays(10), name: "expired");
        var strangers = NewToken(other, now, TimeSpan.FromDays(10), name: "other");
        foreach (var token in new[] { active, already, expired, strangers })
        {
            await store.CreateAsync(token);
        }

        await store.RevokeAsync(already.TokenId, owner, now, owner, AdminApiTokenRevokeReasons.Owner);
        Assert.Equal(1, await store.CountActiveAsync(owner, now));

        var revoked = await store.RevokeAllForOwnerAsync(
            owner, now, owner, AdminApiTokenRevokeReasons.PasswordChanged);

        Assert.Equal(active.TokenId, Assert.Single(revoked).TokenId);
        Assert.Equal(0, await store.CountActiveAsync(owner, now));
        Assert.Equal(1, await store.CountActiveAsync(other, now));
        Assert.Null((await store.FindAsync(expired.TokenId))!.RevokedAt);
        Assert.Equal(AdminApiTokenRevokeReasons.Owner, (await store.FindAsync(already.TokenId))!.RevokeReason);

        // 一覧は新しい順で、持ち主のものだけ
        var listed = await store.ListByOwnerAsync(owner);
        Assert.Equal(3, listed.Count);
        Assert.DoesNotContain(listed, item => item.TokenId == strangers.TokenId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 最後に使った日時は間引いて書く(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var now = DateTime.UtcNow;
        var token = NewToken(Guid.NewGuid(), now, TimeSpan.FromDays(1));
        await store.CreateAsync(token);
        var interval = TimeSpan.FromMinutes(1);

        Assert.True(await store.TouchLastUsedAsync(token.TokenId, now, "203.0.113.9", interval));
        Assert.False(await store.TouchLastUsedAsync(token.TokenId, now.AddSeconds(30), "198.51.100.1", interval));
        var first = (await store.FindAsync(token.TokenId))!;
        Assert.Equal("203.0.113.9", first.LastUsedIp);

        Assert.True(await store.TouchLastUsedAsync(token.TokenId, now.AddSeconds(90), "198.51.100.1", interval));
        Assert.Equal("198.51.100.1", (await store.FindAsync(token.TokenId))!.LastUsedIp);
    }
}
