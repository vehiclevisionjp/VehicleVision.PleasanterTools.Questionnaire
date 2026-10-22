using System.Diagnostics;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class MigrationCommandApplicationTests
{
    [Fact]
    public async Task 本体のコマンドが固有スキーマの状態と適用結果を従来どおり返す()
    {
        var directory = NewDirectory();
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "migration.db")};Pooling=False";
            var status = await RunAsync(directory, connectionString, "--migrate-status");
            Assert.Equal(1, status.ExitCode);
            Assert.Empty(status.Error);
            Assert.StartsWith($"Pending migrations (Sqlite):{Environment.NewLine}", status.Output);
            Assert.Contains("M0001_InitialSchema", status.Output, StringComparison.Ordinal);
            await using (var connection = new DbConnectionFactory(DatabaseProvider.Sqlite, connectionString).Create())
            {
                var tables = await connection.QueryAsync<string>(
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
                Assert.Equal("VersionInfo", Assert.Single(tables));
            }
            var pending = QuestionnaireDatabase.Instance.Migrator.PendingMigrations(DatabaseProvider.Sqlite, connectionString);
            var expectedPending = $"Pending migrations (Sqlite):{Environment.NewLine}"
                + string.Concat(pending.Select(migration => $"  - {migration}{Environment.NewLine}"));
            Assert.Equal(expectedPending, status.Output);

            var apply = await RunAsync(directory, connectionString, "--migrate-status", "--migrate");
            Assert.Equal(0, apply.ExitCode);
            Assert.Empty(apply.Error);
            Assert.Equal(expectedPending + $"Applied {pending.Count} migration(s).{Environment.NewLine}", apply.Output);

            foreach (var argument in new[] { "--migrate-status", "--migrate" })
            {
                var applied = await RunAsync(directory, connectionString, argument);
                Assert.Equal(0, applied.ExitCode);
                Assert.Empty(applied.Error);
                Assert.Equal($"All migrations are applied (Sqlite).{Environment.NewLine}", applied.Output);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task 一時SQLiteを開けないときは待機の出力と終了コードを維持する()
    {
        var directory = NewDirectory();
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "missing", "migration.db")};Pooling=False";
            var result = await RunAsync(directory, connectionString, "--migrate", "--wait-for-db=1");
            Assert.Equal(2, result.ExitCode);
            Assert.Equal($"Waiting for the database (up to 1s)...{Environment.NewLine}", result.Output);
            Assert.StartsWith("Database not reachable after 1s: ", result.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"q-migrate-{Guid.NewGuid():N}")).FullName;

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string directory, string connectionString, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(PleasanterSsoOptions).Assembly.Location);
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        // 実機の設定・接続先は引き継がず、このテスト専用の SQLite だけを使用する。
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("QUESTIONNAIRE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }
        start.Environment["QUESTIONNAIRE_DB_PROVIDER"] = "Sqlite";
        start.Environment["QUESTIONNAIRE_DB_CONNECTIONSTRING"] = connectionString;
        start.Environment["ASPNETCORE_CONTENTROOT"] = directory;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("コマンドを起動できませんでした。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
