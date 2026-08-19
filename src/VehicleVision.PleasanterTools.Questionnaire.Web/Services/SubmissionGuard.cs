using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>bot 対策の設定。</summary>
/// <remarks>**制限値は設定で外部化する**（<c>_documents/非機能設計.md</c> 1 章）。</remarks>
public sealed record SubmissionGuardOptions
{
    /// <summary>bot 対策を効かせるか。**既定は有効。**</summary>
    /// <remarks>
    /// **無効にできるのは、検証環境で自動試験を回すときのため。**
    /// 本番で切らないこと。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>チケットの発行から送信までに、最低限かかるべき時間。</summary>
    /// <remarks>
    /// **人間はフォームを開いた瞬間には送信できない。**
    /// 短すぎると意味が無く、長すぎると 1 問だけのアンケートで正規の回答者を弾く。
    /// </remarks>
    public TimeSpan MinimumElapsed { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>チケットの有効期間。</summary>
    /// <remarks>
    /// **長い設問を書ききるまで開いたままにする人が居る。** 短くしすぎない。
    /// 切れても画面を開き直せば新しいチケットが出る。
    /// </remarks>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>送信を断った理由。</summary>
/// <remarks>
/// **外へは区別せずに返す**（<see cref="SubmissionGuard"/> の注記）。
/// 区別するのはログと試験のためだけ。
/// </remarks>
public enum SubmissionGuardRejection
{
    /// <summary>チケットが付いていない。</summary>
    MissingTicket,

    /// <summary>書式が違う、署名が合わない、別のアンケート・別の回答のもの。</summary>
    InvalidTicket,

    /// <summary>発行から時間が経ちすぎている。</summary>
    ExpiredTicket,

    /// <summary>**人間にはあり得ない速さ**で送ってきた。</summary>
    TooFast,

    /// <summary>画面に見えない項目が埋まっていた。</summary>
    Honeypot,
}

/// <summary>回答の送信が bot によるものかを、外部サービスに頼らずに見る。</summary>
/// <remarks>
/// <para>
/// **外部の CAPTCHA を使わない**（<c>_documents/非機能設計.md</c> 1 章）。
/// 本製品は完全匿名を掲げており、CAPTCHA サービスは回答者の IP や操作の癖を
/// 第三者へ送る。**「匿名で答えられます」と言いながら第三者へ渡すことはできない。**
/// </para>
/// <para>
/// 代わりに、依存を増やさずに済む 3 つを重ねる。
/// </para>
/// <list type="number">
///   <item><description>
///     **送信チケット。** 画面を開いたときにサーバが署名付きで発行し、送信時に検証する。
///     チケットは公開 ID と回答トークンに結び付いているので、
///     **1 枚のチケットで書き込めるのは送信待ちの 1 行だけ**（同じ回答トークンは上書き）。
///     たくさん投稿するには、そのぶんチケットを取りに来る必要があり、
///     そこにレート制限が効く
///   </description></item>
///   <item><description>
///     **投稿までの最短時間。** チケットの発行時刻を署名に含めてあるので、
///     サーバ側に何も覚えずに経過時間を測れる
///   </description></item>
///   <item><description>
///     **ハニーポット項目。** 画面に見えない項目が埋まっていたら bot
///   </description></item>
/// </list>
/// <para>
/// **回答者を識別しない。** チケットに入るのは公開 ID・回答トークン・発行時刻だけで、
/// IP も UA も指紋も入れない。サーバ側に状態を持たないので、
/// 「同じ人が来た」を突き合わせる手掛かりも残らない。
/// </para>
/// <para>
/// **これは万能ではない。** 画面を素直に辿る bot は素通りする。
/// 分散した bot への最後の壁はエッジの WAF であり、ここは多層のうちの 1 枚
/// （<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// </remarks>
public sealed class SubmissionGuard
{
    /// <summary>チケットの書式の版。**変えるときは検証側の分岐を足す。**</summary>
    private const string Version = "t1";

    /// <summary>署名鍵を用途で分けるためのラベル。</summary>
    /// <remarks>
    /// **同じ鍵を別の用途へそのまま使わない。** 片方の署名が
    /// もう片方で通ってしまう事故を、鍵の派生で構造的に無くす。
    /// </remarks>
    private static readonly byte[] KeyLabel =
        Encoding.UTF8.GetBytes("questionnaire:submission-ticket:v1");

    private readonly byte[] key;
    private readonly SubmissionGuardOptions options;
    private readonly TimeProvider time;

    /// <param name="base64Key">
    /// 256 ビットの鍵を Base64 にしたもの（<c>QUESTIONNAIRE_SECRET_KEY</c>）。
    /// </param>
    public SubmissionGuard(string base64Key, SubmissionGuardOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        // **設定の鍵をそのまま使わず、用途ごとに派生させる**
        this.key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, SecretProtector.DecodeKey(base64Key), 32, info: KeyLabel);
        this.options = options;
        this.time = time;
    }

    /// <summary>送信チケットを発行する。</summary>
    /// <remarks>
    /// **DB を見ない。** 見てしまうと、応答の速さで公開 ID の実在が分かる
    /// （<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
    /// </remarks>
    public string Issue(string publicId, string responseToken)
    {
        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();
        var signature = Sign(publicId, responseToken, issuedAt);
        return string.Join('.', Version, issuedAt.ToString(CultureInfo.InvariantCulture), signature);
    }

    /// <summary>送信を受け付けてよいかを見る。受け付けてよければ <c>null</c>。</summary>
    /// <remarks>
    /// **DB へ書く前に呼ぶこと**（<c>_documents/非機能設計.md</c> 1 章）。
    /// 書いてから弾いても消費は起きている。
    /// </remarks>
    /// <param name="trap">
    /// ハニーポット項目の値。**空でなければ bot。**
    /// </param>
    public SubmissionGuardRejection? Check(
        string? ticket, string? trap, string publicId, string responseToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        // **先にハニーポットを見る。** チケットの検証より安い
        if (!string.IsNullOrEmpty(trap))
        {
            return SubmissionGuardRejection.Honeypot;
        }

        if (string.IsNullOrEmpty(ticket))
        {
            return SubmissionGuardRejection.MissingTicket;
        }

        var parts = ticket.Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !long.TryParse(
                parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt))
        {
            return SubmissionGuardRejection.InvalidTicket;
        }

        var expected = Sign(publicId, responseToken, issuedAt);

        // **一致の判定に掛かる時間で中身を漏らさない**
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
        {
            return SubmissionGuardRejection.InvalidTicket;
        }

        var elapsed = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAt);

        // **未来の発行時刻は改竄ではなく時計のずれでも起きる。** どちらにせよ通さない
        if (elapsed < TimeSpan.Zero)
        {
            return SubmissionGuardRejection.InvalidTicket;
        }

        if (elapsed > options.Lifetime)
        {
            return SubmissionGuardRejection.ExpiredTicket;
        }

        return elapsed < options.MinimumElapsed ? SubmissionGuardRejection.TooFast : null;
    }

    private string Sign(string publicId, string responseToken, long issuedAt)
    {
        // **区切りに改行を使い、長さの違いで同じ入力にならないようにする**
        var payload = Encoding.UTF8.GetBytes(
            string.Join(
                '\n',
                Version,
                publicId,
                responseToken,
                issuedAt.ToString(CultureInfo.InvariantCulture)));

        return Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));
    }
}
