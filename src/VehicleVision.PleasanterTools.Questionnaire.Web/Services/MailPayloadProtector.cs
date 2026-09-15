using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>送信待ちのメールを、2 要素の共有鍵と同じ仕掛けで守る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **宛先は回答者のメールアドレスで、本文には回答の写しが入り得る。**
/// 完全匿名を前提にした本アプリで、**個人を指す値が DB に載る唯一の場所**なので、
/// 平文で置かない（<c>_documents/データモデル設計.md</c> 2.6 と同じ考え方）。
/// </para>
/// <para>
/// **鍵は <c>QUESTIONNAIRE_SECRET_KEY</c> を使い回す。** 鍵を増やすと、
/// 運用側が守るものが増え、**片方だけ控えを取り損ねる**事故が起きる。
/// </para>
/// <para>
/// ⚠️ **鍵を失うと、送信待ちのメールは送れない**（復号できない行はデッドレターへ回る）。
/// 2 要素の共有鍵と同じ性質で、設計どおりの挙動。回答そのものは失われない。
/// </para>
/// </remarks>
public sealed class MailPayloadProtector(SecretProtector protector) : IMailPayloadProtector
{
    public string Protect(OutgoingMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return protector.Protect(MailPayload.ToJson(mail));
    }

    public OutgoingMail? Unprotect(string protectedPayload)
    {
        var json = protector.Unprotect(protectedPayload);
        return json is null ? null : MailPayload.FromJson(json);
    }
}
