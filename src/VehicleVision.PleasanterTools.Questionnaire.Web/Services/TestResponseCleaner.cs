using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using Dnc = VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>テスト回答の削除の結果の種類。</summary>
public enum TestResponseCleanupStatus
{
    /// <summary>実行した（個々の結果は件数で見る）。</summary>
    Completed,

    /// <summary>アンケートが無い。</summary>
    SurveyNotFound,

    /// <summary>回答 JSON 列が無く、消す前にレコードの持ち主を確かめられない。**何も消さない。**</summary>
    NotVerifiable,

    /// <summary>画面が見せた件数と、今の件数が違う。**何も消さない。**</summary>
    CountChanged,

    /// <summary>Pleasanter が API キーを断った、または削除の権限が無い。**そこで止める。**</summary>
    Forbidden,
}

/// <summary>削除の結果。</summary>
/// <param name="Status">結果の種類。</param>
/// <param name="Deleted">Pleasanter から消した件数。</param>
/// <param name="AlreadyGone">Pleasanter に既に無かった件数（対応表だけ消した）。</param>
/// <param name="Skipped">取り違えの疑いがあり、触らなかった件数。</param>
/// <param name="Failed">一時的な失敗などで消せなかった件数。</param>
/// <param name="Remaining">この実行のあとも消せる件数。</param>
/// <param name="Current">件数が違うとき、今の件数。</param>
/// <param name="ReferenceIds">消した Pleasanter のレコード ID（操作の記録に残す）。</param>
public sealed record TestResponseCleanupResult(
    TestResponseCleanupStatus Status,
    int Deleted = 0,
    int AlreadyGone = 0,
    int Skipped = 0,
    int Failed = 0,
    int Remaining = 0,
    int Current = 0,
    ImmutableArray<long> ReferenceIds = default);

/// <summary>Pleasanter 側のテスト回答を、管理画面から消す（Issue #1010）。</summary>
/// <remarks>
/// <para>
/// **消してはならないものを消さないことを、すべてに優先する。** Pleasanter の削除 API は、
/// ID が指すものが回答でも<b>サイト</b>でもそのまま消すので、**消す前に毎回、次を確かめる**。
/// </para>
/// <list type="number">
/// <item><b>対象は本アプリの記録だけから決める。</b> <c>IsTest = true</c> で、Pleasanter に届いていて、
/// 送信待ちが無い行（<see cref="IResponseTokenStore.ListDeletableTestAsync"/>）。画面から ID は受け取らない</item>
/// <item><b>画面が見せた件数と一致すること。</b> 違えば何も消さない（画面を開いた後に状況が変わった）</item>
/// <item><b>レコードを 1 件ずつ取り出して照合する。</b> 同じサイト・同じ ID・回答 JSON 列のトークンが
/// 記録と同じ、の 3 つがそろわなければ触らない</item>
/// <item>1 回の実行で消すのは <see cref="MaxPerRun"/> 件まで（API の呼び出し回数を抑える。残りは再実行）</item>
/// </list>
/// <para>
/// **認証・権限の失敗では、残りを試さずに止める。** 同じ失敗を件数ぶん繰り返すだけで、
/// Pleasanter のログとレート制限を汚すため。
/// </para>
/// <para>**実機の Pleasanter では未検証**（偽の通信のみ）。</para>
/// </remarks>
public sealed class TestResponseCleaner(
    ISurveyRepository surveys,
    IResponseTokenStore tokens,
    PleasanterGateway pleasanter,
    ILogger<TestResponseCleaner> logger)
{
    /// <summary>1 回の実行で消す件数の上限。</summary>
    public const int MaxPerRun = 100;

    /// <summary>消せるテスト回答の件数。</summary>
    /// <returns>アンケートが無ければ <c>null</c>。</returns>
    public async Task<int?> CountAsync(Guid surveyId, CancellationToken cancellationToken = default)
    {
        var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false);
        return survey is null
            ? null
            : await tokens.CountDeletableTestAsync(surveyId, cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="PleasanterConfigurationException">接続先の設定が使えないとき（通信しない）。</exception>
    public async Task<TestResponseCleanupResult> DeleteAsync(
        Guid surveyId,
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false);
        if (survey is null)
        {
            return new TestResponseCleanupResult(TestResponseCleanupStatus.SurveyNotFound);
        }

        // **持ち主を確かめる列が無ければ、何も消さない。** 確かめずに消すより、消せない方がよい
        if (string.IsNullOrEmpty(survey.ResponseJsonColumn))
        {
            return new TestResponseCleanupResult(TestResponseCleanupStatus.NotVerifiable);
        }

        var current = await tokens.CountDeletableTestAsync(surveyId, cancellationToken).ConfigureAwait(false);
        if (current != expectedCount)
        {
            return new TestResponseCleanupResult(TestResponseCleanupStatus.CountChanged, Current: current);
        }

        var targets = await tokens.ListDeletableTestAsync(surveyId, MaxPerRun, cancellationToken)
            .ConfigureAwait(false);

        var deleted = 0;
        var alreadyGone = 0;
        var skipped = 0;
        var failed = 0;
        var forbidden = false;
        var ids = ImmutableArray.CreateBuilder<long>();

        foreach (var target in targets)
        {
            var outcome = await DeleteOneAsync(survey, target, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case Outcome.Deleted:
                    deleted++;
                    ids.Add(target.ReferenceId);
                    break;
                case Outcome.AlreadyGone:
                    alreadyGone++;
                    break;
                case Outcome.Skipped:
                    skipped++;
                    break;
                case Outcome.Forbidden:
                    forbidden = true;
                    break;
                default:
                    failed++;
                    break;
            }

            if (forbidden)
            {
                break;
            }
        }

        var remaining = await tokens.CountDeletableTestAsync(surveyId, cancellationToken).ConfigureAwait(false);
        return new TestResponseCleanupResult(
            forbidden ? TestResponseCleanupStatus.Forbidden : TestResponseCleanupStatus.Completed,
            deleted,
            alreadyGone,
            skipped,
            failed,
            remaining,
            ReferenceIds: ids.ToImmutable());
    }

    private enum Outcome
    {
        Deleted,
        AlreadyGone,
        Skipped,
        Failed,
        Forbidden,
    }

    private static bool IsForbidden(Dnc.PleasanterResponse response) =>
        response.ErrorKind == PleasanterErrorKind.Unauthorized || response.DisplayStatusCode() is 401 or 403;

    private async Task<Outcome> DeleteOneAsync(
        SurveyRecord survey,
        TestResponseToken target,
        CancellationToken cancellationToken)
    {
        var get = await pleasanter.GetRecordAsync(target.ReferenceId, cancellationToken).ConfigureAwait(false);
        if (!get.IsSuccess)
        {
            if (IsForbidden(get))
            {
                return Outcome.Forbidden;
            }

            if (get.ErrorKind == PleasanterErrorKind.Permanent && get.DisplayStatusCode() == 404)
            {
                return await ForgetAsync(survey, target, cancellationToken).ConfigureAwait(false);
            }

            logger.LogWarning(
                "テスト回答を取り出せない: 分類={Kind}, ステータス={Status}", get.ErrorKind, get.DisplayStatusCode());
            return Outcome.Failed;
        }

        var row = get.Body?["Response"]?["Data"]?.AsArray()?.FirstOrDefault();
        if (row is null)
        {
            // 既に消された（運用側が手で消した場合など）。対応表だけ片付ける
            return await ForgetAsync(survey, target, cancellationToken).ConfigureAwait(false);
        }

        if (!IsExpectedRecord(survey, target, row))
        {
            // **取り違えの疑い。触らない。** 件数にも出す
            logger.LogWarning("テスト回答の照合が合わないので削除しない: アンケート={SurveyId}", survey.SurveyId);
            return Outcome.Skipped;
        }

        var delete = await pleasanter.DeleteAsync(target.ReferenceId, cancellationToken).ConfigureAwait(false);
        if (!delete.IsSuccess)
        {
            if (IsForbidden(delete))
            {
                return Outcome.Forbidden;
            }

            logger.LogWarning(
                "テスト回答を削除できない: 分類={Kind}, ステータス={Status}", delete.ErrorKind, delete.DisplayStatusCode());
            return Outcome.Failed;
        }

        // **Pleasanter から消えたことを確かめてから、対応表を消す。** 逆だと、失敗したときに追えなくなる
        await tokens.DeleteTestAsync(target.ResponseToken, survey.SurveyId, cancellationToken).ConfigureAwait(false);
        return Outcome.Deleted;
    }

    private async Task<Outcome> ForgetAsync(
        SurveyRecord survey, TestResponseToken target, CancellationToken cancellationToken)
    {
        await tokens.DeleteTestAsync(target.ResponseToken, survey.SurveyId, cancellationToken).ConfigureAwait(false);
        return Outcome.AlreadyGone;
    }

    /// <summary>取り出したレコードが、記録どおりの回答か。**3 つともそろったときだけ。**</summary>
    private static bool IsExpectedRecord(SurveyRecord survey, TestResponseToken target, JsonNode row)
    {
        // 同じサイト（**サイトそのものや別サイトのレコードを除く**）。値が返らなければ照合できないので不一致とする
        if (row["SiteId"]?.TryGetValueAsLong() != survey.PleasanterSiteId)
        {
            return false;
        }

        // 同じ ID
        if (row["ResultId"]?.TryGetValueAsLong() != target.ReferenceId)
        {
            return false;
        }

        // 回答 JSON のトークンが記録と同じ
        var json = ResponseReader.ReadColumn(row, survey.ResponseJsonColumn!);
        var payload = json is null ? null : ResponsePayload.FromJson(json);
        return payload is not null && string.Equals(payload.Token, target.ResponseToken, StringComparison.Ordinal);
    }
}
