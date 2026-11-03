using System.Collections.Concurrent;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// ルックアップの参照元の不備（<c>502 LookupSourceInvalid</c>）を管理者へ知らせる口（Issue #833・仕様書 4.3 節）。
/// </summary>
public interface ILookupFailureNotifier
{
    /// <summary>失敗を知らせる。**例外を投げない。呼び出し側の応答を変えない。**</summary>
    /// <param name="failure">失敗の分類。<see cref="LookupSearchFailure.None"/> は想定外の例外として扱う。</param>
    /// <param name="correlationId">相関 ID（要求の識別子）。ログへだけ出す。</param>
    Task NotifyAsync(
        Guid surveyId,
        string lookupId,
        LookupSearchFailure failure,
        string correlationId);
}

/// <summary>
/// 既存の管理者通知（<see cref="IAdminNotificationStore"/>）へ、参照元の不備を 1 件ずつ立てる。
/// </summary>
/// <remarks>
/// <para>
/// **渡すのはアンケート ID・分類（種類）・時刻だけ。** キー値・取得値・資格情報・生の応答・トークンは
/// この型へ渡る経路すらない。ログへは検索 ID・分類名・相関 ID を出す（値は出さない）。
/// </para>
/// <para>
/// **重複の抑止は 2 段。** (1) インスタンス内で、アンケート・検索・種類ごとに
/// <see cref="LookupPublicOptions.AdminNotificationWindow"/> の間は 2 回目以降を立てない（DB も呼ばない）。
/// (2) 窓が明けても、既存の知らせは<b>未読の同じ種類・同じアンケートがあれば件数を足す</b>だけで、行は増えない。
/// </para>
/// <para>
/// **知らせを立てられなくても応答は変えない。** 例外（キャンセルを含む）は握りつぶし、型名だけを記録する。
/// 立てられなかった窓は閉じて、次の失敗でやり直せるようにする。
/// </para>
/// </remarks>
public sealed class LookupFailureNotifier(
    IAdminNotificationStore notifications,
    LookupPublicOptions options,
    TimeProvider timeProvider,
    ILogger<LookupFailureNotifier> logger) : ILookupFailureNotifier
{
    /// <summary>窓の記録数の上限。**検索 ID は定義にあるものだけだが、無制限に溜めない。**</summary>
    private const int MaxTrackedWindows = 1024;

    /// <summary>知らせを書く待ち時間の上限。応答を遅らせすぎない。</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<(Guid SurveyId, string LookupId, AdminNotificationKind Kind), DateTimeOffset> windows = new();

    /// <summary>失敗の分類を、知らせの種類へ写す。</summary>
    public static AdminNotificationKind KindOf(LookupSearchFailure failure) => failure switch
    {
        LookupSearchFailure.CredentialMissing
            or LookupSearchFailure.CredentialDisabled
            or LookupSearchFailure.CredentialUndecryptable => AdminNotificationKind.LookupCredentialUnavailable,
        LookupSearchFailure.Unauthorized
            or LookupSearchFailure.Forbidden
            or LookupSearchFailure.NotFound => AdminNotificationKind.LookupSourceAccessDenied,
        LookupSearchFailure.InvalidDefinition
            or LookupSearchFailure.InsecureTransport
            or LookupSearchFailure.ClientError
            or LookupSearchFailure.Redirected => AdminNotificationKind.LookupSourceRejected,
        _ => AdminNotificationKind.LookupSourceBadResponse,
    };

    public async Task NotifyAsync(
        Guid surveyId,
        string lookupId,
        LookupSearchFailure failure,
        string correlationId)
    {
        var kind = KindOf(failure);
        var key = (surveyId, lookupId, kind);
        var now = timeProvider.GetUtcNow();
        if (!TryOpenWindow(key, now))
        {
            return;
        }

        try
        {
            // **要求が切れても知らせは書く。** 切断した回答者のせいで管理者に届かないのは困る
            using var timeout = new CancellationTokenSource(WriteTimeout, timeProvider);
            await notifications
                .RaiseAsync(
                    (int)kind,
                    surveyId,
                    now.UtcDateTime,
                    timeout.Token)
                .ConfigureAwait(false);
            logger.LogWarning(
                "ルックアップの参照元の不備を管理者へ知らせた（種類={Kind} 分類={Failure} フォーム={SurveyId} 検索={LookupId} 相関={TraceId}）",
                kind,
                failure,
                surveyId,
                lookupId,
                correlationId);
        }
        catch (Exception exception)
        {
            // 次の失敗でやり直せるよう窓を閉じる。**例外の中身は出さない**（型名だけ）
            windows.TryRemove(key, out _);
            logger.LogError(
                "ルックアップの不備を管理者へ知らせられなかった（例外={ExceptionType} 種類={Kind} フォーム={SurveyId} 検索={LookupId} 相関={TraceId}）",
                exception.GetType().Name,
                kind,
                surveyId,
                lookupId,
                correlationId);
        }
    }

    /// <summary>窓が空いていれば開いて <c>true</c>。窓の中なら <c>false</c>。</summary>
    private bool TryOpenWindow((Guid, string, AdminNotificationKind) key, DateTimeOffset now)
    {
        var window = options.AdminNotificationWindow;
        if (windows.Count >= MaxTrackedWindows)
        {
            foreach (var entry in windows.Where(entry => now - entry.Value >= window).ToArray())
            {
                windows.TryRemove(entry.Key, out _);
            }
        }

        while (true)
        {
            if (windows.TryGetValue(key, out var openedAt))
            {
                if (now - openedAt < window)
                {
                    return false;
                }

                if (windows.TryUpdate(key, now, openedAt))
                {
                    return true;
                }

                continue;
            }

            if (windows.Count >= MaxTrackedWindows)
            {
                // **記録が満杯でも、知らせないよりは立てる。** 重複は DB 側の集約が吸収する
                return true;
            }

            if (windows.TryAdd(key, now))
            {
                return true;
            }
        }
    }
}
