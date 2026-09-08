using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>ウイルススキャンの方式。</summary>
/// <remarks>
/// **2 つ用意して導入先の事情で選べるようにする**
/// （<c>_documents/添付ファイル検査-運用手順書.md</c> 2 章）。
/// </remarks>
public enum VirusScanProvider
{
    /// <summary>ClamAV（<c>clamd</c>）へ TCP で問い合わせる。**既定。判定が同期で付く。**</summary>
    ClamAv,

    /// <summary>Azure Blob Storage へ置いて Defender for Storage の判定を待つ。</summary>
    DefenderForStorage,
}

/// <summary>ウイルススキャンの設定。**既定は無効。**</summary>
/// <remarks>
/// 導入先にスキャナが無い環境でも動くようにするため、既定では検査しない
/// （<c>_documents/非機能設計.md</c> 1 章）。
/// </remarks>
public sealed class VirusScanOptions
{
    /// <summary>ウイルススキャンを行うか。**既定は無効。**</summary>
    public bool Enabled { get; init; }

    /// <summary>どの方式で検査するか。</summary>
    public VirusScanProvider Provider { get; init; } = VirusScanProvider.ClamAv;

    /// <summary><c>clamd</c> の接続先。**サイドカーなので外へ晒さない。**</summary>
    public string ClamAvHost { get; init; } = "localhost";

    /// <summary><c>clamd</c> のポート。</summary>
    public int ClamAvPort { get; init; } = 3310;

    /// <summary>1 件あたりのスキャンに待つ上限。</summary>
    public TimeSpan ClamAvTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>DMZ（未検査用）のストレージアカウントへの接続文字列。</summary>
    /// <remarks>
    /// **検査済みのものだけを本来の置き場へ移す構成にすること。**
    /// <see cref="DefenderContainerUrl"/> を使う場合は不要。
    /// </remarks>
    public string? DefenderConnectionString { get; init; }

    /// <summary>DMZ コンテナの URL（SAS 付き）。接続文字列の代わりに使える。</summary>
    public string? DefenderContainerUrl { get; init; }

    /// <summary>DMZ コンテナ名。<see cref="DefenderConnectionString"/> と組で使う。</summary>
    public string DefenderContainer { get; init; } = "questionnaire-dmz";

    /// <summary>判定が届くまで待つ上限。**超えたら通さない。**</summary>
    /// <remarks>
    /// **判定は非同期で、大きい blob は数十分かかり得る**
    /// （<c>_documents/添付ファイル検査-運用手順書.md</c> 4 章）。
    /// 回答者を待たせる時間なので、長くしすぎないこと。
    /// </remarks>
    public TimeSpan DefenderResultTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Event Grid の受け口を守るパスワード。</summary>
    /// <remarks>
    /// **この受け口は認証の外に置かれる。** パスワードが無いと、誰でも「検出なし」を
    /// 送り込めてしまう。<see cref="VirusScanProvider.DefenderForStorage"/> では必須。
    /// </remarks>
    public string? EventGridKey { get; init; }
}

/// <summary>添付ファイルの受け入れ設定。</summary>
/// <remarks>
/// **実値は環境変数（Azure App Service のアプリケーション設定）で与える**
/// （<c>App_Data/Parameters/README.md</c>）。
/// </remarks>
public sealed class AttachmentOptions
{
    /// <summary>既定で許可する拡張子。</summary>
    /// <remarks>
    /// **禁止リストではなく許可リスト。** 危険な拡張子を挙げていく方式は必ず漏れる。
    /// 導入先で要るものを足すのは設定で行う。
    /// </remarks>
    public static readonly ImmutableArray<string> DefaultAllowedExtensions =
    [
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".txt", ".csv", ".docx", ".xlsx", ".pptx", ".zip",
    ];

    /// <summary>許可する拡張子。</summary>
    public ImmutableArray<string> AllowedExtensions { get; init; } = DefaultAllowedExtensions;

    /// <summary>1 件あたりのサイズ上限（バイト）。</summary>
    public long MaxFileSizeBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>1 設問あたりの個数上限。</summary>
    public int MaxFileCount { get; init; } = 5;

    /// <summary>1 回の送信の合計サイズ上限（バイト）。</summary>
    public long MaxTotalBytes { get; init; } = 20 * 1024 * 1024;

    /// <summary>ウイルススキャンの設定。</summary>
    public VirusScanOptions VirusScan { get; init; } = new();

    /// <summary>要求本文の上限（バイト）。</summary>
    /// <remarks>
    /// **既定値に任せない**（<c>_documents/非機能設計.md</c> 1 章）。
    /// 添付は multipart で生のまま届くので、合計の上限に回答本文とヘッダの分を足す。
    /// </remarks>
    public long MaxRequestBodyBytes => MaxTotalBytes + (1024 * 1024);

    /// <summary>検査に使う条件へ変換する。</summary>
    public AttachmentPolicy ToPolicy() => AttachmentPolicy.Create(
        AllowedExtensions,
        MaxFileSizeBytes,
        MaxFileCount,
        VirusScan.Enabled,
        MaxTotalBytes);

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **読めない値は既定値へ倒さずに落とす。** 「有効にしたつもりが無効だった」を作らない。
    /// </remarks>
    public static AttachmentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new AttachmentOptions();
        var defaultScan = defaults.VirusScan;

        return new AttachmentOptions
        {
            AllowedExtensions = ReadExtensions(configuration) ?? defaults.AllowedExtensions,
            MaxFileSizeBytes = ReadInt64(configuration, "QUESTIONNAIRE_ATTACHMENT_MAXFILESIZEBYTES")
                ?? defaults.MaxFileSizeBytes,
            MaxFileCount = ReadInt32(configuration, "QUESTIONNAIRE_ATTACHMENT_MAXFILECOUNT")
                ?? defaults.MaxFileCount,
            MaxTotalBytes = ReadInt64(configuration, "QUESTIONNAIRE_ATTACHMENT_MAXTOTALBYTES")
                ?? defaults.MaxTotalBytes,
            VirusScan = new VirusScanOptions
            {
                Enabled = ReadBoolean(configuration, "QUESTIONNAIRE_VIRUSSCAN_ENABLED")
                    ?? defaultScan.Enabled,
                Provider = ReadProvider(configuration) ?? defaultScan.Provider,
                ClamAvHost = configuration["QUESTIONNAIRE_VIRUSSCAN_HOST"] ?? defaultScan.ClamAvHost,
                ClamAvPort = ReadInt32(configuration, "QUESTIONNAIRE_VIRUSSCAN_PORT")
                    ?? defaultScan.ClamAvPort,
                ClamAvTimeout = ReadSeconds(configuration, "QUESTIONNAIRE_VIRUSSCAN_TIMEOUTSECONDS")
                    ?? defaultScan.ClamAvTimeout,
                DefenderConnectionString =
                    configuration["QUESTIONNAIRE_VIRUSSCAN_DEFENDER_CONNECTIONSTRING"],
                DefenderContainerUrl = configuration["QUESTIONNAIRE_VIRUSSCAN_DEFENDER_CONTAINERURL"],
                DefenderContainer = configuration["QUESTIONNAIRE_VIRUSSCAN_DEFENDER_CONTAINER"]
                    ?? defaultScan.DefenderContainer,
                DefenderResultTimeout = ReadSeconds(
                    configuration, "QUESTIONNAIRE_VIRUSSCAN_DEFENDER_RESULTTIMEOUTSECONDS")
                    ?? defaultScan.DefenderResultTimeout,
                EventGridKey = configuration["QUESTIONNAIRE_VIRUSSCAN_DEFENDER_EVENTGRIDKEY"],
            },
        };
    }

    private static ImmutableArray<string>? ReadExtensions(IConfiguration configuration)
    {
        var raw = configuration["QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var extensions = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AttachmentPolicy.NormalizeExtension)
            .ToImmutableArray();

        return extensions.IsEmpty
            ? throw Invalid("QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS", raw)
            : extensions;
    }

    private static VirusScanProvider? ReadProvider(IConfiguration configuration)
    {
        var raw = configuration["QUESTIONNAIRE_VIRUSSCAN_PROVIDER"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse<VirusScanProvider>(raw, ignoreCase: true, out var provider)
            ? provider
            : throw Invalid("QUESTIONNAIRE_VIRUSSCAN_PROVIDER", raw);
    }

    private static bool? ReadBoolean(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return bool.TryParse(raw, out var value) ? value : throw Invalid(key, raw);
    }

    private static int? ReadInt32(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : throw Invalid(key, raw);
    }

    private static long? ReadInt64(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : throw Invalid(key, raw);
    }

    private static TimeSpan? ReadSeconds(IConfiguration configuration, string key)
    {
        var seconds = ReadInt32(configuration, key);
        return seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
    }

    private static InvalidOperationException Invalid(string key, string raw) =>
        new($"{key} の値を解釈できない: {raw}");
}
