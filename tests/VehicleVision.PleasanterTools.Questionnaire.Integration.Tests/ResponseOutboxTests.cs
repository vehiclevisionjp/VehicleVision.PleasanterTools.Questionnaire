using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>送信待ちテーブルの読み書きを 3 RDBMS で確かめる。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class ResponseOutboxTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (IResponseOutbox Outbox, IResponseTokenStore Tokens) Create(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **検証用の DB なので、毎回まっさらにしてから始める。**
        // 前の実行が残した行があると「確保できるはず」の判定が揺れる
        using var connection = factory.Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "Responses")}";
        command.ExecuteNonQuery();

        return (new ResponseOutbox(factory), new ResponseTokenStore(factory));
    }

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保存して確保して消せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);
        var token = NewToken();
        var surveyId = Guid.NewGuid();

        await outbox.SaveAsync(token, surveyId, 3, "{\"a\":1}");

        var claimed = await ClaimUntilAsync(outbox, token, "worker-1");
        Assert.NotNull(claimed);
        Assert.Equal(3, claimed.SurveyVersion);
        Assert.Equal("{\"a\":1}", claimed.PayloadJson);
        Assert.Equal(surveyId, claimed.SurveyId);

        await outbox.CompleteAsync(token);

        // **送信できたら消す。** 「念のため」残さない
        Assert.Null(await outbox.FindPayloadAsync(token));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じトークンなら上書きする(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);
        var token = NewToken();
        var surveyId = Guid.NewGuid();

        await outbox.SaveAsync(token, surveyId, 1, "{\"v\":1}");
        await outbox.SaveAsync(token, surveyId, 2, "{\"v\":2}");

        // **キューではない。回答ごとに 1 行で、編集は上書き**
        Assert.Equal("{\"v\":2}", await outbox.FindPayloadAsync(token));

        await outbox.CompleteAsync(token);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 確保中の行は他のワーカーが拾わない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);
        var token = NewToken();
        await outbox.SaveAsync(token, Guid.NewGuid(), 1, "{}");

        var first = await ClaimUntilAsync(outbox, token, "worker-1");
        Assert.NotNull(first);

        // 同じ行は取れない（他の行が取れることはある）
        var second = await outbox.ClaimAsync("worker-2", TimeSpan.FromMinutes(5));
        Assert.NotEqual(token, second?.ResponseToken);

        await outbox.CompleteAsync(token);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れの確保は解放される(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);
        var token = NewToken();
        await outbox.SaveAsync(token, Guid.NewGuid(), 1, "{}");

        // **ワーカーが落ちても回答は失われない**
        var claimed = await ClaimUntilAsync(outbox, token, "worker-1", TimeSpan.FromMinutes(-5));
        Assert.NotNull(claimed);

        var released = await outbox.ReleaseExpiredLocksAsync();
        Assert.True(released >= 1);

        var again = await ClaimUntilAsync(outbox, token, "worker-2");
        Assert.NotNull(again);

        await outbox.CompleteAsync(token);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 再送予定とデッドレターを記録できる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);
        var token = NewToken();
        await outbox.SaveAsync(token, Guid.NewGuid(), 1, "{}");

        await outbox.RescheduleAsync(token, DateTime.UtcNow.AddHours(1), "一時的な失敗");

        // 予定時刻より前なので確保できない
        Assert.NotEqual(token, (await outbox.ClaimAsync("worker-1", TimeSpan.FromMinutes(5)))?.ResponseToken);

        await outbox.DeadLetterAsync(token, "恒久的な失敗");

        // **デッドレターは滞留の件数に数えない**
        var pending = await outbox.CountPendingAsync();
        await outbox.CompleteAsync(token);
        var afterDelete = await outbox.CountPendingAsync();

        Assert.Equal(pending, afterDelete);
    }

    /// <summary>
    /// 期限を過ぎたデッドレターだけを消せること（Issue #85）。
    /// ⚠️ **送信待ちを巻き込むと、まだ届けられる回答を捨てることになる。**
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限を過ぎたデッドレターだけを消す(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = Create(provider, connectionString);

        var dead = NewToken();
        await outbox.SaveAsync(dead, Guid.NewGuid(), 1, "{}");
        await outbox.DeadLetterAsync(dead, "恒久的な失敗");

        var pending = NewToken();
        await outbox.SaveAsync(pending, Guid.NewGuid(), 1, "{}");

        // **まだ期限が来ていないものは消さない**
        Assert.Equal(0, await outbox.DeleteDeadLettersOlderThanAsync(DateTime.UtcNow.AddDays(-1)));
        Assert.NotNull(await outbox.FindPayloadAsync(dead));

        // **期限を過ぎたデッドレターだけ消える**
        Assert.Equal(1, await outbox.DeleteDeadLettersOlderThanAsync(DateTime.UtcNow.AddDays(1)));
        Assert.Null(await outbox.FindPayloadAsync(dead));

        // ⚠️ **送信待ちは残る。** 状態を条件に入れてある
        Assert.NotNull(await outbox.FindPayloadAsync(pending));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task トークンとReferenceIdの対応を保存できる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (_, tokens) = Create(provider, connectionString);
        var token = NewToken();
        var surveyId = Guid.NewGuid();

        // **まだ Create していない状態を表せる**
        await tokens.SaveAsync(token, surveyId, referenceId: null);
        Assert.Null(await tokens.FindReferenceIdAsync(token));

        await tokens.SaveAsync(token, surveyId, referenceId: 4321);
        Assert.Equal(4321, await tokens.FindReferenceIdAsync(token));
    }

    /// <summary>1 件だけ確保する。テーブルは空にしてあるので、取れるのは対象の行だけ。</summary>
    private static Task<PendingResponse?> ClaimUntilAsync(
        IResponseOutbox outbox,
        string token,
        string lockedBy,
        TimeSpan? lockDuration = null) =>
        outbox.ClaimAsync(lockedBy, lockDuration ?? TimeSpan.FromMinutes(5));
}
