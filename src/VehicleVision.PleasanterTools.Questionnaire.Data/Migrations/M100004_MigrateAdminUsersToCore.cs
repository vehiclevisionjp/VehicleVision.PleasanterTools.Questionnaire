using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>
/// 管理者・復旧コード・初期設定の印を、Core.Data の共有表（<c>Core_AdminUsers</c> ほか）へ写し、
/// 管理者のセッション（<c>AdminSessions</c>）は<b>写さずに落とす</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>稼働中のデータがあり得るので、行を写してから旧表を落とす</b>（<see cref="LegacyStoreMigration"/>）。
/// 旧 <c>AdminUsers</c>・<c>AdminRecoveryCodes</c>・<c>AdminSessions</c> を落とす。冪等（旧表が無ければ何もしない。
/// Core の表に同じキーの行があれば写さない）。
/// </para>
/// <para>列の対応は次のとおり。</para>
/// <list type="bullet">
/// <item><description><c>AdminUsers.AdminUserId</c> → <c>Core_AdminUsers.Id</c>（Guid のまま）。
/// <c>LoginIdKey</c> は <c>LoginId</c> を<b>各 RDBMS の <c>LOWER</c> で小文字にした値</b>（Core のストアは小文字で引く）。
/// <b>SQLite の <c>LOWER</c> は ASCII だけ</b>を小文字にする（ASCII 以外の大文字を含むログイン ID は、
/// Core の引き方（.NET の小文字化）と食い違う。メールアドレス形式のログイン ID は通常 ASCII）</description></item>
/// <item><description><c>Role</c>（整数）→ 名前（0=Editor・1=Administrator・2=SurveyAdministrator・3=UserAdministrator・4=Auditor。
/// <see cref="AdminRole"/> の値）。<b>知らない値があれば失敗する</b>（旧表は落ちない）</description></item>
/// <item><description><c>IsDisabled</c>（真偽値）→ 整数 0 / 1。<b>PostgreSQL だけ真偽値を <c>= true</c> で比べる</b></description></item>
/// <item><description><c>TotpSecretEncrypted</c> → <c>TotpSecretCipher</c>（<b>暗号文のまま</b>。復号しない。
/// 旧の暗号文は文脈を持たないので、アプリのアダプタが読み替えて復号する）。
/// 旧は <c>TotpEnabledAt</c> が無ければ 2 要素を未登録と扱ったので、その行の共有鍵は写さない</description></item>
/// <item><description><c>Language</c>（NULL = まだ選んでいない）→ 空文字（Core の列は NULL を許さない。空文字が「選んでいない」）</description></item>
/// <item><description><c>ResponseNotificationEnabled</c> → Q の表 <c>AdminUserSettings</c>（受け取る管理者の行だけ）。
/// <c>UpdatedAt</c>・<c>TotpEnabledAt</c> は Core の表に列が無く、アプリも読まないので写さない</description></item>
/// <item><description><c>AdminRecoveryCodes</c> → <c>Core_AdminRecoveryCodes</c>（<c>RecoveryCodeId</c>・<c>CreatedAt</c> は持たない。
/// ハッシュは PBKDF2 の 79 文字で、Core の列（128 文字）に収まる）</description></item>
/// <item><description>管理者が 1 人でも居れば、<c>Core_AdminSetup</c> に印（Id = 1）を入れる。
/// <b>入れないと、Core の「最初の管理者を作る」が稼働中の DB でもう一度通ってしまう</b></description></item>
/// </list>
/// <para>
/// <b>事前に検査する。</b>ログイン ID が大文字小文字違いだけで重複している（Core の一意索引に入らない）・
/// Core に同じログイン ID の別の管理者が居る・知らない役割の値がある、のいずれかなら、
/// <b>どの行かを示して失敗</b>する（何も書かず、旧表も落とさない）。
/// </para>
/// <para>
/// <b>旧セッションは写さない</b>（切り替え時に失効させる。管理者は再ログインする）。
/// 旧は外部キー（<c>AdminSessions → AdminUsers</c>）があるので、<b>セッション → 復旧コード → 管理者の順</b>に落とす。
/// 管理者を参照する外部キーはほかに無い（<c>AdminInvitations</c> などは外部キー無しで、Core の共有表へ移済み）。
/// </para>
/// </remarks>
[Migration(100004, "管理者・復旧コード・初期設定の印を Core.Data の共有表へ写す")]
public sealed class M100004_MigrateAdminUsersToCore : Migration
{
    public override void Up()
    {
        var hasUsers = Schema.Table("AdminUsers").Exists();
        var hasCodes = Schema.Table("AdminRecoveryCodes").Exists();
        var hasSessions = Schema.Table("AdminSessions").Exists();

        LegacyStoreMigration.WithConnection(this, (connection, transaction, provider) =>
        {
            if (hasUsers)
            {
                Verify(connection, transaction, provider);
                Run(connection, transaction, provider, CopyUsers(provider));
                Run(connection, transaction, provider, CopySettings(provider));
            }

            if (hasUsers && hasCodes)
            {
                Run(connection, transaction, provider, CopyRecoveryCodes(provider));
            }

            // **管理者が 1 人でも居れば印を入れる。** 旧表が無くなった後の再実行でも、印が無ければ入れる
            Run(
                connection,
                transaction,
                provider,
                "INSERT INTO [Core_AdminSetup] ([Id]) SELECT DISTINCT 1 FROM [Core_AdminUsers] "
                + "WHERE NOT EXISTS (SELECT 1 FROM [Core_AdminSetup])");
        });

        // **セッション → 復旧コード → 管理者**（外部キーの向きに従う）。ここまで例外が出なかった場合だけ落とす
        if (hasSessions)
        {
            Delete.Table("AdminSessions");
        }

        if (hasCodes)
        {
            Delete.Table("AdminRecoveryCodes");
        }

        if (hasUsers)
        {
            Delete.Table("AdminUsers");
        }
    }

    /// <summary>前方のみ。旧表の形へは戻さない。</summary>
    public override void Down()
    {
    }

    private static void Run(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        DatabaseProvider provider,
        string sql) => LegacyStoreMigration.ExecuteNonQuery(connection, transaction, provider, sql);

    /// <summary>写す前の検査。<b>問題があれば、どの行かを示して例外にする</b>（旧表は落ちない）。</summary>
    private static void Verify(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction? transaction,
        DatabaseProvider provider)
    {
        var problems = new List<string>();

        // ログイン ID が、大文字小文字違いだけで重複している（Core は小文字にした値の一意索引）
        var duplicates = LegacyStoreMigration.Query(
            connection,
            transaction,
            provider,
            "SELECT u.[AdminUserId], u.[LoginId] FROM [AdminUsers] u "
            + "WHERE EXISTS (SELECT 1 FROM [AdminUsers] d "
            + $"WHERE {LowerOf(provider, "d.[LoginId]")} = {LowerOf(provider, "u.[LoginId]")} "
            + "AND d.[AdminUserId] <> u.[AdminUserId]) "
            + $"ORDER BY {LowerOf(provider, "u.[LoginId]")}, u.[LoginId]");
        if (duplicates.Count > 0)
        {
            problems.Add(
                "ログイン ID が大文字小文字の違いだけで重複している管理者（Core は区別しない。"
                + "どちらかのログイン ID を変えてから、もう一度行う）: "
                + string.Join(", ", duplicates.Select(row => $"{row[1]}（AdminUserId={row[0]}）")));
        }

        // Core に、同じログイン ID の別の管理者が居る
        var conflicts = LegacyStoreMigration.Query(
            connection,
            transaction,
            provider,
            "SELECT u.[AdminUserId], u.[LoginId], c.[Id] FROM [AdminUsers] u "
            + $"INNER JOIN [Core_AdminUsers] c ON c.[LoginIdKey] = {LowerOf(provider, "u.[LoginId]")} "
            + $"WHERE c.[Id] <> {LegacyStoreMigration.KeyOf(provider, "u.[AdminUserId]")}");
        if (conflicts.Count > 0)
        {
            problems.Add(
                "Core の管理者表に、同じログイン ID の別の管理者が居る: "
                + string.Join(", ", conflicts.Select(row => $"{row[1]}（旧 AdminUserId={row[0]}、Core の Id={row[2]}）")));
        }

        // 役割の整数が、AdminRole の値のどれでもない
        var roles = LegacyStoreMigration.Query(
            connection,
            transaction,
            provider,
            "SELECT [AdminUserId], [LoginId], [Role] FROM [AdminUsers] WHERE [Role] NOT IN (0, 1, 2, 3, 4)");
        if (roles.Count > 0)
        {
            problems.Add(
                "知らない役割の値を持つ管理者: "
                + string.Join(", ", roles.Select(row => $"{row[1]}（AdminUserId={row[0]}、Role={row[2]}）")));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "管理者を Core.Data の共有表へ写せない。旧表は残してある。" + string.Join(" / ", problems));
        }
    }

    /// <summary>ログイン ID を小文字にした値（各 RDBMS の <c>LOWER</c>）。MySQL だけ、Core の表と同じ照合順序へ揃える。</summary>
    private static string LowerOf(DatabaseProvider provider, string column) =>
        provider is DatabaseProvider.MySql
            ? $"LOWER(CONVERT({column} USING utf8mb4)) COLLATE utf8mb4_0900_as_cs"
            : $"LOWER({column})";

    /// <summary>旧の真偽値の列が「真」であることを表す条件。<b>PostgreSQL だけ <c>= true</c></b>。</summary>
    private static string IsTrue(DatabaseProvider provider, string column) =>
        provider is DatabaseProvider.PostgreSql ? $"{column} = true" : $"{column} = 1";

    private static string CopyUsers(DatabaseProvider provider) =>
        "INSERT INTO [Core_AdminUsers] "
        + "([Id], [LoginId], [LoginIdKey], [PasswordHash], [Role], [IsDisabled], [FailedLoginCount], [LockedUntil], "
        + "[LastLoginAt], [CreatedAt], [TotpSecretCipher], [TotpLastTimeStep], [Language]) "
        + $"SELECT u.[AdminUserId], u.[LoginId], {LowerOf(provider, "u.[LoginId]")}, u.[PasswordHash], "
        + "CASE u.[Role] WHEN 0 THEN 'Editor' WHEN 1 THEN 'Administrator' WHEN 2 THEN 'SurveyAdministrator' "
        + "WHEN 3 THEN 'UserAdministrator' WHEN 4 THEN 'Auditor' END, "
        + $"CASE WHEN {IsTrue(provider, "u.[IsDisabled]")} THEN 1 ELSE 0 END, "
        + "u.[FailedLoginCount], u.[LockedUntil], u.[LastLoginAt], u.[CreatedAt], "
        + "CASE WHEN u.[TotpEnabledAt] IS NOT NULL AND u.[TotpSecretEncrypted] <> '' THEN u.[TotpSecretEncrypted] END, "
        + "u.[TotpLastTimeStep], COALESCE(u.[Language], '') "
        + "FROM [AdminUsers] u "
        + "WHERE NOT EXISTS (SELECT 1 FROM [Core_AdminUsers] c WHERE c.[Id] = "
        + LegacyStoreMigration.KeyOf(provider, "u.[AdminUserId]") + ")";

    private static string CopySettings(DatabaseProvider provider) =>
        "INSERT INTO [AdminUserSettings] ([AdminUserId], [ResponseNotificationEnabled]) "
        + "SELECT u.[AdminUserId], u.[ResponseNotificationEnabled] FROM [AdminUsers] u "
        + $"WHERE {IsTrue(provider, "u.[ResponseNotificationEnabled]")} "
        + "AND NOT EXISTS (SELECT 1 FROM [AdminUserSettings] s WHERE s.[AdminUserId] = "
        + LegacyStoreMigration.KeyOf(provider, "u.[AdminUserId]") + ")";

    private static string CopyRecoveryCodes(DatabaseProvider provider) =>
        "INSERT INTO [Core_AdminRecoveryCodes] ([UserId], [CodeHash], [UsedAt]) "
        + "SELECT r.[AdminUserId], r.[CodeHash], r.[UsedAt] FROM [AdminRecoveryCodes] r "
        // 管理者の居ない復旧コードは写さない（孤児）
        + "WHERE EXISTS (SELECT 1 FROM [Core_AdminUsers] c WHERE c.[Id] = "
        + LegacyStoreMigration.KeyOf(provider, "r.[AdminUserId]") + ") "
        + "AND NOT EXISTS (SELECT 1 FROM [Core_AdminRecoveryCodes] k WHERE k.[UserId] = "
        + LegacyStoreMigration.KeyOf(provider, "r.[AdminUserId]")
        + " AND k.[CodeHash] = " + LegacyStoreMigration.KeyOf(provider, "r.[CodeHash]") + ")";
}
