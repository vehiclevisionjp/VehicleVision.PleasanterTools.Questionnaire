namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答者へ送る自動返信メールの設定（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **定義の側に持つ。** 件名と本文は回答者に見える文言で、多言語で持ち、
/// **公開した版で固定される**（<c>_documents/データモデル設計.md</c> 1 章）。
/// 受付期間や回答数の上限のような「運用の設定」とは性質が違う。
/// </para>
/// <para>
/// ⚠️ **既定は無効。** 何も設定しなければ 1 通も出ない。
/// **完全匿名が前提のアプリで、回答者のメールアドレスを扱う唯一の機能**なので、
/// 有効にしたことが定義に明示されていない限り送らない。
/// </para>
/// <para>
/// **宛先は「メール形式の設問への回答」から採る。** アンケートに宛先を書く欄が無ければ
/// 送りようが無く、**こちらで勝手に集める経路も作らない。**
/// </para>
/// </remarks>
public sealed record AutoReplySettings
{
    /// <summary>自動返信を送るか。</summary>
    public bool Enabled { get; init; }

    /// <summary>宛先にする設問。**メール形式（<see cref="TextFormat.Email"/>）に限る。**</summary>
    /// <remarks>
    /// **未回答なら送らない。** 任意の設問を指定してもよく、答えた人にだけ届く。
    /// </remarks>
    public string? ToQuestionId { get; init; }

    /// <summary>件名。</summary>
    public LocalizedText? Subject { get; init; }

    /// <summary>本文。**平文。** 書式は持たない。</summary>
    /// <remarks>
    /// **HTML にしない**（<c>OutgoingMail</c>）。管理者が書いた文面と回答の写しが
    /// そのまま入るので、逃がし漏れが即そのまま差し込みになる経路を作らない。
    /// </remarks>
    public LocalizedText? Body { get; init; }

    /// <summary>本文のあとに、回答を直すためのリンクを付けるか（Issue #202）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **このリンクを持つ人は、その回答を書き換えられる。**
    /// 転送・共有メールボックスでは他人でも直せる。**割り切って受け入れる**
    /// （2026-09-14 決定。完全匿名の公開フォームで、それ以上の本人確認をする前提が無い）。
    /// </para>
    /// <para>
    /// **回答の編集を許していないアンケートでは付けられない**
    /// （<see cref="SurveyDefinition.AllowEditingAfterSubmit"/>。公開のときに弾く）。
    /// </para>
    /// </remarks>
    public bool IncludeEditLink { get; init; }

    /// <summary>再編集リンクの有効日数。**既定は 7 日。**</summary>
    /// <remarks>
    /// **受付期間の終了を超えない。受付を止めたら即失効する**（送る側・引き換える側で担保）。
    /// </remarks>
    public int EditLinkDays { get; init; } = DefaultEditLinkDays;

    /// <summary>再編集リンクの既定の有効日数。</summary>
    public const int DefaultEditLinkDays = 7;

    /// <summary>指定できる有効日数の上限。**永久に生きるリンクを作らせない。**</summary>
    public const int MaxEditLinkDays = 365;

    /// <summary>本文のあとに回答の写しを付けるか。</summary>
    /// <remarks>
    /// ⚠️ **付けると、回答の中身がメールとして外へ出る。**
    /// 受け取るのは回答者本人だが、**経路は暗号化されているとは限らない。**
    /// 既定は付けない。
    /// </remarks>
    public bool IncludeAnswers { get; init; }
}
