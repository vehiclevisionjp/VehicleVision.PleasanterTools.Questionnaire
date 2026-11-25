using Dapper;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// DB の <c>ButtonLabel</c> 列は残して無視する（Issue #1079）。
/// </summary>
/// <remarks>
/// 移行は足さない。<b>ストアは列を読まず書かない</b>ので、残っている値は保存しても変わらず、
/// 戻すときにそのまま使える。
/// </remarks>
public class SsoSettingStoreLegacyButtonLabelTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SAMLの設定を保存しても旧ButtonLabel列は変わらない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var store = new SamlSettingStore(factory);

        await AssertColumnKeptAsync(
            factory,
            provider,
            "SamlSettings",
            "SamlSettingId",
            () => store.SaveAsync(new SamlSettingValues { Enabled = "false", EntityId = "kept-check" }),
            async () => _ = await store.GetAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Pleasanterの設定を保存しても旧ButtonLabel列は変わらない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var store = new PleasanterSsoSettingStore(factory);

        await AssertColumnKeptAsync(
            factory,
            provider,
            "PleasanterSsoSettings",
            "PleasanterSsoSettingId",
            () => store.SaveAsync(new PleasanterSsoSettingValues { Enabled = "false" }),
            async () => _ = await store.GetAsync());
    }

    private static async Task AssertColumnKeptAsync(
        DbConnectionFactory factory,
        DatabaseProvider provider,
        string table,
        string key,
        Func<Task> save,
        Func<Task> load)
    {
        string Sql(string sql) => SqlDialect.Format(provider, sql);

        await using var connection = factory.Create();
        await connection.OpenAsync();
        var original = await connection.ExecuteScalarAsync<string?>(
            Sql($"SELECT [ButtonLabel] FROM [{table}] WHERE [{key}] = 1"));

        try
        {
            await connection.ExecuteAsync(
                Sql($"UPDATE [{table}] SET [ButtonLabel] = @Label WHERE [{key}] = 1"),
                new { Label = "移行前の表示" });

            await save();
            await load();

            var kept = await connection.ExecuteScalarAsync<string?>(
                Sql($"SELECT [ButtonLabel] FROM [{table}] WHERE [{key}] = 1"));
            Assert.Equal("移行前の表示", kept);
        }
        finally
        {
            await connection.ExecuteAsync(
                Sql($"UPDATE [{table}] SET [ButtonLabel] = @Label WHERE [{key}] = 1"),
                new { Label = original });
        }
    }
}
