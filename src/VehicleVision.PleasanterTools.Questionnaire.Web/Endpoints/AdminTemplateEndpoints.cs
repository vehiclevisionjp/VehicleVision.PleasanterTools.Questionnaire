using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>アンケートのテンプレートの入口（Issue #58）。</summary>
/// <remarks>
/// <para>
/// **よくある形を毎回作り直さずに済ませるためのもの。**
/// 複製（Issue #46）と同じく設問・選択肢・分岐・マッピングを写すが、
/// **テンプレートは Pleasanter のサイトを持たない**ので、複製と同じ扱いにはできない。
/// **そこからアンケートを作るときに、書き込み先のサイトを指定させる。**
/// </para>
/// <para>
/// **Administrator だけが触れる。** テンプレートから作るのは、
/// 書き込み先のサイトを新しく決める操作であり、誤ると別の業務のサイトへ回答が流れ込む
/// （複製と同じ理由）。**作る側と使う側で権限を分けない。**
/// 分けると Editor には半分しか使えない機能になる。
/// </para>
/// <para>
/// **既定のテンプレートは配らない。** 配ると、こちらが直すたびに
/// 既に手を入れた利用者の分をどうするかという判断が要る（Issue #58 の但し書き）。
/// </para>
/// </remarks>
public static class AdminTemplateEndpoints
{
    public static IEndpointRouteBuilder MapAdminTemplateEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/templates")
            .RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

        AdminAuthSchemes.AddNoStore(group);

        // **作った・使った・消したを残す**（読み取りは残さない。AuditLogFilter）
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- 一覧 ------------------------------------------------------------
        group.MapGet("/", async (ISurveyDraftStore drafts, CancellationToken cancellationToken) =>
            Results.Ok(await drafts.ListTemplatesAsync(cancellationToken).ConfigureAwait(false)));

        // ---- アンケートからテンプレートを作る --------------------------------
        group.MapPost("/", async (
            CreateTemplateRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            if (request.SurveyId is not { } surveyId || surveyId == Guid.Empty)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.TemplateSourceRequired, RequestLanguage.Of(context)),
                });
            }

            var target = new SurveyTemplateTarget(
                Guid.NewGuid(),
                // **公開用 ID は使い回さない**（_documents/データモデル設計.md 3 章）。
                // テンプレートは公開しないので誰にも渡らないが、列が一意かつ NOT NULL
                SurveyPublicId.Generate());

            // **元がテンプレートなら断る**（ストア側で見ている）。
            // テンプレートからテンプレートを作れると、写しの写しが増えるだけになる
            var created = await drafts.SaveAsTemplateAsync(surveyId, target, cancellationToken)
                .ConfigureAwait(false);

            return created
                ? Results.Created(
                    $"/api/admin/templates/{target.TemplateId}",
                    new { templateId = target.TemplateId })
                : Results.NotFound();
        });

        // ---- テンプレートからアンケートを作る --------------------------------
        group.MapPost("/{templateId:guid}/surveys", async (
            Guid templateId,
            CreateFromTemplateRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            // **サイト ID は必ず指定させる。** テンプレートは持っていないので、
            // 写して埋めることができない
            if (request.PleasanterSiteId <= 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PleasanterSiteIdRequired, RequestLanguage.Of(context)),
                });
            }

            var target = new SurveyDuplicationTarget(
                Guid.NewGuid(),
                // **公開用 ID は新しく作る**（_documents/データモデル設計.md 3 章）。
                // テンプレートのものを使い回すと、同じテンプレートから作った
                // アンケートが全部同じ URL を指す
                SurveyPublicId.Generate(),
                request.PleasanterSiteId,
                string.IsNullOrWhiteSpace(request.ResponseJsonColumn)
                    ? null
                    : request.ResponseJsonColumn.Trim());

            // **失敗したら 1 行も残さない。** 中途半端な行は画面からも消せない
            var created = await drafts.CreateFromTemplateAsync(templateId, target, cancellationToken)
                .ConfigureAwait(false);

            return created
                ? Results.Created(
                    $"/api/admin/surveys/{target.SurveyId}",
                    new { surveyId = target.SurveyId, target.PublicId })
                : Results.NotFound();
        });

        // ---- テンプレートを消す ----------------------------------------------
        // **消せるのはテンプレートだけ。** アンケートには回答が紐づいており、
        // 消すと Pleasanter 側に残った回答の出どころが辿れなくなる
        group.MapDelete("/{templateId:guid}", async (
            Guid templateId,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            var deleted = await drafts.DeleteTemplateAsync(templateId, cancellationToken)
                .ConfigureAwait(false);

            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return builder;
    }

    /// <summary>アンケートからテンプレートを作る。</summary>
    /// <param name="SurveyId">
    /// 写す元のアンケート。**経路ではなく本文で受ける。**
    /// テンプレートの入口に置いており、経路の識別子は作られるテンプレートを指すため。
    /// </param>
    /// <remarks>
    /// **題名は指定させない。** 元のアンケートの題名をそのまま写す。
    /// 題名は多言語の器（<see cref="LocalizedText"/>）であり、
    /// ここで 1 つの文字列を受け取ると、**日本語の題名が英語として保存される**
    /// （<c>_documents/多言語対応方針.md</c> 5 章）。
    /// 名前を変えたいときは、元のアンケートの題名を直してからテンプレートにすること。
    /// </remarks>
    public sealed record CreateTemplateRequest(Guid? SurveyId);

    /// <summary>テンプレートからアンケートを作る。</summary>
    /// <param name="PleasanterSiteId">
    /// **書き込み先のサイト。テンプレートは持っていないので、必ず指定させる。**
    /// </param>
    /// <param name="ResponseJsonColumn">
    /// 回答 JSON の正本を入れる列。**サイトに紐づく値なので写さない。**
    /// </param>
    public sealed record CreateFromTemplateRequest(
        long PleasanterSiteId,
        string? ResponseJsonColumn);
}
