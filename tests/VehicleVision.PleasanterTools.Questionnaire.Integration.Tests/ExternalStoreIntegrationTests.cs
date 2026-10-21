using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>Docker の互換実装へ実際に置き、読み、消せることを確かめる。</summary>
/// <remarks>
/// 対象ごとの環境変数が無い場合は実行しない。起動方法は
/// <c>_documents/開発環境.md</c> を参照すること。
/// </remarks>
public class ExternalStoreIntegrationTests
{
    private static string? DatabaseConnectionString =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_STORAGE_DB_CONNECTIONSTRING");

    [Fact]
    public async Task Azuriteで資産を読み書きし完全削除で実体も消える()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE_AZURITE_CONNECTIONSTRING");
        if (DatabaseConnectionString is null || connectionString is null)
        {
            return;
        }

        var containerName = $"survey-assets-{Guid.NewGuid():N}";
        var container = new BlobContainerClient(connectionString, containerName);
        await container.CreateAsync();

        try
        {
            await VerifyAssetLifecycleAsync(Configuration(
                (SurveyAssetStorageOptions.StoreSetting, "AzureBlob"),
                (SurveyAssetStorageOptions.AzureConnectionStringSetting, connectionString),
                (SurveyAssetStorageOptions.AzureContainerNameSetting, containerName)));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [Fact]
    public async Task MinIOはパス形式で資産を読み書きし完全削除で実体も消える()
    {
        var serviceUrl = Environment.GetEnvironmentVariable("QUESTIONNAIRE_MINIO_SERVICE_URL");
        var accessKey = Environment.GetEnvironmentVariable("QUESTIONNAIRE_MINIO_ACCESS_KEY");
        var secretKey = Environment.GetEnvironmentVariable("QUESTIONNAIRE_MINIO_SECRET_KEY");
        if (DatabaseConnectionString is null
            || serviceUrl is null
            || accessKey is null
            || secretKey is null)
        {
            return;
        }

        var bucket = $"survey-assets-{Guid.NewGuid():N}";
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
            });
        await client.PutBucketAsync(bucket);

        try
        {
            var configuration = Configuration(
                (SurveyAssetStorageOptions.StoreSetting, "S3"),
                (SurveyAssetStorageOptions.S3BucketSetting, bucket),
                (SurveyAssetStorageOptions.S3ServiceUrlSetting, serviceUrl),
                // **MinIO は仮想ホスト形式を受けない。**
                // 実際の PUT/GET/DELETE を通して、この設定を外せないようにする
                (SurveyAssetStorageOptions.S3ForcePathStyleSetting, "true"),
                (SurveyAssetStorageOptions.S3RegionSetting, "us-east-1"),
                (SurveyAssetStorageOptions.S3AccessKeySetting, accessKey),
                (SurveyAssetStorageOptions.S3SecretKeySetting, secretKey));

            var options = SurveyAssetStorageOptions.FromConfiguration(configuration);
            Assert.True(options.S3ForcePathStyle);
            await VerifyAssetLifecycleAsync(configuration);
        }
        finally
        {
            await client.DeleteBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task Valkeyのセッションは作成から失効まで通る()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE_VALKEY_CONNECTIONSTRING");
        if (connectionString is null)
        {
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"questionnaire:test:{Guid.NewGuid():N}:";
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new RedisAdminSessionStore(connection, time, prefix);
        var userId = Guid.NewGuid();
        var sso = new SessionSso(
            new SamlSessionKeys("name-id", "index-1"),
            new PleasanterSsoKeys(1, 7, time.GetUtcNow().AddMinutes(-1)));

        try
        {
            var id = await store.CreateAsync(
                userId, AdminSessionKinds.Session, TimeSpan.FromMinutes(5),
                ipAddress: "192.0.2.1", userAgent: "integration-test", sso: sso);
            var pendingId = await store.CreateAsync(userId, AdminSessionKinds.Pending, TimeSpan.FromMinutes(5));

            // ID の現物ではなく SHA-256 が識別子
            Assert.NotEqual(id, store.HandleOf(id));
            Assert.Equal(64, store.HandleOf(id).Length);

            var found = await store.FindAsync(id);
            Assert.Equal(userId, found!.UserId);
            Assert.Equal(AdminSessionKinds.Session, found.Kind);
            Assert.Equal(7, found.Pleasanter!.UserId);
            Assert.Equal(AdminSessionKinds.Pending, (await store.FindAsync(pendingId))!.Kind);

            var keys = await store.GetSamlKeysAsync(id);
            Assert.Equal(new SamlSessionKeys("name-id", "index-1"), keys);

            // 一覧は Session だけ（途中状態は出ない）
            var listed = Assert.Single(await store.ListForUserAsync(userId));
            Assert.Equal(store.HandleOf(id), listed.Handle);
            Assert.Equal(SessionVia.Saml, listed.Via);
            Assert.Equal("192.0.2.1", listed.IpAddress);

            // Pleasanter の確認時刻の更新は、寿命を変えない
            var verified = time.GetUtcNow();
            await store.TouchPleasanterVerifiedAsync(id, verified);
            Assert.Equal(verified.ToUnixTimeSeconds(), (await store.FindAsync(id))!.Pleasanter!.VerifiedAt.ToUnixTimeSeconds());
            var ttl = await connection.GetDatabase().KeyTimeToLiveAsync($"{prefix}entry:{store.HandleOf(id)}");
            Assert.InRange(ttl!.Value.TotalSeconds, 200, 300);

            // 他人の識別子では消えない。持ち主の組なら消える
            Assert.False(await store.DeleteByHandleAsync(Guid.NewGuid(), store.HandleOf(id)));
            Assert.NotNull(await store.FindAsync(id));
            Assert.True(await store.DeleteByHandleAsync(userId, store.HandleOf(id)));
            Assert.Null(await store.FindAsync(id));
            Assert.Empty(await store.ListForUserAsync(userId));

            // SAML の鍵での失効
            var second = await store.CreateAsync(
                userId, AdminSessionKinds.Session, TimeSpan.FromMinutes(5),
                sso: new SessionSso(Saml: new SamlSessionKeys("name-id", "index-2")));
            Assert.Equal(0, await store.DeleteBySamlKeysAsync("name-id", "no-such-index"));
            Assert.Equal(1, await store.DeleteBySamlKeysAsync("name-id", "index-2"));
            Assert.Null(await store.FindAsync(second));

            // 一括失効（いまの 1 つだけ残す）
            var keep = await store.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromMinutes(5));
            var other = await store.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromMinutes(5));
            await store.DeleteForUserAsync(userId, keep);
            Assert.NotNull(await store.FindAsync(keep));
            Assert.Null(await store.FindAsync(other));
            Assert.Null(await store.FindAsync(pendingId));

            // 期限を過ぎたものは見つからない
            time.Advance(TimeSpan.FromMinutes(6));
            Assert.Null(await store.FindAsync(keep));
            Assert.Empty(await store.ListForUserAsync(userId));
        }
        finally
        {
            var database = connection.GetDatabase();
            foreach (var key in connection.GetServer(connection.GetEndPoints()[0]).Keys(pattern: prefix + "*"))
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }

    private static async Task VerifyAssetLifecycleAsync(IConfiguration configuration)
    {
        var factory = new DbConnectionFactory(
            DatabaseProvider.SqlServer,
            DatabaseConnectionString!);
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.SqlServer, DatabaseConnectionString!);

        var surveyId = Guid.NewGuid();
        const string title = "外部ストアの検証";
        await new SurveyRepository(factory).SaveAsync(new SurveyRecord(
            surveyId,
            $"external-store-{surveyId:N}",
            title,
            PleasanterSiteId: 1,
            ResponseJsonColumn: null,
            Status: 0,
            PublishedVersion: null,
            ArchivedAt: DbTime.UtcNowTruncated()));

        var objects = SurveyAssetStorageOptions.FromConfiguration(configuration).CreateObjectStore();
        var assets = new ExternalSurveyAssetStore(factory, objects);
        byte[] content = [0x00, 0x2f, 0xff, 0x42];

        var assetId = await assets.AddAsync(
            surveyId, "application/octet-stream", "asset.bin", content);

        Assert.Equal(content, (await assets.FindAsync(surveyId, assetId))!.Content);
        Assert.Equal(content, await objects.FindAsync(assetId, CancellationToken.None));

        var result = await new SurveyDeletionStore(factory, assets).DeleteAsync(surveyId, title);

        Assert.Equal(SurveyDeletionStatus.Deleted, result.Status);
        Assert.Null(await objects.FindAsync(assetId, CancellationToken.None));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
