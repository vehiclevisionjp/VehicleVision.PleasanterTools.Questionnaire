using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>再編集リンクのトークンを 4 RDBMS で確かめる（Issue #202）。</summary>
/// <remarks>**環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**</remarks>
public class ResponseEditTokenStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static IResponseEditTokenStore Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        using var connection = factory.Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "ResponseEditTokens")}";
        command.ExecuteNonQuery();

        return new ResponseEditTokenStore(factory);
    }

    private static string NewHash() => $"hash-{Guid.NewGuid():N}";

    private static DateTime Now => DateTime.UtcNow;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 発行して引き換えられる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var hash = NewHash();

        await store.SaveAsync(hash, "tok-1", Guid.NewGuid(), Now.AddDays(7));

        Assert.Equal("tok-1", await store.RedeemAsync(hash, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限内は何度でも引き換えられる(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **1 回で失効させない**（2026-09-14 決定）。
        // メールのリンク検査が先に開いても、本人が使えること
        var store = Create(provider, connectionString);
        var hash = NewHash();
        await store.SaveAsync(hash, "tok-1", Guid.NewGuid(), Now.AddDays(7));

        Assert.Equal("tok-1", await store.RedeemAsync(hash, Now));
        Assert.Equal("tok-1", await store.RedeemAsync(hash, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限を過ぎたら引き換えられない(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var hash = NewHash();
        await store.SaveAsync(hash, "tok-1", Guid.NewGuid(), Now.AddMinutes(-1));

        Assert.Null(await store.RedeemAsync(hash, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 知らないトークンは引き換えられない(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);

        Assert.Null(await store.RedeemAsync(NewHash(), Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task アンケート単位で一括失効できる(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **漏れたときに、調べずに止められること**
        var store = Create(provider, connectionString);
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();

        var mine = NewHash();
        var theirs = NewHash();
        await store.SaveAsync(mine, "tok-1", target, Now.AddDays(7));
        await store.SaveAsync(theirs, "tok-2", other, Now.AddDays(7));

        Assert.Equal(1, await store.RevokeBySurveyAsync(target));

        Assert.Null(await store.RedeemAsync(mine, Now));

        // **他のアンケートのリンクは巻き込まない**
        Assert.Equal("tok-2", await store.RedeemAsync(theirs, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れだけを掃除する(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var expired = NewHash();
        var alive = NewHash();
        await store.SaveAsync(expired, "tok-1", Guid.NewGuid(), Now.AddMinutes(-1));
        await store.SaveAsync(alive, "tok-2", Guid.NewGuid(), Now.AddDays(7));

        Assert.Equal(1, await store.DeleteExpiredAsync(Now));

        Assert.Equal("tok-2", await store.RedeemAsync(alive, Now));
    }
}
