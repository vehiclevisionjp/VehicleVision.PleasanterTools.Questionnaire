using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者への知らせの読み書きを 3 RDBMS で確かめる（Issue #80）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **ここで確かめたいのは方言の差。** 「未読の同じ種類なら足す」を
/// <c>UPDATE</c> の戻り行数で決めているが、**MySQL は既定で「一致した行数」ではなく
/// 「変わった行数」を返す**（設定でも変わる）。<c>SUM</c> は空の表で <c>NULL</c> になる。
/// どちらもこの試験が無いと実機でだけ壊れる。
/// </para>
/// </remarks>
public class AdminNotificationStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static readonly DateTime Now = new(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>デッドレター（<c>AdminNotificationKind.DeadLettered</c>）。</summary>
    private const int DeadLettered = 1;

    /// <summary>Pleasanter の認証失敗（<c>PleasanterUnauthorized</c>）。</summary>
    private const int Unauthorized = 4;

    private static async Task<IAdminNotificationStore> CreateAsync(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **前の試験の行を消してから始める。** 件数を数えるので、残っていると揺れる
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, "DELETE FROM [AdminNotifications]")).ConfigureAwait(false);

        return new AdminNotificationStore(factory);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じ知らせは行を増やさず件数が増える(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **1 件ずつ行を作らない。** Pleasanter が壊れれば回答は全部デッドレターになる
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.RaiseAsync(DeadLettered, surveyId, Now).ConfigureAwait(true);
        await store.RaiseAsync(DeadLettered, surveyId, Now.AddMinutes(5)).ConfigureAwait(true);
        await store.RaiseAsync(DeadLettered, surveyId, Now.AddMinutes(9)).ConfigureAwait(true);

        var row = Assert.Single(await store.ListAsync(new AdminNotificationQuery())
            .ConfigureAwait(true));

        Assert.Equal(3, row.Count);
        Assert.Equal(Now, DbTime.AsUtc(row.FirstOccurredAt));
        Assert.Equal(Now.AddMinutes(9), DbTime.AsUtc(row.LastOccurredAt));
        Assert.Null(row.ReadAt);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 種類とアンケートが違えば別の行になる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        await store.RaiseAsync(DeadLettered, Guid.NewGuid(), Now).ConfigureAwait(true);
        await store.RaiseAsync(DeadLettered, Guid.NewGuid(), Now).ConfigureAwait(true);

        // **アンケートに紐づかない知らせは Guid.Empty。** NULL にしていない
        await store.RaiseAsync(Unauthorized, Guid.Empty, Now).ConfigureAwait(true);

        var rows = await store.ListAsync(new AdminNotificationQuery()).ConfigureAwait(true);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Count));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 未読の件数は行数ではなく起きた回数(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **1 行が「デッドレター 300 件」を表すことがある**
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.RaiseAsync(DeadLettered, surveyId, Now).ConfigureAwait(true);
        await store.RaiseAsync(DeadLettered, surveyId, Now).ConfigureAwait(true);
        await store.RaiseAsync(Unauthorized, Guid.Empty, Now).ConfigureAwait(true);

        Assert.Equal(3, await store.UnreadCountAsync().ConfigureAwait(true));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 何も無ければ未読は零件(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **空の表で SUM を取ると NULL。** int で受けていると RDBMS によってだけ落ちる
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        Assert.Equal(0, await store.UnreadCountAsync().ConfigureAwait(true));
        Assert.Empty(await store.ListAsync(new AdminNotificationQuery()).ConfigureAwait(true));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 既読にすると未読は零になり次の知らせは新しい行になる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.RaiseAsync(DeadLettered, surveyId, Now).ConfigureAwait(true);

        Assert.Equal(1, await store.MarkAllReadAsync(Now.AddMinutes(1)).ConfigureAwait(true));
        Assert.Equal(0, await store.UnreadCountAsync().ConfigureAwait(true));

        // **既読の行へ足さない。** 一度気付いた後に起きたことは、新しい知らせとして出す
        await store.RaiseAsync(DeadLettered, surveyId, Now.AddMinutes(2)).ConfigureAwait(true);

        Assert.Equal(1, await store.UnreadCountAsync().ConfigureAwait(true));
        Assert.Equal(
            2,
            (await store.ListAsync(new AdminNotificationQuery()).ConfigureAwait(true)).Count);

        var unread = await store
            .ListAsync(new AdminNotificationQuery { UnreadOnly = true })
            .ConfigureAwait(true);

        Assert.Single(unread);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 掃除では未読を消さない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **気付く前に消えたら、溜める意味が無い**
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        await store.RaiseAsync(DeadLettered, Guid.NewGuid(), Now.AddDays(-100))
            .ConfigureAwait(true);
        await store.MarkAllReadAsync(Now.AddDays(-100)).ConfigureAwait(true);

        // 既読にした後で立った、同じくらい古い未読
        await store.RaiseAsync(Unauthorized, Guid.Empty, Now.AddDays(-100)).ConfigureAwait(true);

        var deleted = await store.DeleteOlderThanAsync(Now.AddDays(-90)).ConfigureAwait(true);

        Assert.Equal(1, deleted);
        var row = Assert.Single(await store.ListAsync(new AdminNotificationQuery())
            .ConfigureAwait(true));
        Assert.Equal(Unauthorized, row.Kind);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 消えたアンケートの知らせも読める(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **アンケートが消えていても、知らせが残っていることは見えなければならない**
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        await store.RaiseAsync(DeadLettered, Guid.NewGuid(), Now).ConfigureAwait(true);

        var row = Assert.Single(await store.ListAsync(new AdminNotificationQuery())
            .ConfigureAwait(true));

        Assert.Null(row.SurveyTitle);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ページ送りで取りこぼさない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **時刻は秒までしか持たない。** 同じ秒の行が並ぶので、
        // 並びを 1 本の列だけで決めると送るたびに順番が変わる
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        foreach (var _ in Enumerable.Range(0, 6))
        {
            await store.RaiseAsync(DeadLettered, Guid.NewGuid(), Now).ConfigureAwait(true);
        }

        var first = await store.ListAsync(new AdminNotificationQuery { Limit = 3 })
            .ConfigureAwait(true);
        var second = await store
            .ListAsync(new AdminNotificationQuery { Limit = 3, Offset = 3 })
            .ConfigureAwait(true);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Empty(first.Select(row => row.AdminNotificationId)
            .Intersect(second.Select(row => row.AdminNotificationId)));
    }
}
