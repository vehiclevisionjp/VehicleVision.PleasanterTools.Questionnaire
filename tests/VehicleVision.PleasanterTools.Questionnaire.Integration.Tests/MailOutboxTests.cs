using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>メールの送信待ちの読み書きを 3 RDBMS で確かめる（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// ⚠️ **回答の送信待ちで踏んだ落とし穴が、そのまま効いてくる。**
/// MySQL は <c>RETURNING</c> が無く、<c>ROW_COUNT</c> が一致行数を返し、
/// <c>COUNT</c> の型が SQL Server と違う。**3 者で通ることを機械で押さえる。**
/// </para>
/// </remarks>
public class MailOutboxTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static IMailOutbox Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **毎回まっさらにしてから始める**（ResponseOutboxTests と同じ理由）
        using var connection = factory.Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "MailOutbox")}";
        command.ExecuteNonQuery();

        return new MailOutbox(factory);
    }

    private const string Payload = "v1$dummy$dummy$dummy";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 積んで確保して消せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var outbox = Create(provider, connectionString);
        var mailId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();

        Assert.True(await outbox.EnqueueAsync(mailId, 1, surveyId, Payload));

        var claimed = await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        Assert.Equal(mailId, claimed.MailId);
        Assert.Equal(1, claimed.Kind);
        Assert.Equal(surveyId, claimed.SurveyId);
        Assert.Equal(Payload, claimed.PayloadProtected);
        Assert.Equal(0, claimed.RetryCount);

        await outbox.CompleteAsync(mailId);

        // **送れたら消す。** 宛先も本文も残さない
        Assert.Equal(0, (await outbox.GetStatusAsync()).PendingCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じ識別子は二重に積めない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **影響行数では見分けられない**（MySQL は一致行数を返す）。
        // **主キーの衝突そのもの**で判定していることを押さえる
        var outbox = Create(provider, connectionString);
        var mailId = Guid.NewGuid();

        Assert.True(await outbox.EnqueueAsync(mailId, 1, Guid.Empty, Payload));
        Assert.False(await outbox.EnqueueAsync(mailId, 1, Guid.Empty, Payload));

        Assert.Equal(1, (await outbox.GetStatusAsync()).PendingCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 確保した行は他のワーカーが拾えない(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var outbox = Create(provider, connectionString);
        await outbox.EnqueueAsync(Guid.NewGuid(), 1, Guid.Empty, Payload);

        Assert.NotNull(await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5)));
        Assert.Null(await outbox.ClaimAsync("worker-2", TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れの確保は解放される(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **ワーカーが落ちてもメールは失われない**
        var outbox = Create(provider, connectionString);
        await outbox.EnqueueAsync(Guid.NewGuid(), 1, Guid.Empty, Payload);

        // **負の確保時間で「既に期限切れ」を作る。** 実時間を待たない
        Assert.NotNull(await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(-5)));
        Assert.Equal(1, await outbox.ReleaseExpiredLocksAsync());
        Assert.NotNull(await outbox.ClaimAsync("worker-2", TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 送り直すたびに回数が増える(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var outbox = Create(provider, connectionString);
        var mailId = Guid.NewGuid();
        await outbox.EnqueueAsync(mailId, 1, Guid.Empty, Payload);

        await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5));

        // **過去の時刻を入れて、すぐ拾える状態に戻す**
        await outbox.RescheduleAsync(mailId, DateTime.UtcNow.AddMinutes(-1), "繋がらない");

        var claimed = await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        Assert.Equal(1, claimed.RetryCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 分離した行は拾われず件数に出る(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var outbox = Create(provider, connectionString);
        var mailId = Guid.NewGuid();
        await outbox.EnqueueAsync(mailId, 1, Guid.Empty, Payload);
        await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5));

        await outbox.DeadLetterAsync(mailId, "宛先が無い");

        Assert.Null(await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5)));

        // ⚠️ **COUNT の型が 3 者で違う**（SQL Server は int、他は bigint）。
        // 受け側で吸収できていることを、ここで押さえる
        var status = await outbox.GetStatusAsync();
        Assert.Equal(0, status.PendingCount);
        Assert.Equal(1, status.DeadLetterCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 滞留の時刻はUTCで返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **列が時間帯を持たないので、読んだままだと Unspecified になる**
        var outbox = Create(provider, connectionString);
        await outbox.EnqueueAsync(Guid.NewGuid(), 1, Guid.Empty, Payload);

        var status = await outbox.GetStatusAsync();
        Assert.NotNull(status.OldestPendingAt);
        Assert.Equal(DateTimeKind.Utc, status.OldestPendingAt.Value.Kind);
    }
}
