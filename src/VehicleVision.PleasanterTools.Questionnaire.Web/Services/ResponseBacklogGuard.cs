using System.Collections.Concurrent;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>滞留で受付を止める閾値（Issue #72）。</summary>
/// <remarks>
/// <para>
/// **エッジに WAF が無い導入先を想定している**（<c>_documents/非機能設計.md</c> 2 章）。
/// 分散した相手にレート制限は効かないので、**最後は「溜まった量」で止める。**
/// </para>
/// <para>
/// **攻撃が無くても効く。** Pleasanter が長く落ちれば滞留は同じように増える。
/// アウトボックスは障害を吸収するが、**量は吸収しない。**
/// </para>
/// </remarks>
public sealed record BacklogGuardOptions
{
    /// <summary>アンケート 1 本あたりの上限。**0 以下で無効。**</summary>
    /// <remarks>
    /// **狙われている 1 本を切り離し、他を生かすための段。**
    /// 既定は 1 万件。**添付があると 1 件が桁違いに重い**ので、
    /// 添付を受け付けるアンケートが多い導入先では下げること。
    /// </remarks>
    public int PerSurveyLimit { get; init; } = 10_000;

    /// <summary>全アンケート合計の上限。**0 以下で無効。**</summary>
    /// <remarks>
    /// **Pleasanter が止まると全アンケートが等しく溜まる**ので、
    /// 1 本ずつの上限だけでは DB が溢れるのを止められない。既定は 5 万件。
    /// </remarks>
    public int TotalLimit { get; init; } = 50_000;

    /// <summary>受付を再開する水準（上限に対する割合）。</summary>
    /// <remarks>
    /// **ヒステリシス。** 上限ちょうどで再開すると、
    /// 1 件捌けては 1 件受け付ける、を境界で延々と繰り返す。
    /// </remarks>
    public double ResumeRatio { get; init; } = 0.8;

    /// <summary>数え直す間隔。</summary>
    /// <remarks>
    /// **受付のたびには数えない**（<c>_documents/非機能設計.md</c> 2 章）。
    /// 間隔の間に届いた分は、受け付けた数を足して補う。
    /// </remarks>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>どちらかの段が生きているか。</summary>
    public bool Enabled => PerSurveyLimit > 0 || TotalLimit > 0;

    /// <summary>アンケート単位で受付を再開する件数。</summary>
    public int PerSurveyResume => Resume(PerSurveyLimit);

    /// <summary>全体で受付を再開する件数。</summary>
    public int TotalResume => Resume(TotalLimit);

    private int Resume(int limit) =>
        limit <= 0 ? 0 : (int)Math.Max(0, Math.Floor(limit * Math.Clamp(ResumeRatio, 0d, 1d)));

    /// <summary>アンケート単位の上限の設定名。</summary>
    public const string PerSurveyLimitKey = "QUESTIONNAIRE_BACKLOG_PER_SURVEY";

    /// <summary>全体の上限の設定名。</summary>
    public const string TotalLimitKey = "QUESTIONNAIRE_BACKLOG_TOTAL";

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **読めない値を黙って既定へ落とさない。** 設定したつもりが効いていない状態を作る。
    /// **0 以下は「その段を使わない」**という指定として通す。
    /// </remarks>
    public static BacklogGuardOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new BacklogGuardOptions();

        return new BacklogGuardOptions
        {
            PerSurveyLimit = Read(configuration, PerSurveyLimitKey, defaults.PerSurveyLimit),
            TotalLimit = Read(configuration, TotalLimitKey, defaults.TotalLimit),
        };
    }

    private static int Read(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は整数で指定する（今の値: {raw}）。0 以下にするとその段を使わない");
    }
}

/// <summary>見張りの今の状態。**管理画面に出す。**</summary>
/// <param name="Enabled">閾値が設定されているか。</param>
/// <param name="Total">最後に数えた滞留の総件数（＋その後に受け付けた分）。</param>
/// <param name="TotalLimit">全体の上限。</param>
/// <param name="TotalBlocked">全体で受付を止めているか。</param>
/// <param name="PerSurveyLimit">アンケート単位の上限。</param>
/// <param name="BlockedSurveyCount">滞留で受付を止めているアンケートの本数。</param>
/// <param name="SampledAt">最後に数えた時刻（UTC）。**一度も数えていなければ <c>null</c>。**</param>
public sealed record BacklogGuardStatus(
    bool Enabled,
    int Total,
    int TotalLimit,
    bool TotalBlocked,
    int PerSurveyLimit,
    int BlockedSurveyCount,
    DateTimeOffset? SampledAt);

/// <summary>溜まりすぎたら受付を断る（Issue #72）。</summary>
/// <remarks>
/// <para>
/// **上限に達したら自動で解ける「弁」であって、止めっぱなしの「開閉器」ではない。**
/// 滞留は Pleasanter の不調や一時的な集中で増減する**過ぎ去る状態**なので、
/// 捌けたら受け付けてよい。
/// **回答数の上限（Issue #53）が人の手でしか戻らないのとは、そこが違う。**
/// あちらは「もう募集しない」という決めごとで、放っておいても消えない。
/// </para>
/// <para>
/// ⚠️ **止めっぱなしにしないのは、意図した判断。** 止めっぱなしにすると、
/// Pleasanter が半日落ちただけで人気のアンケートが永久に閉じる。
/// **溜まる速さを送信が捌ける速さに縛る**方が、目的（DB を溢れさせない）に対して素直で、
/// 攻撃側から見ても「送信ワーカーの速度までしか押し込めない」状態になる。
/// </para>
/// <para>
/// **止めた・戻した瞬間はログへ残す。** 管理画面にも今の状態を出す（Issue #72）。
/// </para>
/// <para>
/// **回答者へ内部の事情は見せない。** 断るときの理由は「停止中」に丸める。
/// </para>
/// </remarks>
public sealed class ResponseBacklogGuard(
    IResponseOutbox outbox,
    BacklogGuardOptions options,
    ILogger<ResponseBacklogGuard> logger,
    TimeProvider? timeProvider = null,
    IAdminNotificationStore? notifications = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>数え直しを 1 本に絞る錠。**待たない。** 誰かが数えていれば古い値で答える。</summary>
    private readonly SemaphoreSlim _sampling = new(1, 1);

    /// <summary>前回数えて以降に受け付けた件数。**数え直しの間の空白を埋める。**</summary>
    private readonly ConcurrentDictionary<Guid, int> _accepted = new();

    /// <summary>今どのアンケートを止めているか。**入っていなければ止めていない。**</summary>
    private readonly ConcurrentDictionary<Guid, bool> _blocked = new();

    private PendingBacklog _sample = PendingBacklog.Empty;
    private long _sampledAtTicks;
    private int _acceptedTotal;
    private int _totalBlocked;

    /// <summary>このアンケートの受付を止めているか。</summary>
    public async ValueTask<bool> IsBlockedAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return false;
        }

        await SampleIfStaleAsync(cancellationToken).ConfigureAwait(false);

        var sample = _sample;

        // **全体が止まっていれば、どのアンケートも受け付けない**
        var total = sample.Total + Volatile.Read(ref _acceptedTotal);
        var totalBlocked = Decide(
            Volatile.Read(ref _totalBlocked) == 1,
            total,
            options.TotalLimit,
            options.TotalResume);

        var flag = totalBlocked ? 1 : 0;
        if (Interlocked.Exchange(ref _totalBlocked, flag) != flag)
        {
            if (totalBlocked)
            {
                logger.LogError(
                    "滞留が {Total} 件になったので、全アンケートの受付を止めた（上限 {Limit} 件）。"
                    + "{Resume} 件まで捌けたら受け付け直す",
                    total,
                    options.TotalLimit,
                    options.TotalResume);
                await NotifyAsync(
                    AdminNotificationKind.BacklogBlockedTotal, Guid.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("滞留が {Total} 件まで減ったので、全体の受付を再開した", total);
            }
        }

        if (totalBlocked)
        {
            return true;
        }

        var mine = sample.For(surveyId)
            + (_accepted.TryGetValue(surveyId, out var delta) ? delta : 0);

        var was = _blocked.ContainsKey(surveyId);
        var blocked = Decide(was, mine, options.PerSurveyLimit, options.PerSurveyResume);

        if (blocked != was)
        {
            if (blocked)
            {
                _blocked[surveyId] = true;
                logger.LogError(
                    "アンケート {SurveyId} の滞留が {Count} 件になったので受付を止めた（上限 {Limit} 件）",
                    surveyId,
                    mine,
                    options.PerSurveyLimit);
                await NotifyAsync(
                    AdminNotificationKind.BacklogBlockedSurvey, surveyId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _blocked.TryRemove(surveyId, out _);
                logger.LogWarning(
                    "アンケート {SurveyId} の滞留が {Count} 件まで減ったので受付を再開した",
                    surveyId,
                    mine);
            }
        }

        return blocked;
    }

    /// <summary>管理者への知らせを 1 件立てる（Issue #80）。</summary>
    /// <remarks>
    /// ⚠️ **知らせを書けなくても受付の結果を変えない。** 例外を投げると、
    /// 「知らせに失敗したせいで回答を断る／通す」が起きる。
    /// **止めた瞬間だけ立てる。** 断るたびに立てると、攻撃で知らせが埋まる。
    /// </remarks>
    private async Task NotifyAsync(
        AdminNotificationKind kind,
        Guid surveyId,
        CancellationToken cancellationToken)
    {
        if (notifications is null)
        {
            return;
        }

        try
        {
            await notifications
                .RaiseAsync(
                    (int)kind, surveyId, _time.GetUtcNow().UtcDateTime, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "管理者への知らせを書けなかった: {Kind}", kind);
        }
    }

    /// <summary>1 件受け付けたことを伝える。</summary>
    /// <remarks>
    /// **数え直しの間に届いた分をここで補う。** 補わないと、
    /// 間隔の内に押し込まれた分だけ上限を素通りする。
    /// </remarks>
    public void OnAccepted(Guid surveyId)
    {
        if (!options.Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _acceptedTotal);
        _accepted.AddOrUpdate(surveyId, 1, static (_, count) => count + 1);
    }

    /// <summary>管理画面へ出す今の状態。**数え直しはしない。**</summary>
    public BacklogGuardStatus GetStatus()
    {
        var ticks = Interlocked.Read(ref _sampledAtTicks);

        return new BacklogGuardStatus(
            options.Enabled,
            _sample.Total + Volatile.Read(ref _acceptedTotal),
            options.TotalLimit,
            Volatile.Read(ref _totalBlocked) == 1,
            options.PerSurveyLimit,
            _blocked.Count,
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    /// <summary>止めるかどうかを決める。**上限で止め、再開の水準まで下がったら戻す。**</summary>
    private static bool Decide(bool was, int count, int limit, int resume) =>
        limit <= 0 ? false
        : count >= limit ? true
        : count <= resume ? false
        : was;

    /// <summary>古くなっていたら数え直す。</summary>
    /// <remarks>
    /// <para>
    /// **待たない。** 既に誰かが数えていれば、その場は前回の値で答える。
    /// 受付のたびに DB を待たせると、詰まっているときほど遅くなる。
    /// </para>
    /// <para>
    /// **数えている間に届いた分は、二重に数えることがある。**
    /// 問い合わせの前に読んだ増分を、返ってきてから引いているため。
    /// **多めに出る側へ倒してある**ので、止め損なうことはない。
    /// </para>
    /// <para>
    /// **数えられなかったら止めない。** DB が応えないときに受付だけ閉じても、
    /// 回答を捨てるだけで滞留は減らない。
    /// </para>
    /// </remarks>
    private async Task SampleIfStaleAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var sampledAt = Interlocked.Read(ref _sampledAtTicks);

        if (sampledAt != 0 && now.UtcTicks - sampledAt < options.SampleInterval.Ticks)
        {
            return;
        }

        if (!await _sampling.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // アンケート単位が無効なときは、返る行が要らないので大きな下限を渡す
            var atLeast = options.PerSurveyLimit > 0
                ? Math.Max(1, options.PerSurveyResume)
                : int.MaxValue;

            var totalBefore = Volatile.Read(ref _acceptedTotal);
            var perBefore = _accepted.ToArray();

            _sample = await outbox.CountBacklogAsync(atLeast, cancellationToken)
                .ConfigureAwait(false);

            Interlocked.Add(ref _acceptedTotal, -totalBefore);
            foreach (var (surveyId, counted) in perBefore)
            {
                var left = _accepted.AddOrUpdate(surveyId, 0, (_, count) => count - counted);
                if (left <= 0)
                {
                    _accepted.TryRemove(new KeyValuePair<Guid, int>(surveyId, left));
                }
            }

            Interlocked.Exchange(ref _sampledAtTicks, _time.GetUtcNow().UtcTicks);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **止めない。** 数えられないことを理由に回答を断らない
            logger.LogError(exception, "滞留の件数を数えられなかった。前回の値のまま続ける");
        }
        finally
        {
            _sampling.Release();
        }
    }
}
