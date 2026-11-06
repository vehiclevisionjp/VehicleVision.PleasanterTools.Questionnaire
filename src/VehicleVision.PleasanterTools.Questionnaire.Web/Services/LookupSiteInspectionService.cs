using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>参照元サイトを点検する口。管理 API（Issue #879）が使い、試験では偽物へ差し替える。</summary>
public interface ILookupSiteInspectionService
{
    Task<LookupSiteInspection> InspectAsync(
        Guid surveyId,
        string credentialId,
        long siteId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 専用資格情報で参照元サイトの定義（<c>GetSite</c>）を読む（接続試験・列の取得・公開前検証）。
/// </summary>
/// <remarks>
/// <see cref="LookupSearchService"/> と同じく、資格情報は要求ごとに取り出す
/// （停止・失効・削除・復号不可なら外部へ送らない）。接続先は管理済みの Pleasanter 接続先だけ。
/// **回答保存用の API キーは使わない。** ログを出さず、結果にも秘密を含めない。
/// </remarks>
public sealed class LookupSiteInspectionService(
    ILookupCredentialStore credentials,
    IPleasanterOptionsProvider pleasanterOptions,
    LookupSearchClient client) : ILookupSiteInspectionService
{
    public async Task<LookupSiteInspection> InspectAsync(
        Guid surveyId,
        string credentialId,
        long siteId,
        CancellationToken cancellationToken = default)
    {
        var secret = await credentials
            .ResolveSecretAsync(surveyId, credentialId, cancellationToken)
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
        return await client
            .InspectSiteAsync(new LookupSourceConnection(options.BaseUrl, secret.Secret!), siteId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static LookupSiteInspection Fail(LookupSearchFailure failure) => new()
    {
        Status = LookupSearchStatus.SourceInvalid,
        Failure = failure,
    };
}
