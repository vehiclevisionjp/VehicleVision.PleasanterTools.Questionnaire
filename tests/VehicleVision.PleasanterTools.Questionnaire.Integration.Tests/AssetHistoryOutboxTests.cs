using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>配布資料履歴の送信待ちを 4 RDBMS で確かめる。</summary>
public class AssetHistoryOutboxTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じ回答へ複数の出来事を積んで個別に完了できる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        using (var connection = factory.Create())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "AssetHistoryOutbox")}";
            command.ExecuteNonQuery();
        }

        var outbox = new AssetHistoryOutbox(factory);
        var surveyId = Guid.NewGuid();
        await outbox.EnqueueAsync(
            surveyId, 1, "token-1", AssetHistoryEventType.Revisit, null, null, DateTime.UtcNow);
        await outbox.EnqueueAsync(
            surveyId, 1, "token-1", AssetHistoryEventType.Download, Guid.NewGuid(), "guide.pdf", DateTime.UtcNow);

        var first = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(5));
        Assert.NotNull(first);
        await outbox.CompleteAsync(first.EventId);

        var second = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(5));
        Assert.NotNull(second);
        Assert.Equal("token-1", second.ResponseToken);
        Assert.NotEqual(first.EventId, second.EventId);
    }

    /// <summary>設定の誤りで送れなかったときは <c>RetryCount</c> を増やさず、一時的な失敗は増やすこと（Issue #853）。</summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 設定の誤りによる再送では再送回数を増やさない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        using (var connection = factory.Create())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "AssetHistoryOutbox")}";
            command.ExecuteNonQuery();
        }

        var outbox = new AssetHistoryOutbox(factory);
        await outbox.EnqueueAsync(
            Guid.NewGuid(), 1, "token-postpone", AssetHistoryEventType.Revisit, null, null, DateTime.UtcNow);

        for (var i = 0; i < 3; i++)
        {
            var claimed = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(5));
            Assert.NotNull(claimed);
            Assert.Equal(0, claimed.RetryCount);
            await outbox.PostponeAsync(claimed.EventId, DateTime.UtcNow.AddMinutes(-1), "設定の誤り");
        }

        var again = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(5));
        Assert.NotNull(again);
        await outbox.RescheduleAsync(again.EventId, DateTime.UtcNow.AddMinutes(-1), "一時的な失敗");

        var after = await outbox.ClaimAsync("worker", TimeSpan.FromMinutes(5));
        Assert.NotNull(after);
        Assert.Equal(1, after.RetryCount);
    }
}
