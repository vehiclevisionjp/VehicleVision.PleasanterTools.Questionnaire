using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// <c>QUESTIONNAIRE_*_FILE</c> で指定されたファイルを設定値として読み込む。
/// </summary>
/// <remarks>
/// Docker secrets と Kubernetes Secret のファイルマウントへ対応する。
/// 値は例外へ含めず、読み取り元のファイル内容もログへ渡さない。
/// </remarks>
public sealed class SecretFileConfigurationProvider : ConfigurationProvider
{
    /// <summary>受け付ける環境変数の接頭辞。</summary>
    public const string Prefix = "QUESTIONNAIRE_";

    /// <summary>ファイル指定を表す環境変数の接尾辞。</summary>
    public const string FileSuffix = "_FILE";

    /// <summary>秘密ファイルに許す最大サイズ。</summary>
    public const int MaximumFileSizeBytes = 64 * 1024;

    /// <inheritdoc />
    public override void Load()
    {
        var variables = Environment.GetEnvironmentVariables();

        foreach (System.Collections.DictionaryEntry entry in variables)
        {
            if (entry.Key is not string fileKey
                || entry.Value is not string path
                || !fileKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || !fileKey.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = fileKey[..^FileSuffix.Length];
            var directValue = Environment.GetEnvironmentVariable(key);
            if (directValue is not null)
            {
                throw new InvalidOperationException(
                    $"環境変数 '{key}' と '{fileKey}' は同時に指定できません。");
            }

            Data[key] = ReadValue(key, path);
        }
    }

    private static string ReadValue(string key, string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"環境変数 '{key}_FILE' には絶対パスを指定してください。");
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new InvalidOperationException(
                    $"環境変数 '{key}_FILE' のファイルが見つかりません: {path}");
            }

            if (file.Length > MaximumFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"環境変数 '{key}_FILE' のファイルが大きすぎます: {path}");
            }

            var value = File.ReadAllText(path);
            if (value.Length > 0 && value[^1] == '\n')
            {
                value = value[..^1];
                if (value.Length > 0 && value[^1] == '\r')
                {
                    value = value[..^1];
                }
            }

            if (value.Length == 0)
            {
                throw new InvalidOperationException(
                    $"環境変数 '{key}_FILE' のファイルは空です: {path}");
            }

            return value;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"環境変数 '{key}_FILE' のファイルを読み取れません: {path}");
        }
    }
}

/// <summary><see cref="SecretFileConfigurationProvider"/> を構成へ追加する拡張。</summary>
public static class SecretFileConfigurationExtensions
{
    /// <summary>環境変数で指定された秘密ファイルを設定値として積む。</summary>
    public static IConfigurationBuilder AddSecretFileEnvironmentVariables(
        this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new SecretFileConfigurationSource());
    }

    private sealed class SecretFileConfigurationSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new SecretFileConfigurationProvider();
    }
}
