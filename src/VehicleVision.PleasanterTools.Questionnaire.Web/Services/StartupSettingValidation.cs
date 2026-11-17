namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 起動時に読む設定のうち、書き間違いが「設定名の無い .NET の例外」になっていたものを、
/// **設定名と使える値を添えたメッセージ**で止める（Issue #857）。
/// </summary>
/// <remarks>
/// 例外の種類と「失敗して起動しない」振る舞いは変えない。メッセージに設定の値そのものは含めない
/// （接続文字列や鍵の実値をログへ出さないため。DB の種類名だけは秘密ではないので出す）。
/// </remarks>
public static class StartupSettingValidation
{
    public const string DatabaseProviderKey = "QUESTIONNAIRE_DB_PROVIDER";
    public const string SecretKeyKey = "QUESTIONNAIRE_SECRET_KEY";
    public const string SharedStateTimeoutKey = "QUESTIONNAIRE_SHARED_STATE_TIMEOUT_MS";

    /// <summary>共有ストアの待ち時間（ミリ秒）の既定値。</summary>
    public const int DefaultSharedStateTimeoutMilliseconds = 1000;

    /// <summary>共有ストアの待ち時間の下限（ミリ秒）。</summary>
    public const int MinimumSharedStateTimeoutMilliseconds = 100;

    /// <summary>共有ストアの待ち時間の上限（ミリ秒）。</summary>
    public const int MaximumSharedStateTimeoutMilliseconds = 10000;

    /// <summary>DB の種類を読む。未設定（null）は SQL Server。</summary>
    public static DatabaseProvider ParseDatabaseProvider(string? value)
    {
        if (value is null)
        {
            return DatabaseProvider.SqlServer;
        }

        // **数字や大文字小文字違いを黙って通さない。** 従来の Enum.Parse と同じく名前の完全一致だけ
        if (Enum.GetNames<DatabaseProvider>().Contains(value, StringComparer.Ordinal))
        {
            return Enum.Parse<DatabaseProvider>(value);
        }

        throw new InvalidOperationException(
            $"{DatabaseProviderKey} には {string.Join(" / ", Enum.GetNames<DatabaseProvider>())} "
            + "のいずれかを指定する（大文字小文字も合わせる。未設定なら SqlServer）");
    }

    /// <summary>秘密の鍵の設定を取り出す。未設定も空も同じメッセージで止める。</summary>
    public static string RequireSecretKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{SecretKeyKey} が設定されていない（Base64 の 32 バイト）");
        }

        return value;
    }

    /// <summary>Redis の接続文字列を取り出す。未設定も空も同じメッセージで止める。</summary>
    public static string RequireRedisConnectionString(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Redis を使うときは {key} が要る");
        }

        return value;
    }

    /// <summary>
    /// 共有ストアの接続・操作の待ち時間（ミリ秒）を読む。未設定・空は既定の 1000。
    /// 範囲外・数でない値は、設定名と使える範囲を添えて起動時に止める（値そのものは出さない）。
    /// </summary>
    public static int ParseSharedStateTimeoutMilliseconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultSharedStateTimeoutMilliseconds;
        }

        if (int.TryParse(
                value.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var milliseconds)
            && milliseconds is >= MinimumSharedStateTimeoutMilliseconds
                and <= MaximumSharedStateTimeoutMilliseconds)
        {
            return milliseconds;
        }

        throw new InvalidOperationException(
            $"{SharedStateTimeoutKey} には {MinimumSharedStateTimeoutMilliseconds} から "
            + $"{MaximumSharedStateTimeoutMilliseconds} の整数（ミリ秒）を指定する"
            + $"（未設定なら {DefaultSharedStateTimeoutMilliseconds}）");
    }
}
