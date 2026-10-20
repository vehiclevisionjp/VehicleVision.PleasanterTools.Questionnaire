using System.Data.Common;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 旧表（<c>AuditLogs</c> / <c>MailOutbox</c> / <c>AdminInvitations</c> / <c>AppSettings</c>）の行が、
/// Core.Data の共有表（<c>Core_</c> 接頭辞）へ写り、旧表が消えることを 4 RDBMS で確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>旧スキーマの段階（M0037 まで）で行を入れてから、Core の共有表（M10001〜M10007）を作り、
/// 最後に写す移行（M100000〜）まで上げる。</b> 稼働中のデータがある導入先が、更新で通る道そのもの。
/// 各 RDBMS に<b>使い捨ての DB</b>を作って確かめる（ほかの試験が残した表・行の影響を受けない）。
/// </para>
/// <para>
/// <b>冪等であること</b>も見る: Core の表にすでに同じキーの行があれば、その行は上書きも重複もしない。
/// </para>
/// <para>
/// 環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する
/// （<see cref="DatabaseMigrationTests"/> と同じ）。
/// </para>
/// </remarks>
public class CoreStoreMigrationTests
{
    private const long LastLegacyVersion = 37;

    private const long LastCoreVersion = 10007;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    [Fact]
    public void 写す移行はCoreの共有表の後に動く()
    {
        // **Core の共有表が先にできていないと、写し先が無い。**
        var versions = typeof(QuestionnaireDatabase).Assembly.GetTypes()
            .Select(type => type.GetCustomAttributes(typeof(FluentMigrator.MigrationAttribute), false)
                .Cast<FluentMigrator.MigrationAttribute>().SingleOrDefault())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Version)
            .ToList();

        Assert.Contains(100000L, versions);
        Assert.All(versions.Where(version => version >= 100000), version => Assert.True(version > LastCoreVersion));
        Assert.Equal(
            [100000L, 100001L, 100002L, 100003L, 100004L],
            versions.Where(version => version >= 100000).Order().ToArray());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 旧表の行がCoreの共有表へ写り旧表が消える(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);

        // 旧スキーマ（M0037 まで）。Core の共有表はまだ無い
        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        var legacy = await InsertLegacyRowsAsync(factory);

        // Core の共有表を作る（旧表は残ったまま）。**写す前に、Core の表へ同じキーの行を入れておく**（冪等の確認）
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);
        await InsertAlreadyMigratedRowsAsync(factory, legacy);

        // アプリが起動時に通る道（排他付きの全適用）
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);

        Assert.False(QuestionnaireDatabase.Instance.Migrator.HasPendingMigrations(provider, database.ConnectionString));
        foreach (var table in new[] { "AuditLogs", "MailOutbox", "AdminInvitations", "AppSettings" })
        {
            Assert.False(await TableExistsAsync(factory, table), $"旧表 {table} が残っている");
        }

        await AssertAuditLogsAsync(factory, legacy);
        await AssertMailOutboxAsync(factory, legacy);
        await AssertAdminInvitationsAsync(factory, legacy);
        await AssertAppSettingsAsync(factory, legacy);

        // **管理者の表も写して落とす**（M100004。詳しい確認は別の試験）
        Assert.False(await TableExistsAsync(factory, "AdminUsers"));

        // もう一度当てても何も起きない
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);
        Assert.Equal(5, (await new AuditLogStore(factory).ListAsync(new AuditLogQuery())).Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 旧表が無ければ何もしない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);

        // 旧表を落としてから、写す移行を当てる（旧表が最初から無い導入先・移行をやり直した導入先）
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            foreach (var table in new[]
            {
                "AuditLogs", "MailOutbox", "AdminInvitations", "AppSettings", "AdminSessions", "AdminRecoveryCodes", "AdminUsers",
            })
            {
                await connection.ExecuteAsync(SqlDialect.Format(provider, $"DROP TABLE [{table}]"));
            }
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);

        Assert.False(QuestionnaireDatabase.Instance.Migrator.HasPendingMigrations(provider, database.ConnectionString));
        Assert.Empty(await new AuditLogStore(factory).ListAsync(new AuditLogQuery()));
    }

    // ---- 旧表の行 -------------------------------------------------------------------

    private sealed record Legacy(
        DateTime Now,
        Guid AdminUserId,
        Guid MissingAdminUserId,
        Guid SurveyId,
        Guid[] AuditLogIds,
        Guid[] MailIds,
        Guid[] InvitationIds);

    private const string TokenHashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string TokenHashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private const string TokenHashC = "cccccccccccccccccccccccccccccccccccccccccccc";

    private static async Task<Legacy> InsertLegacyRowsAsync(DbConnectionFactory factory)
    {
        var provider = factory.Provider;
        var legacy = new Legacy(
            DbTime.UtcNowTruncated(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        await using var connection = factory.Create();
        await connection.OpenAsync();

        async Task Run(string sql, object parameters) =>
            await connection.ExecuteAsync(SqlDialect.Format(provider, sql), parameters);

        // 管理者（LoginId を ActorLoginId へ引く元）。もう 1 人（MissingAdminUserId）は居ない
        await Run(
            "INSERT INTO [AdminUsers] ([AdminUserId], [LoginId], [PasswordHash], [Role], [CreatedAt], [UpdatedAt]) "
            + "VALUES (@Id, @LoginId, @Hash, 0, @Now, @Now)",
            new { Id = legacy.AdminUserId, LoginId = "alice", Hash = "hash", legacy.Now });

        // 管理操作の記録。StatusCode は NULL / 200 / 302 / 400 / 403（400 以上が失敗）
        var audits = new (int Index, Guid? Admin, string Action, int? Status)[]
        {
            (0, legacy.AdminUserId, "POST /a", 200),
            (1, legacy.AdminUserId, "POST /b", 403),
            (2, null, "POST /login", null),
            (3, legacy.MissingAdminUserId, "POST /c", 400),
            (4, legacy.AdminUserId, "POST /d", 302),
        };
        foreach (var (index, admin, action, status) in audits)
        {
            await Run(
                "INSERT INTO [AuditLogs] ([AuditLogId], [OccurredAt], [AdminUserId], [Action], [StatusCode], "
                + "[TargetType], [TargetId], [DetailJson], [IpAddress]) "
                + "VALUES (@Id, @At, @Admin, @Action, @Status, @TargetType, @TargetId, @Detail, @Ip)",
                new
                {
                    Id = legacy.AuditLogIds[index],
                    At = legacy.Now.AddMinutes(-index),
                    Admin = admin,
                    Action = action,
                    Status = status,
                    TargetType = "Survey",
                    TargetId = legacy.SurveyId.ToString(),
                    Detail = "{\"surveyId\":\"" + legacy.SurveyId + "\"}",
                    Ip = "203.0.113.10",
                });
        }

        // メールの送信待ち。1 通目はアンケートに紐づかない（旧は Guid.Empty）
        var mails = new (int Index, Guid Survey, int Kind, int Status, int Retry, string? Error, string? LockedBy)[]
        {
            (0, Guid.Empty, 5, 0, 2, "一時的な失敗", null),
            (1, legacy.SurveyId, 1, 2, 5, "恒久的な失敗", null),
            (2, legacy.SurveyId, 4, 1, 0, null, "worker-1:abc"),
        };
        foreach (var (index, survey, kind, status, retry, error, lockedBy) in mails)
        {
            await Run(
                "INSERT INTO [MailOutbox] ([MailId], [Kind], [SurveyId], [PayloadProtected], [Status], [RetryCount], "
                + "[NextAttemptAt], [LastError], [LockedBy], [LockedUntil], [CreatedAt], [UpdatedAt]) "
                + "VALUES (@Id, @Kind, @Survey, @Payload, @Status, @Retry, @Next, @Error, @LockedBy, @LockedUntil, @Created, @Updated)",
                new
                {
                    Id = legacy.MailIds[index],
                    Kind = kind,
                    Survey = survey,
                    Payload = $"protected-{index}",
                    Status = status,
                    Retry = retry,
                    Next = legacy.Now.AddMinutes(10),
                    Error = error,
                    LockedBy = lockedBy,
                    LockedUntil = lockedBy is null ? (DateTime?)null : legacy.Now.AddMinutes(5),
                    Created = legacy.Now.AddHours(-1),
                    Updated = legacy.Now.AddMinutes(-1),
                });
        }

        // 管理者の招待。未使用・使用済み・招いた人が無いもの
        var invitations = new (int Index, string Hash, DateTime? UsedAt, Guid? CreatedBy)[]
        {
            (0, TokenHashA, null, legacy.AdminUserId),
            (1, TokenHashB, legacy.Now.AddMinutes(-3), legacy.AdminUserId),
            (2, TokenHashC, null, null),
        };
        foreach (var (index, hash, usedAt, createdBy) in invitations)
        {
            await Run(
                "INSERT INTO [AdminInvitations] ([InvitationId], [AdminUserId], [TokenHash], [ExpiresAt], [UsedAt], "
                + "[CreatedAt], [CreatedBy]) VALUES (@Id, @Admin, @Hash, @Expires, @UsedAt, @Created, @CreatedBy)",
                new
                {
                    Id = legacy.InvitationIds[index],
                    Admin = legacy.AdminUserId,
                    Hash = hash,
                    Expires = legacy.Now.AddDays(3),
                    UsedAt = usedAt,
                    Created = legacy.Now.AddHours(-2),
                    CreatedBy = createdBy,
                });
        }

        // アプリケーション設定。秘密と、そうでないもの
        await Run(
            "INSERT INTO [AppSettings] ([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedByAdminUserId]) "
            + "VALUES (@Key, @Value, @IsSecret, @Now, @Admin)",
            new { Key = "TEST_PLAIN", Value = "平文の値", IsSecret = false, legacy.Now, Admin = legacy.AdminUserId });
        await Run(
            "INSERT INTO [AppSettings] ([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedByAdminUserId]) "
            + "VALUES (@Key, @Value, @IsSecret, @Now, @Admin)",
            new { Key = "TEST_SECRET", Value = "暗号文のまま", IsSecret = true, legacy.Now, Admin = legacy.AdminUserId });
        await Run(
            "INSERT INTO [AppSettings] ([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedByAdminUserId]) "
            + "VALUES (@Key, @Value, @IsSecret, @Now, @Admin)",
            new { Key = "TEST_CONFLICT", Value = "旧の値", IsSecret = false, legacy.Now, Admin = legacy.AdminUserId });

        return legacy;
    }

    /// <summary>写す前に、Core の表へ同じキーの行を入れておく。<b>写した後も、この行が残る</b>（上書きも重複もしない）。</summary>
    private static async Task InsertAlreadyMigratedRowsAsync(DbConnectionFactory factory, Legacy legacy)
    {
        var provider = factory.Provider;
        await using var connection = factory.Create();
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            SqlDialect.Format(
                provider,
                "INSERT INTO [Core_AuditLogs] ([AuditLogId], [OccurredAt], [Action], [Succeeded]) "
                + "VALUES (@Id, @Now, @Action, 1)"),
            new { Id = legacy.AuditLogIds[0], legacy.Now, Action = "ALREADY IN CORE" });
        await connection.ExecuteAsync(
            SqlDialect.Format(
                provider,
                "INSERT INTO [Core_MailOutbox] ([MailId], [Kind], [SubjectId], [PayloadProtected], [Status], [RetryCount], "
                + "[NextAttemptAt], [CreatedAt], [UpdatedAt]) VALUES (@Id, 9, NULL, @Payload, 0, 0, @Now, @Now, @Now)"),
            new { Id = legacy.MailIds[2], Payload = "already-in-core", legacy.Now });
        await connection.ExecuteAsync(
            SqlDialect.Format(
                provider,
                "INSERT INTO [Core_AdminInvitations] ([InvitationId], [AdminUserId], [TokenHash], [ExpiresAt], [CreatedAt]) "
                + "VALUES (@Id, @Admin, @Hash, @Now, @Now)"),
            new { Id = legacy.InvitationIds[2], Admin = legacy.AdminUserId, Hash = "already-in-core-hash", legacy.Now });
        await connection.ExecuteAsync(
            SqlDialect.Format(
                provider,
                "INSERT INTO [Core_AppSettings] ([SettingKey], [Value], [IsSecret], [UpdatedAt]) "
                + "VALUES (@Key, @Value, 0, @Now)"),
            new { Key = "TEST_CONFLICT", Value = "Core の値", legacy.Now });
    }

    // ---- 写った後の確認（Core のストアで読む。Core の読み方と整合していること） ----------

    private static async Task AssertAuditLogsAsync(DbConnectionFactory factory, Legacy legacy)
    {
        var entries = await new AuditLogStore(factory).ListAsync(new AuditLogQuery { Limit = 100 });

        // 5 件（Core に先にあった 1 件は重複させない）
        Assert.Equal(5, entries.Count);
        Assert.Equal(5, entries.Select(entry => entry.Action).Distinct().Count());

        var alreadyInCore = Assert.Single(entries, entry => entry.Action == "ALREADY IN CORE");
        Assert.Null(alreadyInCore.ActorKey);

        // 管理者 ID は、ハイフン付き小文字の文字列（Core のストアが読み書きする形）。名前は旧 AdminUsers から引く
        var succeeded = Assert.Single(entries, entry => entry.Action == "POST /d");
        Assert.True(succeeded.Succeeded);
        Assert.Equal(302, succeeded.StatusCode);
        Assert.Equal(legacy.AdminUserId.ToString("D"), succeeded.ActorKey);
        Assert.Equal("alice", succeeded.ActorLoginId);
        Assert.Equal("Survey", succeeded.TargetType);
        Assert.Equal(legacy.SurveyId.ToString(), succeeded.TargetId);
        Assert.Equal("{\"surveyId\":\"" + legacy.SurveyId + "\"}", succeeded.DetailJson);
        Assert.Equal("203.0.113.10", succeeded.IpAddress);
        Assert.Equal(legacy.Now.AddMinutes(-4), succeeded.OccurredAt.UtcDateTime);

        // 400 以上は失敗
        var forbidden = Assert.Single(entries, entry => entry.Action == "POST /b");
        Assert.False(forbidden.Succeeded);
        Assert.Equal(403, forbidden.StatusCode);
        var badRequest = Assert.Single(entries, entry => entry.Action == "POST /c");
        Assert.False(badRequest.Succeeded);

        // 居ない管理者の行は、ID は残り、名前は NULL
        Assert.Equal(legacy.MissingAdminUserId.ToString("D"), badRequest.ActorKey);
        Assert.Null(badRequest.ActorLoginId);

        // 認証前の試み（管理者なし・結果なし）は、成功側（旧は 400 以上だけを失敗とした）。ID も名前も無い
        var login = Assert.Single(entries, entry => entry.Action == "POST /login");
        Assert.True(login.Succeeded);
        Assert.Null(login.StatusCode);
        Assert.Null(login.ActorKey);
        Assert.Null(login.ActorLoginId);

        // 失敗だけの絞り込みは、旧と同じ 2 件（403 と 400）
        var failed = await new AuditLogStore(factory).ListAsync(new AuditLogQuery { FailedOnly = true });
        Assert.Equal(["POST /b", "POST /c"], failed.Select(entry => entry.Action).Order().ToArray());

        // 管理者での絞り込み（ActorKey）が、写した行に効く（POST /a は Core に先にあった行で、管理者を持たない）
        var byAdmin = await new AuditLogStore(factory).ListAsync(
            new AuditLogQuery { ActorKey = legacy.AdminUserId.ToString("D") });
        Assert.Equal(["POST /b", "POST /d"], byAdmin.Select(entry => entry.Action).Order().ToArray());
    }

    private sealed class MailRow
    {
        public Guid MailId { get; set; }

        public long Kind { get; set; }

        public Guid? SubjectId { get; set; }

        public string PayloadProtected { get; set; } = "";

        public long Status { get; set; }

        public long RetryCount { get; set; }

        public string? LastError { get; set; }

        public string? LockedBy { get; set; }

        public DateTime? LockedUntil { get; set; }
    }

    private static async Task AssertMailOutboxAsync(DbConnectionFactory factory, Legacy legacy)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync();
        var rows = (await connection.QueryAsync<MailRow>(SqlDialect.Format(
            factory.Provider,
            "SELECT [MailId], [Kind], [SubjectId], [PayloadProtected], [Status], [RetryCount], [LastError], "
            + "[LockedBy], [LockedUntil] FROM [Core_MailOutbox]"))).ToDictionary(row => row.MailId);

        Assert.Equal(3, rows.Count);

        // 紐づかないメール（旧は Guid.Empty）は NULL
        var unbound = rows[legacy.MailIds[0]];
        Assert.Null(unbound.SubjectId);
        Assert.Equal(5, unbound.Kind);
        Assert.Equal("protected-0", unbound.PayloadProtected);
        Assert.Equal(0, unbound.Status);
        Assert.Equal(2, unbound.RetryCount);
        Assert.Equal("一時的な失敗", unbound.LastError);

        // アンケートに紐づくメールは SubjectId へ、状態・確保の情報もそのまま
        var deadLetter = rows[legacy.MailIds[1]];
        Assert.Equal(legacy.SurveyId, deadLetter.SubjectId);
        Assert.Equal(2, deadLetter.Status);
        Assert.Equal(5, deadLetter.RetryCount);
        Assert.Equal("恒久的な失敗", deadLetter.LastError);

        // Core に先にあった行は、上書きも重複もしない
        var alreadyInCore = rows[legacy.MailIds[2]];
        Assert.Equal("already-in-core", alreadyInCore.PayloadProtected);
        Assert.Equal(9, alreadyInCore.Kind);
        Assert.Null(alreadyInCore.SubjectId);

        // Core のストアが、写した行を数えられる（死んだ手紙 1 通、待ち 2 通）
        var status = await new MailOutbox(factory, TimeProvider.System).GetStatusAsync();
        Assert.Equal(2, status.PendingCount);
        Assert.Equal(1, status.DeadLetterCount);
    }

    private static async Task AssertAdminInvitationsAsync(DbConnectionFactory factory, Legacy legacy)
    {
        var store = new AdminInvitationStore(factory, new FakeTimeProvider(AsUtc(legacy.Now)));

        var unused = await store.FindByTokenHashAsync(TokenHashA);
        Assert.NotNull(unused);
        Assert.Equal(legacy.InvitationIds[0], unused.InvitationId);
        Assert.Equal(legacy.AdminUserId, unused.AdminUserId);
        Assert.Null(unused.UsedAt);
        Assert.Equal(legacy.Now.AddDays(3), unused.ExpiresAt.UtcDateTime);
        Assert.Equal(legacy.Now.AddHours(-2), unused.CreatedAt!.Value.UtcDateTime);
        Assert.Equal(legacy.AdminUserId, unused.CreatedBy);

        // 使用済みの行も写る（使われた事実として残す）。大文字小文字を含むハッシュもそのまま引ける
        var used = await store.FindByTokenHashAsync(TokenHashB);
        Assert.NotNull(used);
        Assert.Equal(legacy.Now.AddMinutes(-3), used.UsedAt!.Value.UtcDateTime);

        // 招いた人が無い行は CreatedBy が NULL のまま。ただし Core に先にあった同じ ID の行は上書きしない
        Assert.Null(await store.FindByTokenHashAsync(TokenHashC));
        var existing = await store.FindByTokenHashAsync("already-in-core-hash");
        Assert.NotNull(existing);
        Assert.Equal(legacy.InvitationIds[2], existing.InvitationId);

        // 招待中（未使用で期限内）の管理者。写した未使用の行と、Core に先にあった行（期限が今）
        Assert.Equal([legacy.AdminUserId], await store.ListPendingAdminUserIdsAsync());

        // 写した招待は使い切れる（同時に 2 回は通らない）
        Assert.True(await store.TryConsumeAsync(unused.InvitationId));
        Assert.False(await store.TryConsumeAsync(unused.InvitationId));
    }

    private static async Task AssertAppSettingsAsync(DbConnectionFactory factory, Legacy legacy)
    {
        var records = (await new AppSettingStore(factory, new FakeTimeProvider(AsUtc(legacy.Now))).ListAsync())
            .ToDictionary(record => record.SettingKey);

        Assert.Equal(3, records.Count);

        var plain = records["TEST_PLAIN"];
        Assert.Equal("平文の値", plain.Value);
        Assert.False(plain.IsSecret);
        Assert.Equal(legacy.AdminUserId, plain.UpdatedByAdminUserId);
        Assert.Equal(legacy.Now, plain.UpdatedAt.UtcDateTime);

        // 秘密は暗号文のまま（復号しない）で、秘密の印も保つ
        var secret = records["TEST_SECRET"];
        Assert.Equal("暗号文のまま", secret.Value);
        Assert.True(secret.IsSecret);

        // Core に先にあった同じ設定名は、上書きしない
        Assert.Equal("Core の値", records["TEST_CONFLICT"].Value);
    }

    // ---- 管理者・復旧コード・初期設定の印・セッション（M100004） ----------------------

    private static readonly Guid AdminA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static readonly Guid AdminB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly Guid AdminC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static readonly Guid AdminD = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static readonly Guid AdminE = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");

    private const string TotpSecret = "JBSWY3DPEHPK3PXP";

    private static readonly PasswordHasher Hasher = new();

    private static readonly SecretProtector LegacyProtector = new(SecretProtector.GenerateKey());

    private static string RecoveryHash(string code) => Hasher.Hash(RecoveryCode.Normalize(code));

    /// <summary>旧スキーマ（M0037）の管理者・復旧コード・セッションを入れる。</summary>
    private static async Task InsertLegacyAdminsAsync(DbConnectionFactory factory, DateTime now)
    {
        var provider = factory.Provider;
        await using var connection = factory.Create();
        await connection.OpenAsync();

        async Task Run(string sql, object parameters) =>
            await connection.ExecuteAsync(SqlDialect.Format(provider, sql), parameters);

        const string insertUser =
            "INSERT INTO [AdminUsers] ([AdminUserId], [LoginId], [PasswordHash], [Role], [IsDisabled], "
            + "[TotpSecretEncrypted], [TotpEnabledAt], [LastLoginAt], [CreatedAt], [UpdatedAt], [FailedLoginCount], "
            + "[LockedUntil], [TotpLastTimeStep], [Language], [ResponseNotificationEnabled]) "
            + "VALUES (@Id, @LoginId, @Hash, @Role, @Disabled, @Secret, @TotpAt, @LastLogin, @Now, @Now, @Failed, "
            + "@LockedUntil, @Step, @Language, @Notify)";

        // A: Administrator。2 要素登録済み（暗号文は文脈を持たない旧の形）・英語・通知を受ける・失敗 3 回・締め出し中
        await Run(insertUser, new
        {
            Id = AdminA,
            LoginId = "Admin@Example.test",
            Hash = Hasher.Hash("pw-A"),
            Role = 1,
            Disabled = false,
            Secret = (string?)LegacyProtector.Protect(TotpSecret),
            TotpAt = (DateTime?)now,
            LastLogin = (DateTime?)now.AddDays(-1),
            Now = now,
            Failed = 3,
            LockedUntil = (DateTime?)now.AddMinutes(10),
            Step = (long?)12345,
            Language = (string?)"en",
            Notify = true,
        });

        // B: SurveyAdministrator。無効。言語は未選択（NULL）
        await Run(insertUser, new
        {
            Id = AdminB,
            LoginId = "survey",
            Hash = Hasher.Hash("pw-B"),
            Role = 2,
            Disabled = true,
            Secret = (string?)null,
            TotpAt = (DateTime?)null,
            LastLogin = (DateTime?)null,
            Now = now,
            Failed = 0,
            LockedUntil = (DateTime?)null,
            Step = (long?)null,
            Language = (string?)null,
            Notify = false,
        });

        // C: UserAdministrator、D: Auditor
        foreach (var (id, loginId, role) in new[] { (AdminC, "users", 3), (AdminD, "audit", 4) })
        {
            await Run(insertUser, new
            {
                Id = id,
                LoginId = loginId,
                Hash = Hasher.Hash("pw"),
                Role = role,
                Disabled = false,
                Secret = (string?)null,
                TotpAt = (DateTime?)null,
                LastLogin = (DateTime?)now,
                Now = now,
                Failed = 0,
                LockedUntil = (DateTime?)null,
                Step = (long?)null,
                Language = (string?)"ja",
                Notify = false,
            });
        }

        // A の復旧コード（1 本は使用済み）
        foreach (var (code, usedAt) in new[] { ("AAAA-1111", (DateTime?)null), ("BBBB-2222", (DateTime?)null), ("CCCC-3333", (DateTime?)now) })
        {
            await Run(
                "INSERT INTO [AdminRecoveryCodes] ([RecoveryCodeId], [AdminUserId], [CodeHash], [UsedAt], [CreatedAt]) "
                + "VALUES (@Id, @User, @Hash, @UsedAt, @Now)",
                new { Id = Guid.NewGuid(), User = AdminA, Hash = RecoveryHash(code), UsedAt = usedAt, Now = now });
        }

        // 旧セッション（写さずに落とす）
        foreach (var user in new[] { AdminA, AdminC })
        {
            await Run(
                "INSERT INTO [AdminSessions] ([AdminSessionId], [AdminUserId], [Kind], [ProtectedPayload], [CreatedAt], "
                + "[ExpiresAt], [IpAddress], [UserAgent], [LoginMethod]) "
                + "VALUES (@Id, @User, 0, 'payload', @Now, @Expires, '203.0.113.1', 'agent', 1)",
                new { Id = Guid.NewGuid(), User = user, Now = now, Expires = now.AddHours(8) });
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 旧の管理者が共有表へ写り旧セッションは失効する(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);
        var now = DbTime.UtcNowTruncated();

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        await InsertLegacyAdminsAsync(factory, now);

        MigrateTo(provider, database.ConnectionString, LastCoreVersion);
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);

        foreach (var table in new[] { "AdminUsers", "AdminRecoveryCodes", "AdminSessions" })
        {
            Assert.False(await TableExistsAsync(factory, table), $"旧表 {table} が残っている");
        }

        var time = new FakeTimeProvider(AsUtc(now));
        var store = AdminTestSupport.CreateUserStore(factory, time, LegacyProtector);

        // 4 人。役割は整数から名前へ。ログイン ID は元の大文字小文字のまま、検索は区別しない
        var users = (await store.ListAsync()).ToDictionary(user => user.Id);
        Assert.Equal(4, users.Count);
        Assert.Equal("Administrator", users[AdminA].Role);
        Assert.Equal("SurveyAdministrator", users[AdminB].Role);
        Assert.Equal("UserAdministrator", users[AdminC].Role);
        Assert.Equal("Auditor", users[AdminD].Role);

        var a = users[AdminA];
        Assert.Equal("Admin@Example.test", a.LoginId);
        Assert.Equal("en", a.Language);
        Assert.Equal(3, a.FailedLoginCount);
        Assert.Equal(now.AddMinutes(10), a.LockedUntil!.Value.UtcDateTime);
        Assert.Equal(now.AddDays(-1), a.LastLoginAt!.Value.UtcDateTime);
        Assert.Equal(now, a.CreatedAt.UtcDateTime);
        Assert.False(a.IsDisabled);
        Assert.True(a.TotpEnabled);

        Assert.True(users[AdminB].IsDisabled);
        Assert.Equal(string.Empty, users[AdminB].Language);
        Assert.False(users[AdminB].TotpEnabled);
        Assert.Null(users[AdminB].LastLoginAt);

        var found = await store.FindForLoginAsync("  admin@EXAMPLE.test ");
        Assert.Equal(AdminA, found!.Value.User.Id);

        // パスワードのハッシュは、そのまま照合できる
        Assert.True(Hasher.Verify("pw-A", found.Value.PasswordHash).Verified);

        // **旧の暗号文（文脈なし）が、新しい文脈の窓口でそのまま読める。** 共有鍵は復号しないで写している
        Assert.Equal(TotpSecret, await store.GetTotpSecretAsync(AdminA));

        // 時間枠も写っている（同じ枠は通らない）
        Assert.False(await store.TryUseTotpStepAsync(AdminA, 12345));
        Assert.True(await store.TryUseTotpStepAsync(AdminA, 12346));

        // 復旧コード: 未使用は 2 本。塩つきのハッシュを読んで照合し、完全一致で使い切れる
        Assert.Equal(2, await store.CountRecoveryCodesAsync(AdminA));
        var settings = new AdminUserSettingsStore(factory);
        var hashes = await settings.ListUnusedRecoveryCodeHashesAsync(AdminA);
        var matched = Assert.Single(hashes, hash => Hasher.Verify(RecoveryCode.Normalize("AAAA-1111"), hash).Verified);
        Assert.True(await store.TryUseRecoveryCodeAsync(AdminA, matched));
        Assert.False(await store.TryUseRecoveryCodeAsync(AdminA, matched));
        Assert.Equal(1, await store.CountRecoveryCodesAsync(AdminA));

        // 回答通知メールの設定は Q の表へ（受け取る管理者の行だけ）
        var recipient = Assert.Single(await settings.ListResponseNotificationRecipientsAsync());
        Assert.Equal(AdminA, recipient.AdminUserId);
        Assert.Equal("en", recipient.Language);
        Assert.True(await settings.GetResponseNotificationEnabledAsync(AdminA));
        Assert.False(await settings.GetResponseNotificationEnabledAsync(AdminB));

        // **初期設定の印が立つ。** 最初の管理者は、もう作れない
        Assert.Null(await store.CreateFirstAsync("attacker", "hash", string.Empty));

        // **旧セッションは写さない。** 全員が再ログインになる
        var sessions = new AdminSessionStore(factory, time);
        Assert.Empty(await sessions.ListForUserAsync(AdminA));
        Assert.Empty(await sessions.ListForUserAsync(AdminC));

        // もう一度当てても何も起きない
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);
        Assert.Equal(4, (await store.ListAsync()).Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 管理者が居なければ初期設定の印を立てない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);

        var store = AdminTestSupport.CreateUserStore(factory, TimeProvider.System, LegacyProtector);
        Assert.Equal(0, await store.CountAsync());

        // 印が無いので、最初の管理者を作れる
        Assert.NotNull(await store.CreateFirstAsync("first", "hash", string.Empty));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Coreに同じキーがあれば管理者も復旧コードも重複させない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);
        var now = DbTime.UtcNowTruncated();

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        await InsertLegacyAdminsAsync(factory, now);
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);

        // 先に Core へ入れておく: A と同じ ID（同じログイン ID）、A の未使用の復旧コード 1 本、初期設定の印
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                SqlDialect.Format(
                    provider,
                    "INSERT INTO [Core_AdminUsers] ([Id], [LoginId], [LoginIdKey], [PasswordHash], [Role], [IsDisabled], "
                    + "[FailedLoginCount], [CreatedAt], [Language]) "
                    + "VALUES (@Id, 'Admin@Example.test', 'admin@example.test', 'core-hash', 'Editor', 0, 0, @Now, 'ja')"),
                new { Id = AdminA, Now = now });
            await connection.ExecuteAsync(
                SqlDialect.Format(
                    provider,
                    "INSERT INTO [Core_AdminRecoveryCodes] ([UserId], [CodeHash]) VALUES (@Id, @Hash)"),
                new
                {
                    Id = AdminA,
                    Hash = await connection.ExecuteScalarAsync<string>(
                        SqlDialect.Format(provider, "SELECT MIN([CodeHash]) FROM [AdminRecoveryCodes] WHERE [UsedAt] IS NULL")),
                });
            await connection.ExecuteAsync(SqlDialect.Format(provider, "INSERT INTO [Core_AdminSetup] ([Id]) VALUES (1)"));
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString);

        var store = AdminTestSupport.CreateUserStore(factory, TimeProvider.System, LegacyProtector);
        Assert.Equal(4, await store.CountAsync());

        // Core にあった行は上書きしない
        var core = await store.FindForLoginAsync("admin@example.test");
        Assert.Equal("core-hash", core!.Value.PasswordHash);
        Assert.Equal("Editor", core.Value.User.Role);

        // 復旧コードは旧の 3 本のうち、Core に同じ（ユーザ, ハッシュ）が先にあった 1 本は重複させず、合計 3 本
        await using var check = factory.Create();
        await check.OpenAsync();
        Assert.Equal(
            3,
            await check.ExecuteScalarAsync<int>(SqlDialect.Format(provider, "SELECT COUNT(*) FROM [Core_AdminRecoveryCodes]")));
        Assert.Equal(
            1,
            await check.ExecuteScalarAsync<int>(SqlDialect.Format(provider, "SELECT COUNT(*) FROM [Core_AdminSetup]")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ログインIDが大文字小文字違いで重複していれば行を示して失敗し旧表は残る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // 大文字小文字を区別しない照合順序の RDBMS（SQL Server・MySQL）では、旧の一意索引がこの重複を許さない
        if (provider is DatabaseProvider.SqlServer or DatabaseProvider.MySql)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);
        var now = DbTime.UtcNowTruncated();

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        await InsertLegacyAdminsAsync(factory, now);
        await InsertSimpleAdminAsync(factory, AdminE, "ADMIN@example.test", 1, now);
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);

        var exception = Record.Exception(
            () => QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString));

        Assert.NotNull(exception);
        var message = FlattenMessages(exception);
        Assert.Contains("Admin@Example.test", message, StringComparison.Ordinal);
        Assert.Contains("ADMIN@example.test", message, StringComparison.Ordinal);
        Assert.Contains(AdminE.ToString(), message, StringComparison.OrdinalIgnoreCase);

        // **旧表は落ちず、Core にも何も写っていない**
        Assert.True(await TableExistsAsync(factory, "AdminUsers"));
        Assert.True(await TableExistsAsync(factory, "AdminSessions"));
        await using var check = factory.Create();
        await check.OpenAsync();
        Assert.Equal(
            0,
            await check.ExecuteScalarAsync<int>(SqlDialect.Format(provider, "SELECT COUNT(*) FROM [Core_AdminUsers]")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 知らない役割の値があれば行を示して失敗し旧表は残る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);
        var now = DbTime.UtcNowTruncated();

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        await InsertSimpleAdminAsync(factory, AdminE, "mystery", 9, now);
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);

        var exception = Record.Exception(
            () => QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString));

        Assert.NotNull(exception);
        var message = FlattenMessages(exception);
        Assert.Contains("mystery", message, StringComparison.Ordinal);
        Assert.Contains("Role=9", message, StringComparison.Ordinal);
        Assert.True(await TableExistsAsync(factory, "AdminUsers"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Coreに同じログインIDの別の管理者が居れば行を示して失敗する(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        await using var database = await ScratchDatabase.CreateAsync(provider, connectionString);
        var factory = new DbConnectionFactory(provider, database.ConnectionString);
        var now = DbTime.UtcNowTruncated();

        MigrateTo(provider, database.ConnectionString, LastLegacyVersion);
        await InsertSimpleAdminAsync(factory, AdminE, "Shared@example.test", 1, now);
        MigrateTo(provider, database.ConnectionString, LastCoreVersion);
        await using (var connection = factory.Create())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                SqlDialect.Format(
                    provider,
                    "INSERT INTO [Core_AdminUsers] ([Id], [LoginId], [LoginIdKey], [PasswordHash], [Role], [IsDisabled], "
                    + "[FailedLoginCount], [CreatedAt], [Language]) "
                    + "VALUES (@Id, 'shared@example.test', 'shared@example.test', 'h', 'Editor', 0, 0, @Now, '')"),
                new { Id = Guid.NewGuid(), Now = now });
        }

        var exception = Record.Exception(
            () => QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, database.ConnectionString));

        Assert.NotNull(exception);
        Assert.Contains("Shared@example.test", FlattenMessages(exception), StringComparison.Ordinal);
        Assert.True(await TableExistsAsync(factory, "AdminUsers"));
    }

    private static async Task InsertSimpleAdminAsync(
        DbConnectionFactory factory,
        Guid id,
        string loginId,
        int role,
        DateTime now)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            SqlDialect.Format(
                factory.Provider,
                "INSERT INTO [AdminUsers] ([AdminUserId], [LoginId], [PasswordHash], [Role], [CreatedAt], [UpdatedAt]) "
                + "VALUES (@Id, @LoginId, 'hash', @Role, @Now, @Now)"),
            new { Id = id, LoginId = loginId, Role = role, Now = now });
    }

    private static string FlattenMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" / ", messages);
    }

    // ---- 道具 -----------------------------------------------------------------------

    /// <summary>DB へ渡す形（種別なし）の日時を、UTC の時刻として読む（暗黙の変換は、種別なしをローカルと見なす）。</summary>
    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static async Task<bool> TableExistsAsync(DbConnectionFactory factory, string table)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync();
        try
        {
            await connection.ExecuteScalarAsync(
                SqlDialect.Format(factory.Provider, $"SELECT COUNT(*) FROM [{table}]"));
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>指定の版まで当てる（<see cref="DatabaseMigrator"/> は版を指定できないので、ランナーを直接組む）。</summary>
    private static void MigrateTo(DatabaseProvider provider, string connectionString, long version)
    {
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(builder =>
            {
                var configured = provider switch
                {
                    DatabaseProvider.SqlServer => builder.AddSqlServer(),
                    DatabaseProvider.PostgreSql => builder.AddPostgres(),
                    DatabaseProvider.MySql => builder.AddMySql8(),
                    DatabaseProvider.Sqlite => builder.AddSQLite(),
                    _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
                };

                configured
                    .WithGlobalConnectionString(connectionString)
                    // アプリの分と、Core.Data の共有マイグレーション（M10001〜）の両方
                    .ScanIn(typeof(M0001_InitialSchema).Assembly, typeof(DatabaseMigrator).Assembly)
                    .For.Migrations();
            })
            .BuildServiceProvider(validateScopes: false);
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
    }

    /// <summary>この試験だけの DB。ほかの試験が残した表・行の影響を受けないよう、使い捨てで作る。</summary>
    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private readonly DatabaseProvider provider;
        private readonly string adminConnectionString;
        private readonly string name;

        private ScratchDatabase(
            DatabaseProvider provider,
            string adminConnectionString,
            string name,
            string connectionString)
        {
            this.provider = provider;
            this.adminConnectionString = adminConnectionString;
            this.name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ScratchDatabase> CreateAsync(DatabaseProvider provider, string adminConnectionString)
        {
            if (provider is DatabaseProvider.Sqlite)
            {
                var path = Path.Combine(Path.GetTempPath(), $"qcore-{Guid.NewGuid():N}.db");
                return new ScratchDatabase(provider, adminConnectionString, path, $"Data Source={path};Pooling=False");
            }

            var databaseName = $"qmig_{Guid.NewGuid():N}"[..20];
            await using (var connection = new DbConnectionFactory(provider, adminConnectionString).Create())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync($"CREATE DATABASE {SqlDialect.Quote(provider, databaseName)}");
            }

            // 接続の使い回しをやめる（消すときに残った接続が邪魔をしない）
            var builder = new DbConnectionStringBuilder { ConnectionString = adminConnectionString };
            builder["Database"] = databaseName;
            builder["Pooling"] = "False";
            return new ScratchDatabase(provider, adminConnectionString, databaseName, builder.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            if (provider is DatabaseProvider.Sqlite)
            {
                foreach (var path in new[] { name, name + "-shm", name + "-wal" })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }

                return;
            }

            var quoted = SqlDialect.Quote(provider, name);
            await using var connection = new DbConnectionFactory(provider, adminConnectionString).Create();
            await connection.OpenAsync();
            var drop = provider switch
            {
                DatabaseProvider.SqlServer =>
                    $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}",
                DatabaseProvider.PostgreSql => $"DROP DATABASE {quoted} WITH (FORCE)",
                _ => $"DROP DATABASE {quoted}",
            };
            await connection.ExecuteAsync(drop);
        }
    }
}
