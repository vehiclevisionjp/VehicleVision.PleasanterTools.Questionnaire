using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// ルックアップの検索を、専用資格情報と結線して実行する（Issue #829・仕様書 3.3・6.1 節）。
/// </summary>
/// <remarks>
/// <para>
/// **定義は公開版で固定されたもの（<see cref="LookupDefinition"/>）を呼び出し側が渡す。**
/// 下書きや回答者の入力から定義・サイト ID・列名・接続先を作らない。
/// </para>
/// <para>
/// 資格情報は要求ごとに <see cref="ILookupCredentialStore"/> から取り出す。
/// **停止・失効・削除・復号不可なら外部へ送らない**（公開し直さずに効く。過去の値や別の資格情報で穴埋めしない）。
/// 取り出しはアンケート ID で絞られ、別のアンケートの資格情報は使えない。
/// </para>
/// <para>
/// 接続先は管理済みの Pleasanter 接続先の URL だけを使う。**回答保存用の API キーは読まない・使わない。**
/// このクラスはログを出さず、結果にも秘密・検索値を含めない。
/// </para>
/// </remarks>
public sealed class LookupSearchService(
    ILookupCredentialStore credentials,
    IPleasanterOptionsProvider pleasanterOptions,
    LookupSearchClient client)
{
    public async Task<LookupSearchResult> SearchAsync(
        Guid surveyId,
        LookupDefinition lookup,
        IReadOnlyDictionary<string, string?> keyValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(keyValues);

        if (lookup.SourceKind != LookupSourceKind.Pleasanter
            || string.IsNullOrWhiteSpace(lookup.CredentialId))
        {
            return Fail(LookupSearchFailure.InvalidDefinition);
        }

        var secret = await credentials
            .ResolveSecretAsync(surveyId, lookup.CredentialId, cancellationToken)
            .ConfigureAwait(false);
        switch (secret.Status)
        {
            case LookupSecretStatus.Available when !string.IsNullOrEmpty(secret.Secret):
                break;
            case LookupSecretStatus.Disabled:
                return Fail(LookupSearchFailure.CredentialDisabled);
            case LookupSecretStatus.Undecryptable:
                return Fail(LookupSearchFailure.CredentialUndecryptable);
            default:
                return Fail(LookupSearchFailure.CredentialMissing);
        }

        var options = await pleasanterOptions.GetAsync(cancellationToken).ConfigureAwait(false);
        var connection = new LookupSourceConnection(options.BaseUrl, secret.Secret!);
        return await client
            .SearchAsync(connection, new LookupSearchRequest(lookup, keyValues), cancellationToken)
            .ConfigureAwait(false);
    }

    private static LookupSearchResult Fail(LookupSearchFailure failure) => new()
    {
        Status = LookupSearchStatus.SourceInvalid,
        Failure = failure,
    };
}
