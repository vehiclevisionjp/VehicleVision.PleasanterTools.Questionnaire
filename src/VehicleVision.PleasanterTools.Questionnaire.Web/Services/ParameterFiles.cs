using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// <c>App_Data/Parameters/*.json</c> を設定として積む（Issue #158）。
/// </summary>
/// <remarks>
/// <para>
/// **Pleasanter 本体と同じ置き場・同じ書き方**にしてある
/// （<c>App_Data/Parameters/README.md</c>）。**優先順位は
/// <c>{名前}.local.json</c> ＞ 環境変数 ＞ <c>{名前}.json</c>。**
/// </para>
/// <para>
/// **1 か所にまとめてある。** 設定ごとに「JSON → 環境変数」を繰り返すと、
/// 後から積んだ環境変数が**前の <c>.local.json</c> を追い越す**（実際に踏んだ）。
/// </para>
/// <para>
/// ⚠️ **ファイルのキーと環境変数の名前が違うものがある。**
/// <c>Pleasanter.json</c> は Pleasanter 本体に合わせた短い名前（<c>BaseUrl</c> など）で、
/// 環境変数は衝突を避けるため <c>QUESTIONNAIRE_*</c> で始まる。
/// **そのままでは片方しか効かない**ので、<see cref="Aliases"/> で正式な名前へ写している。
/// </para>
/// <para>
/// **値が <c>null</c> のキーは写さない。** <c>Pleasanter.json</c> の <c>ApiKey</c> は
/// 既定で <c>null</c>（＝ここへ書かせない）なので、
/// 写すと**環境変数で与えた API キーを空で塗り潰してしまう。**
/// </para>
/// </remarks>
public static partial class ParameterFiles
{
    /// <summary>既定の置き場。</summary>
    public const string DefaultDirectory = "App_Data/Parameters";

    /// <summary>既存の順序を保ち、用途別の設定ファイルを追加する。</summary>
    private static readonly ImmutableArray<string> Names =
        ["Service", "Pleasanter", "Security", "Analytics", "Rds", "Mail", "Authentication",
            "Session", "BinaryStorage", "VirusScan", "BackgroundService", "Script", "RateLimit",
            "General", "Monitoring"];

    /// <summary>本アプリが日時を解釈するときの既定タイムゾーン。</summary>
    public const string TimeZoneDefaultKey = "QUESTIONNAIRE_TIMEZONE_DEFAULT";

    /// <summary>実際に読んだ置き場。**起動時の記録に出す。**</summary>
    public const string DirectoryKey = "QUESTIONNAIRE_PARAMETERS_DIRECTORY";

    /// <summary>実際に見つかったファイル。**空なら 1 つも読めていない。**</summary>
    /// <remarks>
    /// ⚠️ **「読めているつもりで読めていない」を起動時に見せるため**（Issue #158 の再発防止）。
    /// 設定ファイルは <c>optional</c> なので、置き場を間違えても黙って既定で動いてしまう。
    /// </remarks>
    public const string FoundFilesKey = "QUESTIONNAIRE_PARAMETERS_FOUND";

    /// <summary>
    /// 設定を積む。**アプリの一番先で 1 度だけ呼ぶ**
    /// （以降の読み取りが全てこれを見る）。
    /// </summary>
    public static IConfigurationBuilder AddParameterFiles(
        this IConfigurationBuilder builder,
        string directory = DefaultDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // ⚠️ **絶対パスへ直してから積む。**
        // JSON の側（AddJsonFile）は content root を基準にするが、
        // 別名を写すために自分で読む側は現在のディレクトリを基準にする。
        // **基準が食い違うと、片方だけ読めて気付けない**（実際に踏んだ）
        var resolved = Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(BasePathOf(builder), directory);

        // ---- 1. {名前}.json（一番弱い）
        foreach (var name in Names)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.json"),
                optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: false));

        // ---- 2. 環境変数
        builder.AddEnvironmentVariables();

        // ---- 3. {名前}.local.json（一番強い。git 管理外）
        foreach (var name in Names)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.local.json"),
                optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: true));

        // ---- 4. 何を読んだかを残す（起動時の記録に出す）
        return builder.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(DirectoryKey, resolved),
            new KeyValuePair<string, string?>(FoundFilesKey, string.Join(" / ", FoundIn(resolved))),
        ]);
    }

    /// <summary>実際に置いてあるファイルの名前。</summary>
    public static IEnumerable<string> FoundIn(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        foreach (var name in Names)
        {
            foreach (var suffix in new[] { ".json", ".local.json" })
            {
                if (File.Exists(Path.Combine(directory, name + suffix)))
                {
                    yield return name + suffix;
                }
            }
        }
    }

    /// <summary>相対パスの基準。**content root に合わせる。**</summary>
    private static string BasePathOf(IConfigurationBuilder builder) =>
        builder.Properties.TryGetValue("FileProvider", out var provider)
            && provider is Microsoft.Extensions.FileProviders.PhysicalFileProvider physical
            ? physical.Root
            : Directory.GetCurrentDirectory();

    /// <summary>ファイルのキーを正式な名前へ写したものを作る。</summary>
    /// <remarks>
    /// **写す前に一度だけ読む。** 積んだ後の設定から読むと、
    /// 環境変数の値まで巻き込んで写してしまう。
    /// </remarks>
    private static IEnumerable<KeyValuePair<string, string?>> AliasesOf(string directory, bool local)
    {
        var suffix = local ? ".local.json" : ".json";
        foreach (var name in Names)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(directory, $"{name}{suffix}"),
                    optional: true, reloadOnChange: false)
                .Build();

            using var lifetime = configuration as IDisposable;

            foreach (var alias in Aliases.Where(alias => alias.File == name))
            {
                var value = configuration[alias.Key];
                // null と空は未指定。false と 0 は明示された値として写す。
                if (!string.IsNullOrEmpty(value))
                {
                    yield return new KeyValuePair<string, string?>(alias.Setting, value);
                }
            }
        }
    }
}
