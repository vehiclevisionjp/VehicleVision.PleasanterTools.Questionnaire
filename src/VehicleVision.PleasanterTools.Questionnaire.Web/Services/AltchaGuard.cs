using Ixnas.AltchaNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

// **同じ名前の受け口が 2 つある。** ライブラリ側と、こちらの Data 層側。
// 別名を付けて、どちらの話かを読み違えないようにする
using AltchaChallengeLog = VehicleVision.PleasanterTools.Questionnaire.Data.IAltchaChallengeStore;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>proof-of-work の設定。</summary>
public sealed record AltchaOptions
{
    /// <summary>効かせるか。**既定は有効。**</summary>
    /// <remarks>
    /// **切れるのは検証環境で自動試験を回すときのため。** 本番で切らないこと。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>探させる数の下限。</summary>
    /// <remarks>
    /// **回答者の端末で計算させる。** 大きくするほど bot の費用が上がるが、
    /// **古い携帯の待ち時間も伸びる。** 既定は数百ミリ秒で解ける程度。
    /// </remarks>
    public int MinimumNumber { get; init; } = 50_000;

    /// <summary>探させる数の上限。</summary>
    public int MaximumNumber { get; init; } = 150_000;

    /// <summary>課題の有効期間。</summary>
    /// <remarks>
    /// **短いと、長い設問を書いている間に切れる。**
    /// 長いと、解答を貯めておける時間が延びる。
    /// </remarks>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>設定から読む。</summary>
    public static AltchaOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AltchaOptions
        {
            Enabled = !string.Equals(
                configuration["QUESTIONNAIRE_ALTCHA_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
        };

        if (int.TryParse(configuration["QUESTIONNAIRE_ALTCHA_MIN_NUMBER"], out var minimum)
            && int.TryParse(configuration["QUESTIONNAIRE_ALTCHA_MAX_NUMBER"], out var maximum)
            && minimum > 0
            && maximum >= minimum)
        {
            options = options with { MinimumNumber = minimum, MaximumNumber = maximum };
        }

        return options;
    }
}

/// <summary>解答を受け付けなかった理由。</summary>
public enum AltchaRejection
{
    /// <summary>解答が付いていない。</summary>
    Missing,

    /// <summary>書式が違う、署名が合わない、数が合わない、期限切れ、**使い回し**。</summary>
    Invalid,
}

/// <summary>
/// 回答の送信に proof-of-work を課す。
/// </summary>
/// <remarks>
/// <para>
/// **ALTCHA を自前で設置する**（Issue #55）。
/// **第三者へ回答者の情報を送らない**ので、完全匿名と両立する。
/// 課題を作るのも解答を確かめるのもこのサーバだけで完結する。
/// </para>
/// <para>
/// **<see cref="SubmissionGuard"/> と置き換えない。重ねる。**
/// proof-of-work は「人かどうか」を確かめるものではなく、
/// **総当たりの費用を上げるだけ**。素直に画面を辿る bot は、
/// 時間さえかければ解いて通る。
/// </para>
/// <para>
/// **解答の使い回しを防ぐために、使い終えた課題を DB に覚える**
/// （<see cref="AltchaChallengeLog"/>）。覚えないと、
/// 1 回解くだけで何度でも投稿できる。
/// </para>
/// </remarks>
public sealed class AltchaGuard
{
    /// <summary>署名鍵を用途で分けるためのラベル。</summary>
    /// <remarks>
    /// **同じ鍵を別の用途へそのまま使わない**（<see cref="SubmissionGuard"/> と同じ考え）。
    /// </remarks>
    private static readonly byte[] KeyLabel =
        System.Text.Encoding.UTF8.GetBytes("questionnaire:altcha:v1");

    private readonly AltchaService service;

    public AltchaGuard(string base64Key, AltchaOptions options, AltchaChallengeLog store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        Options = options;

        // **設定の鍵をそのまま使わず、用途ごとに派生させる。**
        // ALTCHA は 64 バイトの鍵を要求する
        var key = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            SecretProtector.DecodeKey(base64Key),
            64,
            info: KeyLabel);

        var configuration = new AltchaSha256Configuration
        {
            Key = AltchaKey.FromBytes(key),
            StoreFactory = () => new StoreAdapter(store),
            Expiry = AltchaExpiry.FromSeconds((int)options.Lifetime.TotalSeconds),
        };

        // **難しさだけ差し替える。** 既定の組み立てを丸ごと置き換えると、
        // 版が上がって項目が増えたときに黙って既定へ戻る
        service = Altcha.CreateService(configuration with
        {
            Complexity = configuration.Complexity with
            {
                Counter = new AltchaComplexityCounterRange(
                    options.MinimumNumber, options.MaximumNumber),
            },
        });
    }

    public AltchaOptions Options { get; }

    /// <summary>課題を作る。</summary>
    /// <remarks>
    /// **DB を見ない。** 見てしまうと、応答の速さで公開 ID の実在が分かる
    /// （<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
    /// </remarks>
    public AltchaChallenge Issue() => service.Generate();

    /// <summary>解答を確かめる。受け付けてよければ <c>null</c>。</summary>
    /// <remarks>
    /// **断る理由を外へ区別して返さないこと**（<see cref="SubmissionGuard"/> と同じ）。
    /// 区別するのはログと試験のためだけ。
    /// </remarks>
    public async Task<AltchaRejection?> CheckAsync(
        string? solution,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(solution))
        {
            return AltchaRejection.Missing;
        }

        // **書式が壊れていても落とさない。** 送りつけるだけで 500 を返すなら、
        // それ自体が攻撃になる
        try
        {
            var result = await service.Validate(solution, cancellationToken).ConfigureAwait(false);
            return result.IsValid ? null : AltchaRejection.Invalid;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AltchaRejection.Invalid;
        }
    }

    /// <summary>ライブラリの受け口へつなぐ。</summary>
    /// <remarks>
    /// **ライブラリの型を Data 層へ持ち込まない**ようにするための薄い層。
    /// Data 層が特定のライブラリに縛られると、差し替えが効かなくなる。
    /// </remarks>
    private sealed class StoreAdapter(AltchaChallengeLog store) : Ixnas.AltchaNet.IAltchaChallengeStore
    {
        public Task Store(string challenge, DateTimeOffset expiryUtc) =>
            store.StoreAsync(challenge, expiryUtc.LocalDateTime);

        public Task<bool> Exists(string challenge) => store.ExistsAsync(challenge);
    }
}
