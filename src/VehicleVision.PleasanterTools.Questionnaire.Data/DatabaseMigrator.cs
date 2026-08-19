using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>スキーマを適用する。</summary>
/// <remarks>
/// <para>
/// **アプリ起動時に自動適用しない。** スケールアウト時に同時実行され得る
/// （<c>_documents/データモデル設計.md</c> 5 章）。デプロイ手順で明示的に流す。
/// </para>
/// <para>
/// **前方のみ。** ロールバック用のスクリプトに頼らない。
/// </para>
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>未適用のマイグレーションをすべて適用する。</summary>
    public static void MigrateUp(DatabaseProvider provider, string connectionString)
    {
        using var services = BuildServices(provider, connectionString);
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>適用済みかどうかを確かめる。</summary>
    public static bool HasPendingMigrations(DatabaseProvider provider, string connectionString)
    {
        using var services = BuildServices(provider, connectionString);
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IMigrationRunner>().HasMigrationsToApplyUp();
    }

    /// <summary>まだ当たっていないマイグレーションの名前。</summary>
    /// <remarks>
    /// **「当て忘れている」と言うだけでは直せない。** 何が足りないかまで見せる。
    /// **DB は読むだけで、版の表も作らない。**
    /// </remarks>
    public static IReadOnlyList<string> PendingMigrations(
        DatabaseProvider provider,
        string connectionString)
    {
        using var services = BuildServices(provider, connectionString);
        using var scope = services.CreateScope();

        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        var applied = scope.ServiceProvider.GetRequiredService<IVersionLoader>().VersionInfo;

        return [.. runner.MigrationLoader.LoadMigrations()
            .Where(migration => !applied.HasAppliedMigration(migration.Key))
            .Select(migration => $"{migration.Key} {migration.Value.Description}")];
    }

    /// <summary>DB が接続を受けるようになるまで待つ。</summary>
    /// <remarks>
    /// <para>
    /// **コンテナで一緒に立ち上げると、DB より先にこちらが動き出す。**
    /// マイグレーションだけは待ってから当てないと、順番だけの理由で失敗する。
    /// </para>
    /// <para>
    /// **待つのはここだけ。** アプリ本体は待たない（設定の誤りを起動の遅さで隠さないため）。
    /// </para>
    /// </remarks>
    /// <returns>待った末に繋がらなかったときの例外。繋がれば <c>null</c>。</returns>
    public static async Task<Exception?> WaitForDatabaseAsync(
        DatabaseProvider provider,
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var factory = new DbConnectionFactory(provider, connectionString);
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;

        while (true)
        {
            try
            {
                await using var connection = factory.Create();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // **繋がらない理由は待てば消えることも消えないこともある。**
                // 見分けが付かないので、期限まではどちらも待つ
                last = exception;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ServiceProvider BuildServices(DatabaseProvider provider, string connectionString) =>
        new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(builder =>
            {
                var configured = provider switch
                {
                    DatabaseProvider.SqlServer => builder.AddSqlServer(),
                    DatabaseProvider.PostgreSql => builder.AddPostgres(),
                    DatabaseProvider.MySql => builder.AddMySql8(),
                    _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
                };

                configured
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof(M0001_InitialSchema).Assembly).For.Migrations();
            })
            .BuildServiceProvider(validateScopes: false);
}
