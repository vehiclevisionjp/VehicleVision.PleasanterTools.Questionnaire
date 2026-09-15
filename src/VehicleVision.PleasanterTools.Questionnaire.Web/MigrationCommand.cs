using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>コマンドラインからマイグレーションを当てる／状態を見る。</summary>
/// <remarks>
/// <para>
/// **アプリ起動時に自動適用しない**と決めてある
/// （<c>_documents/データモデル設計.md</c> 5 章。スケールアウト時に同時実行され得る）。
/// **その代わり、当てる口をアプリ本体と同じ実行ファイルに持たせる。**
/// 別の道具にすると接続文字列の読み方が二重になり、片方だけ直す事故が起きる。
/// </para>
/// <para>
///   当てる:   <c>dotnet run --project src/…Web -- --migrate</c><br/>
///   見るだけ: <c>dotnet run --project src/…Web -- --migrate-status</c>
/// </para>
/// <para>
/// **公開済みのイメージからも同じことができる**（<c>dotnet …Web.dll --migrate</c>）。
/// デプロイ手順とコンテナの起動順の両方で同じ口を使う。
/// </para>
/// </remarks>
public static class MigrationCommand
{
    /// <summary>コマンドラインの合図。</summary>
    public const string ApplyArgument = "--migrate";

    /// <summary>当てずに状態だけ見る合図。</summary>
    public const string StatusArgument = "--migrate-status";

    /// <summary>この起動がマイグレーションのためのものか。</summary>
    public static bool IsRequested(string[] args) =>
        args.Contains(ApplyArgument, StringComparer.Ordinal)
        || args.Contains(StatusArgument, StringComparer.Ordinal);

    /// <summary>当てる、または状態を見る。</summary>
    /// <returns>終了コード。0 なら「当たっている」。</returns>
    public static async Task<int> RunAsync(
        DatabaseProvider provider,
        string connectionString,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var apply = args.Contains(ApplyArgument, StringComparer.Ordinal);

        // **DB より先にこちらが動き出すことがある**（コンテナで一緒に立ち上げたとき）。
        // 既定は待たない。設定の誤りを起動の遅さで隠さないため
        var wait = WaitFor(args);
        if (wait > TimeSpan.Zero)
        {
            Console.WriteLine($"DB が繋がるのを待つ（最大 {wait.TotalSeconds:0} 秒）…");
            var failure = await DatabaseMigrator
                .WaitForDatabaseAsync(provider, connectionString, wait, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                await Console.Error.WriteLineAsync(
                    $"DB へ繋がらないまま {wait.TotalSeconds:0} 秒が過ぎた: {failure.Message}")
                    .ConfigureAwait(false);
                return 2;
            }
        }

        var pending = DatabaseMigrator.PendingMigrations(provider, connectionString);
        if (pending.Count == 0)
        {
            Console.WriteLine($"マイグレーションはすべて当たっている（{provider}）。");
            return 0;
        }

        Console.WriteLine($"当たっていないマイグレーション（{provider}）:");
        foreach (var migration in pending)
        {
            Console.WriteLine($"  - {migration}");
        }

        if (!apply)
        {
            // **見るだけのときは 0 を返さない。** CI や起動前の確認で気付けるようにする
            return 1;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        Console.WriteLine($"{pending.Count} 件を当てた。");
        return 0;
    }

    /// <summary><c>--wait-for-db=90</c> の秒数。無ければ待たない。</summary>
    private static TimeSpan WaitFor(string[] args)
    {
        const string prefix = "--wait-for-db=";

        var argument = args.FirstOrDefault(
            value => value.StartsWith(prefix, StringComparison.Ordinal));

        return argument is not null
            && int.TryParse(argument[prefix.Length..], out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
    }
}
