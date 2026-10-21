using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>
/// 旧表の行を Core.Data の共有表（<c>Core_</c> 接頭辞）へ写して、旧表を落とす移行の共通部品。
/// </summary>
/// <remarks>
/// <para>
/// <b>稼働中のデータがあり得るので、行を写してから落とす。</b> 写す SQL は RDBMS ごとに違う
/// （Guid・真偽値・文字列の照合順序）ので、呼び出し側が RDBMS ごとに書き分けて渡す。
/// </para>
/// <para>
/// <b>冪等。</b> 旧表が無ければ何もしない。Core の表に同じキーの行があれば、その行は写さない
/// （<c>WHERE NOT EXISTS</c> は呼び出し側の SQL に書く）。
/// </para>
/// <para>
/// <b>Core の共有表の後に動く。</b> Core.Data の移行は M10001〜M10007 なので、
/// この移行の番号は 100000 以降にして、必ず後に回す。
/// </para>
/// </remarks>
internal static class LegacyStoreMigration
{
    /// <summary>旧表の行を写して、旧表を落とす。旧表が無ければ何もしない。</summary>
    /// <param name="migration">呼び出す移行。</param>
    /// <param name="legacyTable">旧表の名前。</param>
    /// <param name="copySql">
    /// 写す SQL を RDBMS ごとに返す。<b>識別子は角括弧で囲んで書く</b>
    /// （<see cref="SqlDialect.Format"/> が各 RDBMS の引用符へ書き換える）。<b>文字列リテラルに角括弧を含めない。</b>
    /// </param>
    public static void CopyThenDrop(
        Migration migration,
        string legacyTable,
        Func<DatabaseProvider, string> copySql)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(copySql);

        if (!migration.Schema.Table(legacyTable).Exists())
        {
            return;
        }

        // **RDBMS は、実際に繋いでいる接続の型で見分ける。** FluentMigrator の `IfDatabase` は名前の一致で決まり、
        // 名前が合わないと**何も実行されないまま旧表だけが落ちて、行が消える**
        // （PostgreSQL の処理系の名前が `Postgres` ではなく `PostgreSQL` だったために、実際に行が写らなかった）。
        // 見分けられない接続なら例外にして、旧表を落とさない
        WithConnection(migration, (connection, transaction, provider) =>
            ExecuteNonQuery(connection, transaction, provider, copySql(provider)));

        migration.Delete.Table(legacyTable);
    }

    /// <summary>
    /// 実際に繋いでいる接続と、その RDBMS を渡して処理を実行する。
    /// <b>RDBMS は接続の型で見分ける</b>（<see cref="CopyThenDrop"/> の注意を参照）。見分けられなければ例外にする。
    /// </summary>
    /// <remarks>
    /// <b>移行は、前の式が失敗すると後ろの式（旧表を落とす <c>Delete.Table</c>）へ進まない。</b>
    /// 事前の検査（重複など）で例外を投げれば、旧表は残る。
    /// </remarks>
    public static void WithConnection(
        Migration migration,
        Action<System.Data.IDbConnection, System.Data.IDbTransaction?, DatabaseProvider> action)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(action);

        migration.Execute.WithConnection((connection, transaction) =>
            action(connection, transaction, ProviderOf(connection)));
    }

    /// <summary>SQL を 1 文実行する。<b>識別子は角括弧で囲んで書く</b>（<see cref="SqlDialect.Format"/> が書き換える）。</summary>
    public static int ExecuteNonQuery(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        DatabaseProvider provider,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlDialect.Format(provider, sql);
        return command.ExecuteNonQuery();
    }

    /// <summary>SELECT を実行し、各行を文字列の並びで返す（検査用。行数は少ない前提）。</summary>
    public static List<string[]> Query(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        DatabaseProvider provider,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlDialect.Format(provider, sql);
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? "(NULL)" : Convert.ToString(reader.GetValue(index)) ?? string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static DatabaseProvider ProviderOf(System.Data.IDbConnection connection)
    {
        var type = connection.GetType().FullName ?? string.Empty;
        return type switch
        {
            _ when type.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) => DatabaseProvider.PostgreSql,
            _ when type.Contains("MySql", StringComparison.OrdinalIgnoreCase) => DatabaseProvider.MySql,
            _ when type.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) => DatabaseProvider.Sqlite,
            _ when type.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) => DatabaseProvider.SqlServer,
            _ => throw new NotSupportedException($"移行で RDBMS を見分けられない接続: {type}"),
        };
    }

    /// <summary>
    /// 旧表の列を、写し先の列と比べられる形で書く。
    /// </summary>
    /// <remarks>
    /// <b>MySQL だけ、文字列の照合順序が旧表と Core の表で違う</b>（Core の表は <c>utf8mb4_0900_as_cs</c> へ直してある）。
    /// 揃えずに比べると「Illegal mix of collations」で落ちるので、旧表の列を Core の表と同じ文字セット・照合順序へ変えてから比べる。
    /// ほかの RDBMS はそのまま比べる。
    /// </remarks>
    public static string KeyOf(DatabaseProvider provider, string legacyColumn) =>
        provider is DatabaseProvider.MySql
            ? $"CONVERT({legacyColumn} USING utf8mb4) COLLATE utf8mb4_0900_as_cs"
            : legacyColumn;
}
