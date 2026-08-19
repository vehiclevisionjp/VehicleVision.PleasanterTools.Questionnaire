using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>使い終えた proof-of-work の課題を 3 RDBMS で覚えられること。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **覚えられていなければ、1 回解くだけで何度でも投稿できる**（Issue #55）。
/// ここが 3 者で動くことを確かめないと、**特定の RDBMS でだけ使い回しが通る**。
/// </para>
/// </remarks>
public class AltchaChallengeStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static AltchaChallengeStore Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        return new AltchaChallengeStore(new DbConnectionFactory(provider, connectionString));
    }

    private static string NewChallenge() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 覚えたものは使われていると分かる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var challenge = NewChallenge();

        Assert.False(await store.ExistsAsync(challenge).ConfigureAwait(true));

        await store.StoreAsync(challenge, DateTime.Now.AddHours(1)).ConfigureAwait(true);

        Assert.True(await store.ExistsAsync(challenge).ConfigureAwait(true));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じものを二度覚えても落ちない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **書き込みの競合で送信を落とさない。**
        // 先に入っていること自体が「もう使われている」の答え
        var store = Create(provider, connectionString);
        var challenge = NewChallenge();

        await store.StoreAsync(challenge, DateTime.Now.AddHours(1)).ConfigureAwait(true);
        await store.StoreAsync(challenge, DateTime.Now.AddHours(1)).ConfigureAwait(true);

        Assert.True(await store.ExistsAsync(challenge).ConfigureAwait(true));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限を過ぎたものは消える(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **課題そのものに期限がある。** 過ぎたものを覚えておくと増え続ける
        var store = Create(provider, connectionString);
        var expired = NewChallenge();
        var alive = NewChallenge();

        await store.StoreAsync(expired, DateTime.Now.AddMinutes(-5)).ConfigureAwait(true);
        await store.StoreAsync(alive, DateTime.Now.AddHours(1)).ConfigureAwait(true);

        // **読むついでに掃除する**
        Assert.False(await store.ExistsAsync(expired).ConfigureAwait(true));
        Assert.True(await store.ExistsAsync(alive).ConfigureAwait(true));
    }
}
