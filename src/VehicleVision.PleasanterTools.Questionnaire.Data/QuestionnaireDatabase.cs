using VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>
/// このアプリの DB の設定。DB の基盤（接続・暗号化検査・マイグレーションと排他）は Core.Data にあり、
/// <b>アプリごとに違う値はここで持って、引数で渡す</b>。
/// </summary>
public static class QuestionnaireDatabase
{
    /// <summary>SQLite の既定ファイル名。</summary>
    public const string DefaultSqliteFileName = "questionnaire.db";

    /// <summary>接続文字列を与える設定名。</summary>
    public const string ConnectionStringSetting = "QUESTIONNAIRE_DB_CONNECTIONSTRING";

    /// <summary>暗号化の検査を緩める設定名。</summary>
    public const string AllowInsecureSetting = "QUESTIONNAIRE_DB_ALLOW_INSECURE";

    /// <summary>
    /// 起動時のマイグレーションの排他に使う名前。
    /// **アプリごとに別にする。** 同じ DB サーバを複数のアプリが使うとき、名前が同じだと別のアプリのマイグレーションを待たせる。
    /// </summary>
    public const string MigrationLockName = "questionnaire-startup-migrations";

    /// <summary>このアプリのマイグレーション（M0001〜）を適用する。</summary>
    public static DatabaseMigrator Migrator { get; } =
        new(typeof(M0001_InitialSchema).Assembly, MigrationLockName);

    /// <summary>接続文字列を解決する（SQLite は <c>App_Data</c> 配下へ既定のファイルを置く）。</summary>
    public static string ResolveConnectionString(
        DatabaseProvider provider,
        string? configured,
        string contentRootPath) =>
        DatabaseConnectionString.Resolve(
            provider,
            configured,
            contentRootPath,
            DefaultSqliteFileName,
            ConnectionStringSetting);

    /// <summary>接続が暗号化されているかを見る。緩める設定が <c>true</c> のときだけ許す。</summary>
    public static void EnsureSecure(DatabaseProvider provider, string connectionString, bool allowInsecure) =>
        ConnectionSecurity.EnsureSecure(provider, connectionString, allowInsecure, AllowInsecureSetting);
}
