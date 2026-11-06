using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>ルックアップの専用資格情報の保存・暗号化・失効を 4 RDBMS で確かめる（Issue #821）。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 秘密の値は試験用の架空のもの。
/// </remarks>
public class LookupCredentialStoreTests
{
    private const string Secret = "test-lookup-secret-0001";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static SecretCipherAdapter NewCipher() =>
        new(new SecretProtector(SecretProtector.GenerateKey()));

    private static (ILookupCredentialStore Store, DbConnectionFactory Factory) Create(
        DatabaseProvider provider,
        string connectionString,
        ISecretCipher? cipher = null)
    {
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        return (new LookupCredentialStore(factory, cipher ?? NewCipher()), factory);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 登録した秘密を取り出せ_DBには平文が入らない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, factory) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();

        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");

        var resolved = await store.ResolveSecretAsync(surveyId, id);
        Assert.Equal(LookupSecretStatus.Available, resolved.Status);
        Assert.Equal(Secret, resolved.Secret);

        await using var connection = factory.Create();
        await connection.OpenAsync();
        var stored = await connection.QuerySingleAsync<string>(SqlDialect.Format(
            provider, "SELECT [SecretCipher] FROM [LookupCredentials] WHERE [CredentialId] = @Id"),
            new { Id = id });
        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.StartsWith("v2$", stored, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 状態の問い合わせは秘密を返さず_設定済みの有無と有効状態だけ(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");

        var status = await store.FindStatusAsync(surveyId, id);

        Assert.NotNull(status);
        Assert.True(status.IsEnabled);
        Assert.Equal(LookupSourceKind.Pleasanter, status.SourceKind);
        Assert.Equal("admin-1", status.UpdatedBy);
        Assert.Null(await store.FindStatusAsync(surveyId, "none"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 入れ替えると新しい秘密になる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);

        Assert.True(await store.RotateAsync(surveyId, id, "rotated-secret-0002", "admin-2"));

        var resolved = await store.ResolveSecretAsync(surveyId, id);
        Assert.Equal("rotated-secret-0002", resolved.Secret);
        Assert.Equal("admin-2", (await store.FindStatusAsync(surveyId, id))!.UpdatedBy);
        Assert.False(await store.RotateAsync(surveyId, "none", "x", null));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 停止すると取り出せず_再開すると取り出せる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);

        Assert.True(await store.SetEnabledAsync(surveyId, id, false, "admin-1"));

        // **公開し直さずに効く。** 停止中は秘密を渡さない
        var stopped = await store.ResolveSecretAsync(surveyId, id);
        Assert.Equal(LookupSecretStatus.Disabled, stopped.Status);
        Assert.Null(stopped.Secret);
        Assert.False((await store.FindStatusAsync(surveyId, id))!.IsEnabled);

        Assert.True(await store.SetEnabledAsync(surveyId, id, true, "admin-1"));
        Assert.Equal(LookupSecretStatus.Available, (await store.ResolveSecretAsync(surveyId, id)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 解除すると無くなる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);

        Assert.True(await store.DeleteAsync(surveyId, id));

        Assert.Equal(LookupSecretStatus.NotFound, (await store.ResolveSecretAsync(surveyId, id)).Status);
        Assert.False(await store.DeleteAsync(surveyId, id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 別のアンケートからは読めず_書き換えられない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);

        Assert.Equal(LookupSecretStatus.NotFound, (await store.ResolveSecretAsync(other, id)).Status);
        Assert.Null(await store.FindStatusAsync(other, id));
        Assert.False(await store.RotateAsync(other, id, "x", null));
        Assert.False(await store.SetEnabledAsync(other, id, false, null));
        Assert.False(await store.DeleteAsync(other, id));
        Assert.Equal(LookupSecretStatus.Available, (await store.ResolveSecretAsync(surveyId, id)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 暗号文を別の行へ貼り替えても復号できない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, factory) = Create(provider, connectionString, NewCipher());
        var surveyId = Guid.NewGuid();
        var first = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);
        var second = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, "second-secret", null);

        await using var connection = factory.Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            SqlDialect.Format(
                provider,
                "UPDATE [LookupCredentials] SET [SecretCipher] = "
                + "(SELECT c.[SecretCipher] FROM (SELECT [SecretCipher] FROM [LookupCredentials] "
                + "WHERE [CredentialId] = @First) c) WHERE [CredentialId] = @Second"),
            new { First = first, Second = second });

        // **文脈（資格情報 ID）が違うので読めない。** 例外ではなく状態で返す
        Assert.Equal(
            LookupSecretStatus.Undecryptable,
            (await store.ResolveSecretAsync(surveyId, second)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 鍵が違えば復号できない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, factory) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var id = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, null);

        var otherKey = new LookupCredentialStore(factory, NewCipher());

        Assert.Equal(
            LookupSecretStatus.Undecryptable,
            (await otherKey.ResolveSecretAsync(surveyId, id)).Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧は自分のアンケートの状態だけを返し_秘密を含まない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, _) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var first = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");
        var second = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-2");
        await store.CreateAsync(Guid.NewGuid(), LookupSourceKind.Pleasanter, Secret, "other");
        await store.SetEnabledAsync(surveyId, second, enabled: false, "admin-2");

        var list = await store.ListStatusAsync(surveyId);

        Assert.Equal(2, list.Count);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), list.Select(item => item.CredentialId).Order(StringComparer.Ordinal));
        Assert.False(list.Single(item => item.CredentialId == second).IsEnabled);
        Assert.Empty(await store.ListStatusAsync(Guid.NewGuid()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保存済みの下書きか公開した版が参照している資格情報だけを参照中として返す(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (store, factory) = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var orphan = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");
        var inDraft = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");
        var inVersion = await store.CreateAsync(surveyId, LookupSourceKind.Pleasanter, Secret, "admin-1");
        var otherSurvey = Guid.NewGuid();
        var foreign = await store.CreateAsync(otherSurvey, LookupSourceKind.Pleasanter, Secret, "other");

        // 孤立だけのあいだは、参照中は空
        Assert.Empty(await store.ListReferencedAsync(surveyId));

        await using var connection = factory.Create();
        await connection.OpenAsync();
        const string InsertLookup =
            "INSERT INTO [SurveyLookups] ([SurveyId], [LookupId], [SortOrder], [DefinitionJson]) "
            + "VALUES (@SurveyId, 'l1', 0, @Json)";
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, InsertLookup),
            new { SurveyId = surveyId, Json = $"{{\"lookupId\":\"l1\",\"credentialId\":\"{inDraft}\"}}" });
        await connection.ExecuteAsync(
            SqlDialect.Format(provider,
                "INSERT INTO [SurveyVersions] ([SurveyId], [Version], [DefinitionJson], [MappingJson], [PublishedAt]) "
                + "VALUES (@SurveyId, 1, @Json, '{}', @Now)"),
            new
            {
                SurveyId = surveyId,
                Json = $"{{\"lookups\":[{{\"credentialId\": \"{inVersion}\"}}]}}",
                Now = DateTime.UtcNow,
            });

        // ほかのアンケートの定義が参照していても数えない
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, InsertLookup),
            new { SurveyId = otherSurvey, Json = $"{{\"credentialId\":\"{orphan}\"}}" });

        var referenced = await store.ListReferencedAsync(surveyId);

        Assert.Equal(
            new[] { inDraft, inVersion }.Order(StringComparer.Ordinal),
            referenced.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(orphan, referenced);
        Assert.DoesNotContain(foreign, referenced);
        Assert.Empty(await store.ListReferencedAsync(Guid.NewGuid()));
    }
}
