using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>Pleasanter シングルサインオン設定の保存先を、4 RDBMS で確かめる（Issue #464）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **ここで確かめたいのは、単体試験では代わりが効かない 2 つ。**
/// SELECT の列と record の結び付きが実行時に崩れないこと（Dapper は実際に読むまで分からない）と、
/// **移行の直後はどの項目も空であること。** 空でないと、移行しただけで入口が開く。
/// </para>
/// <para>
/// ⚠️ **1 行だけの表を共有する。** 書いた試験は最後に空へ戻す。
/// </para>
/// </remarks>
public class PleasanterSsoSettingStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static PleasanterSsoSettingStore Create(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        return new PleasanterSsoSettingStore(new DbConnectionFactory(provider, connectionString));
    }

    private static readonly PleasanterSsoSettingValues Empty = new();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 全項目を入れて読み直すと同じ値が返る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);

        // **列ごとに違う値を入れる。** 同じ値だと、列の取り違えに気付けない。
        // URL は列の上限いっぱいまで入れ、3 者で切られないことも見る
        var longUrl = "https://pleasanter.example.jp/" + new string('a', 2048 - 30);
        var values = new PleasanterSsoSettingValues
        {
            Enabled = "true",
            InternalBaseUrl = longUrl,
            LoginUrl = "https://pleasanter.example.jp/users/login",
            LogoutUrl = "https://pleasanter.example.jp/users/logout",
            Method = "ExtendedSql",
            SqlName = "QuestionnaireWhoAmI",
            CookieNames = ".AspNetCore.Cookies,Pleasanter_SessionGuid",
            UnknownUser = "Register",
            RegisterRole = "Editor",
            RevalidateMinutes = "5",
            TimeoutSeconds = "10",
            ButtonLabel = "社内ポータルでログイン",
        };

        try
        {
            await store.SaveAsync(values);

            var loaded = await store.GetAsync();

            Assert.Equal(values, loaded);
        }
        finally
        {
            await store.SaveAsync(Empty);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 空に戻すとどの項目も空で返る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **移行直後の状態と同じ。** 外部設定が無ければ既定値（無効）で動く
        var store = Create(provider, connectionString);

        await store.SaveAsync(Empty);
        var loaded = await store.GetAsync();

        Assert.Equal(Empty, loaded);
    }
}
