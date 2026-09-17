using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>アンケート資産の保存先。</summary>
public enum SurveyAssetStorageKind
{
    Database,
    Path,
    AzureBlob,
    S3,
}

/// <summary>アンケート資産の保存先設定。</summary>
public sealed class SurveyAssetStorageOptions
{
    public const string StoreSetting = "QUESTIONNAIRE_ASSET_STORE";
    public const string PathSetting = "QUESTIONNAIRE_ASSET_PATH";
    public const string AzureContainerUriSetting = "QUESTIONNAIRE_ASSET_AZURE_CONTAINERURI";
    public const string AzureConnectionStringSetting = "QUESTIONNAIRE_ASSET_AZURE_CONNECTIONSTRING";
    public const string AzureContainerNameSetting = "QUESTIONNAIRE_ASSET_AZURE_CONTAINERNAME";
    public const string S3BucketSetting = "QUESTIONNAIRE_ASSET_S3_BUCKET";
    public const string S3ServiceUrlSetting = "QUESTIONNAIRE_ASSET_S3_SERVICEURL";
    public const string S3ForcePathStyleSetting = "QUESTIONNAIRE_ASSET_S3_FORCEPATHSTYLE";
    public const string S3RegionSetting = "QUESTIONNAIRE_ASSET_S3_REGION";
    public const string S3AccessKeySetting = "QUESTIONNAIRE_ASSET_S3_ACCESSKEY";
    public const string S3SecretKeySetting = "QUESTIONNAIRE_ASSET_S3_SECRETKEY";

    public SurveyAssetStorageKind Kind { get; private init; }

    public string? Path { get; private init; }

    public string? AzureContainerUri { get; private init; }

    public string? AzureConnectionString { get; private init; }

    public string? AzureContainerName { get; private init; }

    public string? S3Bucket { get; private init; }

    public string? S3ServiceUrl { get; private init; }

    public bool S3ForcePathStyle { get; private init; }

    public string? S3Region { get; private init; }

    public string? S3AccessKey { get; private init; }

    public string? S3SecretKey { get; private init; }

    public static SurveyAssetStorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawKind = configuration[StoreSetting];
        var kind = string.IsNullOrWhiteSpace(rawKind)
            ? SurveyAssetStorageKind.Database
            : Enum.TryParse<SurveyAssetStorageKind>(rawKind, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"{StoreSetting} には Database / Path / AzureBlob / S3 のいずれかを指定する");

        var options = new SurveyAssetStorageOptions
        {
            Kind = kind,
            Path = Value(configuration, PathSetting),
            AzureContainerUri = Value(configuration, AzureContainerUriSetting),
            AzureConnectionString = Value(configuration, AzureConnectionStringSetting),
            AzureContainerName = Value(configuration, AzureContainerNameSetting),
            S3Bucket = Value(configuration, S3BucketSetting),
            S3ServiceUrl = Value(configuration, S3ServiceUrlSetting),
            S3ForcePathStyle = Bool(configuration, S3ForcePathStyleSetting),
            S3Region = Value(configuration, S3RegionSetting),
            S3AccessKey = Value(configuration, S3AccessKeySetting),
            S3SecretKey = Value(configuration, S3SecretKeySetting),
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (Kind == SurveyAssetStorageKind.Path && Path is null)
        {
            throw Missing(PathSetting);
        }

        if (Kind == SurveyAssetStorageKind.AzureBlob)
        {
            var usesManagedIdentity = AzureContainerUri is not null;
            var usesConnectionString =
                AzureConnectionString is not null && AzureContainerName is not null;
            if (usesManagedIdentity == usesConnectionString)
            {
                throw new InvalidOperationException(
                    $"{AzureContainerUriSetting}、または "
                    + $"{AzureConnectionStringSetting} と {AzureContainerNameSetting} の組を指定する");
            }
        }

        if (Kind == SurveyAssetStorageKind.S3)
        {
            if (S3Bucket is null)
            {
                throw Missing(S3BucketSetting);
            }

            if ((S3AccessKey is null) != (S3SecretKey is null))
            {
                throw new InvalidOperationException(
                    $"{S3AccessKeySetting} と {S3SecretKeySetting} は両方を指定する");
            }

            if (S3ServiceUrl is null && S3Region is null)
            {
                throw new InvalidOperationException(
                    $"{S3ServiceUrlSetting} または {S3RegionSetting} を指定する");
            }
        }
    }

    internal IAssetObjectStore CreateObjectStore()
    {
        if (Kind == SurveyAssetStorageKind.Path)
        {
            return new PathAssetObjectStore(Path!);
        }

        if (Kind == SurveyAssetStorageKind.AzureBlob)
        {
            var client = AzureContainerUri is not null
                ? new BlobContainerClient(new Uri(AzureContainerUri), new DefaultAzureCredential())
                : new BlobContainerClient(AzureConnectionString, AzureContainerName);
            return new AzureBlobAssetObjectStore(client);
        }

        if (Kind == SurveyAssetStorageKind.S3)
        {
            var config = new AmazonS3Config
            {
                ForcePathStyle = S3ForcePathStyle,
            };
            if (S3ServiceUrl is not null)
            {
                config.ServiceURL = S3ServiceUrl;
                config.AuthenticationRegion = S3Region;
            }
            else
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(S3Region!);
            }

            AmazonS3Client client = S3AccessKey is null
                ? new AmazonS3Client(config)
                : new AmazonS3Client(
                    new BasicAWSCredentials(S3AccessKey, S3SecretKey), config);
            return new S3AssetObjectStore(client, S3Bucket!);
        }

        throw new InvalidOperationException("DB 保存には外部の実体ストアを作れない");
    }

    private static string? Value(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

    private static bool Bool(IConfiguration configuration, string key)
    {
        var raw = Value(configuration, key);
        if (raw is null)
        {
            return false;
        }

        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"{key} には true または false を指定する");
    }

    private static InvalidOperationException Missing(string key) =>
        new($"{key} が設定されていない");
}

/// <summary>設定に応じた資産ストアを登録する。</summary>
public static class SurveyAssetStorageRegistration
{
    public static IServiceCollection AddSurveyAssetStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = SurveyAssetStorageOptions.FromConfiguration(configuration);
        services.AddSingleton(options);

        if (options.Kind == SurveyAssetStorageKind.Database)
        {
            services.AddSingleton<ISurveyAssetStore, SurveyAssetStore>();
            return services;
        }

        services.AddSingleton(options.CreateObjectStore());
        services.AddSingleton<ISurveyAssetStore, ExternalSurveyAssetStore>();
        return services;
    }
}
