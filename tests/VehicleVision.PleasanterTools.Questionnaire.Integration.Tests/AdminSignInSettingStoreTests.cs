using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理画面のログイン方式の設定の保存先を、4 RDBMS で確かめる（Issue #1056）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する**
/// （SQLite 以外の接続先は <see cref="DatabaseMigrationTests.Providers"/> の設定に従う）。
/// </para>
/// <para>
/// **確かめたいのは 2 つ。** SELECT の列と record の結び付きが実行時に崩れないこと、
/// **移行の直後は空（＝隠さない）であること。** 空でないと、移行しただけで合言葉の入口が閉じる。
/// </para>
/// <para>⚠️ **1 行だけの表を共有する。** 書いた試験は最後に空へ戻す。</para>
/// </remarks>
public class AdminSignInSettingStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static AdminSignInSettingStore Create(DatabaseProvider provider, string connectionString)
    {
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        return new AdminSignInSettingStore(new DbConnectionFactory(provider, connectionString));
    }

    private static readonly AdminSignInSettingValues Empty = new();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 書いた値を読み直すと同じ値が返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);

        try
        {
            foreach (var value in new[] { "false", "true" })
            {
                var values = new AdminSignInSettingValues { PasswordSignIn = value };
                await store.SaveAsync(values);

                Assert.Equal(values, await store.GetAsync());
            }
        }
        finally
        {
            await store.SaveAsync(Empty);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 空に戻すと空で返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **移行直後の状態と同じ。** 外部設定も保存値も無ければ既定（隠さない）で動く
        var store = Create(provider, connectionString);

        await store.SaveAsync(Empty);

        Assert.Equal(Empty, await store.GetAsync());
    }
}
