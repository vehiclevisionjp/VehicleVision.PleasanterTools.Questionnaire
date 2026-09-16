using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class SurveyAssetStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "questionnaire-assets-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 未設定ならDB保存()
    {
        var options = SurveyAssetStorageOptions.FromConfiguration(Configuration());

        Assert.Equal(SurveyAssetStorageKind.Database, options.Kind);
    }

    [Fact]
    public void パス保存にはディレクトリが必須()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => SurveyAssetStorageOptions.FromConfiguration(Configuration(
                (SurveyAssetStorageOptions.StoreSetting, "Path"))));

        Assert.Contains(SurveyAssetStorageOptions.PathSetting, exception.Message);
    }

    [Fact]
    public void AzureBlobはマネージドIDと接続文字列の両方を同時に使えない()
    {
        Assert.Throws<InvalidOperationException>(
            () => SurveyAssetStorageOptions.FromConfiguration(Configuration(
                (SurveyAssetStorageOptions.StoreSetting, "AzureBlob"),
                (SurveyAssetStorageOptions.AzureContainerUriSetting,
                    "https://storage.example.test/assets"),
                (SurveyAssetStorageOptions.AzureConnectionStringSetting, "UseDevelopmentStorage=true"),
                (SurveyAssetStorageOptions.AzureContainerNameSetting, "assets"))));
    }

    [Fact]
    public void AzureBlobは接続文字列なしならマネージドIDを使う()
    {
        var options = SurveyAssetStorageOptions.FromConfiguration(Configuration(
            (SurveyAssetStorageOptions.StoreSetting, "AzureBlob"),
            (SurveyAssetStorageOptions.AzureContainerUriSetting,
                "https://storage.example.test/assets")));

        Assert.Null(options.AzureConnectionString);
        Assert.IsType<AzureBlobAssetObjectStore>(options.CreateObjectStore());
    }

    [Fact]
    public void S3互換の接続項目を読める()
    {
        var options = SurveyAssetStorageOptions.FromConfiguration(Configuration(
            (SurveyAssetStorageOptions.StoreSetting, "S3"),
            (SurveyAssetStorageOptions.S3BucketSetting, "survey-assets"),
            (SurveyAssetStorageOptions.S3ServiceUrlSetting, "https://s3.example.test"),
            (SurveyAssetStorageOptions.S3ForcePathStyleSetting, "true"),
            (SurveyAssetStorageOptions.S3RegionSetting, "ap-northeast-1"),
            (SurveyAssetStorageOptions.S3AccessKeySetting, "access"),
            (SurveyAssetStorageOptions.S3SecretKeySetting, "secret")));

        Assert.Equal(SurveyAssetStorageKind.S3, options.Kind);
        Assert.Equal("survey-assets", options.S3Bucket);
        Assert.Equal("https://s3.example.test", options.S3ServiceUrl);
        Assert.True(options.S3ForcePathStyle);
        Assert.Equal("ap-northeast-1", options.S3Region);
        Assert.Equal("access", options.S3AccessKey);
        Assert.Equal("secret", options.S3SecretKey);
    }

    [Fact]
    public void S3はアクセスキーなしなら既定の資格情報探索を使う()
    {
        var options = SurveyAssetStorageOptions.FromConfiguration(Configuration(
            (SurveyAssetStorageOptions.StoreSetting, "S3"),
            (SurveyAssetStorageOptions.S3BucketSetting, "survey-assets"),
            (SurveyAssetStorageOptions.S3RegionSetting, "ap-northeast-1")));

        Assert.Null(options.S3AccessKey);
        Assert.Null(options.S3SecretKey);
        Assert.IsType<S3AssetObjectStore>(options.CreateObjectStore());
    }

    [Fact]
    public async Task パス保存はGUID名だけで読み書きして削除できる()
    {
        var store = new PathAssetObjectStore(directory);
        var key = Guid.NewGuid();
        byte[] content = [0x00, 0x2f, 0xff];

        await store.PutAsync(key, content, CancellationToken.None);

        Assert.Equal(
            [key.ToString("N")],
            Directory.GetFiles(directory).Select(path => Path.GetFileName(path)!).ToArray());
        Assert.Equal(
            content,
            await store.FindAsync(key, CancellationToken.None));

        await store.DeleteAsync(key, CancellationToken.None);

        Assert.Null(await store.FindAsync(key, CancellationToken.None));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
