using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>添付を弾いた記録の読み書きを 3 RDBMS で確かめる（Issue #39）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **<c>SUM</c> の戻り型が 3 者で割れる**（空の表では <c>NULL</c>、
/// SQL Server は <c>int</c>、他は違う型で返る）。ここが無いと実機で一度も動かない。
/// </para>
/// </remarks>
public class AttachmentRejectionStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static readonly DateTime Now = new(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);

    private static async Task<IAttachmentRejectionStore> CreateAsync(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **前の試験の行を消してから始める。** 件数を数えるので、残っていると揺れる
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, "DELETE FROM [AttachmentRejections]")).ConfigureAwait(false);

        return new AttachmentRejectionStore(factory);
    }

    private static AttachmentRejectionEntry Entry(
        Guid surveyId,
        int reason = 0,
        string? questionId = "q1",
        int fileCount = 1,
        int minutesAgo = 0) =>
        new(Now.AddMinutes(-minutesAgo), surveyId, questionId, reason, fileCount);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 書いた記録を新しい順に読める(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.WriteAsync(
        [
            Entry(surveyId, reason: 0, minutesAgo: 10),
            Entry(surveyId, reason: 6, questionId: null, fileCount: 3),
        ]).ConfigureAwait(true);

        var rows = await store.ListAsync(new AttachmentRejectionQuery()).ConfigureAwait(true);

        Assert.Equal(2, rows.Count);
        Assert.Equal(6, rows[0].Reason);
        Assert.Equal(3, rows[0].FileCount);
        // **設問に紐づかない理由は NULL のまま返る**
        Assert.Null(rows[0].QuestionId);
        Assert.Equal("q1", rows[1].QuestionId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 消えたアンケートの記録も読める(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **アンケートが消えていても、弾いた記録が残っていることは見えなければならない**
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        await store.WriteAsync([Entry(Guid.NewGuid())]).ConfigureAwait(true);

        var row = Assert.Single(await store.ListAsync(new AttachmentRejectionQuery())
            .ConfigureAwait(true));

        Assert.Null(row.SurveyTitle);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 直近の件数はファイルの数で数える(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **行数ではない。** 1 行が複数件を表す
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.WriteAsync(
        [
            Entry(surveyId, fileCount: 3),
            Entry(surveyId, reason: 2, fileCount: 4),
            // **古い分は数えない**
            Entry(surveyId, reason: 3, fileCount: 99, minutesAgo: 60),
        ]).ConfigureAwait(true);

        var count = await store.CountSinceAsync(Now.AddMinutes(-30)).ConfigureAwait(true);

        Assert.Equal(7, count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 何も無ければ零件で返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **空の表で SUM を取ると NULL。** int で受けていると RDBMS によってだけ落ちる
        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        Assert.Equal(0, await store.CountSinceAsync(Now.AddDays(-7)).ConfigureAwait(true));
        Assert.Empty(await store.ListAsync(new AttachmentRejectionQuery()).ConfigureAwait(true));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限を過ぎた記録を消せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await store.WriteAsync(
        [
            Entry(surveyId, minutesAgo: 0),
            Entry(surveyId, reason: 1, minutesAgo: 120),
        ]).ConfigureAwait(true);

        var deleted = await store.DeleteOlderThanAsync(Now.AddMinutes(-60)).ConfigureAwait(true);

        Assert.Equal(1, deleted);
        var row = Assert.Single(await store.ListAsync(new AttachmentRejectionQuery())
            .ConfigureAwait(true));
        Assert.Equal(0, row.Reason);
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
        var surveyId = Guid.NewGuid();

        await store.WriteAsync([.. Enumerable.Range(0, 6).Select(_ => Entry(surveyId))])
            .ConfigureAwait(true);

        var first = await store.ListAsync(new AttachmentRejectionQuery { Limit = 3 })
            .ConfigureAwait(true);
        var second = await store
            .ListAsync(new AttachmentRejectionQuery { Limit = 3, Offset = 3 })
            .ConfigureAwait(true);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
    }
}
