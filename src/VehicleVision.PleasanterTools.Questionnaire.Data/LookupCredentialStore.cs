using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>資格情報の状態。**秘密は含めない**（設定済みの有無・有効状態だけ）。</summary>
public sealed record LookupCredentialStatus(
    string CredentialId,
    LookupSourceKind SourceKind,
    bool IsEnabled,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>資格情報の取り出し結果。</summary>
public enum LookupSecretStatus
{
    /// <summary>取り出せた。</summary>
    Available,

    /// <summary>該当するアンケートに、その資格情報が無い。</summary>
    NotFound,

    /// <summary>停止・失効している。</summary>
    Disabled,

    /// <summary>復号できない（鍵・文脈が違う、または壊れている）。</summary>
    Undecryptable,
}

/// <summary>資格情報の取り出し結果。<see cref="Secret"/> は <see cref="LookupSecretStatus.Available"/> のときだけ。</summary>
/// <remarks>**ログ・例外・画面へ出さないこと。** 検索のためにサーバーが使う間だけ持つ。</remarks>
public sealed record LookupSecretResolution(LookupSecretStatus Status, string? Secret = null);

/// <summary>ルックアップの専用資格情報（API キー）の保管先（仕様書 3.3 節）。</summary>
/// <remarks>
/// <para>
/// **暗号文は文脈（アンケート ID・資格情報 ID）に束縛する。** 別のアンケートや別の行へ貼り替えても復号できない。
/// すべての操作が <c>SurveyId</c> も条件に入れ、**他のアンケートの資格情報を読み書きできない。**
/// </para>
/// <para>平文も暗号文も、状態の問い合わせでは返さない。</para>
/// </remarks>
public interface ILookupCredentialStore
{
    /// <summary>資格情報を登録する。新しい資格情報 ID を返す。</summary>
    Task<string> CreateAsync(
        Guid surveyId,
        LookupSourceKind sourceKind,
        string secret,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>秘密を入れ替える（ローテーション）。公開済みの版は書き換わらない。見つからなければ <c>false</c>。</summary>
    Task<bool> RotateAsync(
        Guid surveyId,
        string credentialId,
        string secret,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>停止（<c>false</c>）・再開（<c>true</c>）する。**公開し直さずに実行時へ効く。**</summary>
    Task<bool> SetEnabledAsync(
        Guid surveyId,
        string credentialId,
        bool enabled,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>資格情報を削除する（解除）。</summary>
    Task<bool> DeleteAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>状態を読む。**秘密は返さない。**</summary>
    Task<LookupCredentialStatus?> FindStatusAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default);

    /// <summary>検索のために秘密を取り出す。**停止・失効していれば取り出さない。**</summary>
    Task<LookupSecretResolution> ResolveSecretAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。表は <c>LookupCredentials</c>（M0040）。</summary>
public sealed class LookupCredentialStore(
    IDbConnectionFactory connectionFactory,
    ISecretCipher cipher) : ILookupCredentialStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    /// <summary>暗号化の文脈。**アンケート ID・資格情報 ID に束縛する。**</summary>
    internal static string ContextOf(Guid surveyId, string credentialId) =>
        $"questionnaire:lookup:{surveyId:N}:{credentialId}";

    public async Task<string> CreateAsync(
        Guid surveyId,
        LookupSourceKind sourceKind,
        string secret,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var credentialId = Guid.NewGuid().ToString("N");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [LookupCredentials] ([CredentialId], [SurveyId], [SourceKind], "
            + "[SecretCipher], [IsEnabled], [UpdatedBy], [UpdatedAt]) "
            + "VALUES (@CredentialId, @SurveyId, @SourceKind, @SecretCipher, @IsEnabled, "
            + "@UpdatedBy, @Now)",
            new
            {
                CredentialId = credentialId,
                SurveyId = surveyId,
                SourceKind = (int)sourceKind,
                SecretCipher = cipher.Protect(secret, ContextOf(surveyId, credentialId)),
                IsEnabled = true,
                UpdatedBy = updatedBy,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken)).ConfigureAwait(false);
        return credentialId;
    }

    public async Task<bool> RotateAsync(
        Guid surveyId,
        string credentialId,
        string secret,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [LookupCredentials] SET [SecretCipher] = @SecretCipher, "
            + "[UpdatedBy] = @UpdatedBy, [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId AND [CredentialId] = @CredentialId",
            new
            {
                SurveyId = surveyId,
                CredentialId = credentialId,
                SecretCipher = cipher.Protect(secret, ContextOf(surveyId, credentialId)),
                UpdatedBy = updatedBy,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<bool> SetEnabledAsync(
        Guid surveyId,
        string credentialId,
        bool enabled,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [LookupCredentials] SET [IsEnabled] = @IsEnabled, "
            + "[UpdatedBy] = @UpdatedBy, [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId AND [CredentialId] = @CredentialId",
            new
            {
                SurveyId = surveyId,
                CredentialId = credentialId,
                IsEnabled = enabled,
                UpdatedBy = updatedBy,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<bool> DeleteAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [LookupCredentials] "
            + "WHERE [SurveyId] = @SurveyId AND [CredentialId] = @CredentialId",
            new { SurveyId = surveyId, CredentialId = credentialId },
            cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<LookupCredentialStatus?> FindStatusAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<StatusRow>(Sql(
            "SELECT [CredentialId], [SourceKind], [IsEnabled], [UpdatedAt], [UpdatedBy] "
            + "FROM [LookupCredentials] "
            + "WHERE [SurveyId] = @SurveyId AND [CredentialId] = @CredentialId",
            new { SurveyId = surveyId, CredentialId = credentialId },
            cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new LookupCredentialStatus(
                row.CredentialId,
                (LookupSourceKind)row.SourceKind,
                row.IsEnabled,
                DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc),
                row.UpdatedBy);
    }

    public async Task<LookupSecretResolution> ResolveSecretAsync(
        Guid surveyId,
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<SecretRow>(Sql(
            "SELECT [SecretCipher], [IsEnabled] FROM [LookupCredentials] "
            + "WHERE [SurveyId] = @SurveyId AND [CredentialId] = @CredentialId",
            new { SurveyId = surveyId, CredentialId = credentialId },
            cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return new LookupSecretResolution(LookupSecretStatus.NotFound);
        }

        // **停止・失効していれば、復号もしない。** 過去の値や別の資格情報で穴埋めしない
        if (!row.IsEnabled)
        {
            return new LookupSecretResolution(LookupSecretStatus.Disabled);
        }

        try
        {
            return new LookupSecretResolution(
                LookupSecretStatus.Available,
                cipher.Unprotect(row.SecretCipher, ContextOf(surveyId, credentialId)));
        }
        catch (CryptographicException)
        {
            return new LookupSecretResolution(LookupSecretStatus.Undecryptable);
        }
    }

    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む**（<c>SqlDialect.Format</c> が書き換える）。</summary>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed class StatusRow
    {
        public string CredentialId { get; set; } = "";

        public int SourceKind { get; set; }

        public bool IsEnabled { get; set; }

        public DateTime UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }
    }

    private sealed class SecretRow
    {
        public string SecretCipher { get; set; } = "";

        public bool IsEnabled { get; set; }
    }
}
