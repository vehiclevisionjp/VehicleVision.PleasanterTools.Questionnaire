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
}
