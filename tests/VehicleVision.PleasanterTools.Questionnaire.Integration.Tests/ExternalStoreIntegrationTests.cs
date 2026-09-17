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
    public async Task Valkeyのセッションは削除すると再取得できない()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE_VALKEY_CONNECTIONSTRING");
        if (connectionString is null)
        {
            return;
        }

        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var prefix = $"questionnaire:test:{Guid.NewGuid():N}:";
        var store = new RedisAdminSessionStore(connection, prefix);
        var entry = new AdminSessionEntry
        {
            AdminSessionId = Guid.NewGuid(),
            AdminUserId = Guid.NewGuid(),
            Kind = AdminSessionKind.Session,
            ProtectedPayload = "protected",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IpAddress = "192.0.2.1",
            UserAgent = "integration-test",
        };

        try
        {
            await store.CreateAsync(entry);
            Assert.Equal(entry, await store.FindAsync(entry.AdminSessionId));

            Assert.True(await store.DeleteAsync(entry.AdminSessionId));
            Assert.Null(await store.FindAsync(entry.AdminSessionId));
            Assert.Empty(await store.ListAsync(entry.AdminUserId));
        }
        finally
        {
            var database = connection.GetDatabase();
            await database.KeyDeleteAsync(
            [
                $"{prefix}{entry.AdminSessionId:N}",
                $"{prefix}user:{entry.AdminUserId:N}",
            ]);
        }
    }

    private static async Task VerifyAssetLifecycleAsync(IConfiguration configuration)
    {
        var factory = new DbConnectionFactory(
            DatabaseProvider.SqlServer,
            DatabaseConnectionString!);
        DatabaseMigrator.MigrateUp(DatabaseProvider.SqlServer, DatabaseConnectionString!);

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
