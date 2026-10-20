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
    /// <summary>FluentMigrator の RDBMS の名前と、Core.Data の <see cref="DatabaseProvider"/> の対応。</summary>
    private static readonly (string Name, DatabaseProvider Provider)[] Databases =
    [
        ("SqlServer", DatabaseProvider.SqlServer),
        ("Postgres", DatabaseProvider.PostgreSql),
        ("MySql", DatabaseProvider.MySql),
        ("SQLite", DatabaseProvider.Sqlite),
    ];

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

        foreach (var (name, provider) in Databases)
        {
            migration.IfDatabase(name).Execute.Sql(SqlDialect.Format(provider, copySql(provider)));
        }

        migration.Delete.Table(legacyTable);
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
