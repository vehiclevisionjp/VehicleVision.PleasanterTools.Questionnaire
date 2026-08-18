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
