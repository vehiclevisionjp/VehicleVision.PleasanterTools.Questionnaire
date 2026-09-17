using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>回答画面から来る要求 1 件。</summary>
/// <param name="Ticket">
/// 送信チケット。**画面を開いたときにサーバが発行したものをそのまま返してもらう**
/// （<see cref="SubmissionGuard"/>）。
/// </param>
/// <param name="Trap">
/// ハニーポット項目。**人間には見えないので、埋まっていたら bot。**
/// 画面側の名前と揃えること。
/// </param>
public sealed record SubmitRequest(
    ImmutableArray<PayloadAnswer> Answers,
    string? Ticket = null,
    string? Trap = null,
    /// <summary>proof-of-work の解答（Issue #55）。**base64 の JSON。**</summary>
    string? Altcha = null,

    /// <summary>
    /// 外部の CAPTCHA の解答（Issue #164）。
    /// </summary>
    /// <remarks>
    /// **外部を選んでいる導入先だけ使う。** 既定（自前設置）では見ない。
    /// ⚠️ **画面から「通った」と言われただけでは通さない。** サーバ側で確かめる
    /// </remarks>
    string? Captcha = null);

/// <summary>送信チケットの要求。</summary>
/// <param name="ResponseToken">
/// 端末が既に持っている回答トークン。**初めての回答なら <c>null</c>。**
/// **URL ではなく本文で受け取る。** URL に載せると経路のログへ残る
/// （<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
/// </param>
public sealed record TicketRequest(string? ResponseToken = null);

/// <summary>回答画面へ返す送信チケット。</summary>
/// <param name="Altcha">
/// proof-of-work の課題（Issue #55）。**切っているときは <c>null</c>。**
/// 画面はこれを解いて、送信時に解答を添える。
/// </param>
public sealed record TicketResponse(
    string ResponseToken,
    string Ticket,
    object? Altcha = null,

    /// <summary>課す課題の種類（Issue #164）。<c>Altcha</c> なら自前設置。</summary>
    string? CaptchaProvider = null,

    /// <summary>外部の CAPTCHA のサイトキー。**秘密鍵は返さない。**</summary>
    string? CaptchaSiteKey = null,

    /// <summary>外部の CAPTCHA のスクリプトの URL。</summary>
    string? CaptchaScriptUrl = null);

/// <summary>回答画面へ返す定義。</summary>
/// <remarks>
/// **Pleasanter の <c>ReferenceId</c> やサイト ID を含めない**
/// （<c>_documents/画面設計.md</c> 1 章）。
/// </remarks>
/// <param name="RequiresProofOfWork">
/// このアンケートが proof-of-work を要るとしているか（Issue #66）。
///
/// **要否を伝えるのはこの口だけ。** 存在しない公開 ID に <c>404</c> を返す口なので、
/// **実在をもともと隠していない。** 課題を出す口では出し分けない
/// （<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
/// </param>
/// <param name="AllowsDraft">
/// 回答の下書きを端末へ残してよいか（Issue #59）。
/// **下書きはサーバへ送らない**ので、伝えるのは可否だけ。
/// </param>
public sealed record FormResponse(
    string PublicId,
    SurveyDefinition Definition,
    bool RequiresProofOfWork,
    bool AllowsDraft = false,
    bool IsTest = false,
    bool RecordsAssetHistory = false);

/// <summary>回答画面向けの口。**認証は無い。**</summary>
public static class FormEndpoints
{
    /// <summary>回答の送信だけに掛けるレート制限の名前。</summary>
    /// <remarks>
    /// **書き込みは読み取りより高くつく。** 全体の枠に紛れさせない
    /// （<c>_documents/非機能設計.md</c> 1 章）。
    /// </remarks>
    public const string SubmitRateLimitPolicy = "form-submit";

    /// <summary>回答トークンの長さ（バイト）。</summary>
    private const int TokenBytes = 24;

    /// <summary>画面と同じ書き方（先頭小文字）で読む。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapFormEndpoints(this IEndpointRouteBuilder app)
    {
        var forms = app.MapGroup("/api/forms")
            .WithTags("回答画面");

        forms.MapGet("/{publicId}", async (
            string publicId,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            var (form, rejection) = await intake.GetPublishedAsync(publicId, cancellationToken);
            return form is null
                ? ToProblem(rejection)
                : Results.Ok(new FormResponse(
                    publicId,
                    form.Definition,
                    form.RequiresProofOfWork,
                    form.AllowsDraft,
                    form.IsTest,
                    form.RecordsAssetHistory));
        });

        // **ヘッダ画像を配る**（Issue #56）。
        // **本アプリが配る。** テーマに外部の URL を持たせていないので、
        // 回答者のブラウザが第三者へ要求を出すことは無い（完全匿名）。
        //
        // **公開中の版が指している画像だけを返す。** 下書きで差し替えた画像も、
        // 停止中のアンケートの画像も出さない。無ければ 404（アンケートが無いときと同じ応答）
        forms.MapGet("/{publicId}/header-image", async (
            string publicId,
            HttpContext context,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            var image = await intake.GetPublishedHeaderImageAsync(publicId, cancellationToken);

            // **型は保存時にサーバが決めた値だが、配る前にもう一度確かめる。**
            // 画像以外の型を自分のドメインから配らない
            if (image is null || !HeaderImage.IsAllowedContentType(image.ContentType))
            {
                return Results.NotFound();
            }

            // **差し替えると識別子が変わる**ので、URL の `?v=` も変わる。
            // 版ごとに別の URL になるため、長く持たせても古い画像が残らない
            context.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(image.Content, image.ContentType);
        });

        // **公開版の本文が参照する自前資産だけを配る**（Issue #266 / #269 / #318）。
        // 設問・説明文の画像は回答前に要るので無条件、完了画面だけの配布物は引換券を要する
        forms.MapGet("/{publicId}/assets/{assetId:guid}", async (
            string publicId,
            Guid assetId,
            HttpContext context,
            ResponseIntake intake,
            IAssetTicketStore assetTickets,
            IAssetHistoryOutbox assetHistory,
            SubmissionGuard guard,
            TimeProvider timeProvider,
            AssetOptions options,
            CancellationToken cancellationToken) =>
        {
            var published = await intake.GetPublishedAssetAsync(publicId, assetId, cancellationToken);
            if (published is null
                || !options.IsAllowed(published.Asset.FileName, published.Asset.ContentType))
            {
                return Results.NotFound();
            }

            if (published.RequiresTicket)
            {
                var ticket = context.Request.Cookies[AssetTicket.CookieName];
                var grant = await AssetTicket.ResolveAccessAsync(
                    ticket,
                    publicId,
                    published.SurveyId,
                    guard,
                    assetTickets,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);
                if (grant is null)
                {
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = "private, no-store";

                if (published.RecordsAssetHistory)
                {
                    await assetHistory.EnqueueAsync(
                        published.SurveyId,
                        published.SurveyVersion,
                        grant.ResponseToken,
                        AssetHistoryEventType.Download,
                        assetId,
                        published.Asset.FileName,
                        timeProvider.GetUtcNow().UtcDateTime,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // **設問・説明文の画像は引換券なし。** 回答中にまだ回答は存在しない
                context.Response.Headers.CacheControl = "public, max-age=86400";
            }

            // **必ず attachment。** PDF や Office 文書をブラウザ内で開かせない
            return Results.File(
                published.Asset.Content,
                published.Asset.ContentType,
                fileDownloadName: published.Asset.FileName);
        });

        // **送信チケットを出す。** 画面を開いた時刻を署名に閉じ込めて返すだけで、
        // サーバ側には何も覚えない（回答者を突き合わせる手掛かりを残さない）。
        //
        // **DB を見ない。** 見ると応答の速さで公開 ID の実在が分かってしまうため、
        // 存在しないアンケートでも同じようにチケットを返す。**使っても受付で断られる。**
        forms.MapPost("/{publicId}/ticket", (
            string publicId,
            TicketRequest request,
            SubmissionGuard guard,
            AltchaGuard altcha,
            CaptchaOptions captcha) =>
        {
            // **端末が持っていない・書式が壊れていれば、こちらで作る。**
            // 回答トークンは推測不能でなければならない値なので、
            // 画面任せにせずサーバの暗号論的乱数から出す
            var responseToken = request.ResponseToken is { } token && IsWellFormedToken(token)
                ? token
                : NewResponseToken();

            // **課題もここで出す。** 画面を開いた時点から解き始められるので、
            // 書き終えるころには計算が済んでいる（待たせない）。
            //
            // **アンケートごとの要否では出し分けない**（Issue #66）。
            // 出し分けるには DB を見るしかなく、見た時点で応答の速さから
            // 公開 ID の実在が分かる。**要否は `GET /api/forms/{publicId}` で伝える**
            // **外部の CAPTCHA を選んでいるなら、自前の課題は出さない**（Issue #164）。
            // 課すのは 1 つだけ。両方出すと、回答者に二重の手間をかける
            var usesExternalCaptcha = captcha.IsExternalReady;

            return Results.Ok(new TicketResponse(
                responseToken,
                guard.Issue(publicId, responseToken),
                !usesExternalCaptcha && altcha.Options.Enabled ? altcha.Issue() : null,
                captcha.Provider.ToString(),

                // **秘密鍵は返さない。** 画面へ渡すのはサイトキーだけ
                usesExternalCaptcha ? captcha.SiteKey : null,
                usesExternalCaptcha ? captcha.ScriptUrl : null));
        });

        // ---- 再編集リンクの引き換え（Issue #202）-------------------------------
        // ⚠️ **トークンは本文で受ける。** 経路（URL）へ載せると、Web サーバの
        // アクセスログにも、経路の値を丸ごと書く監査ログにも残る。
        // **リンクでは URL の断片（`#` の後ろ）に置く**ので、そもそもサーバへ送られない
        forms.MapPost("/{publicId}/edit-link", async (
            string publicId,
            EditLinkRedeemRequest request,
            ResponseIntake intake,
            IResponseEditTokenStore editTokens,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.EditToken))
            {
                return Results.NotFound();
            }

            // **受け付けられる状態かを先に見る。** 受付を止めたら即失効
            var (form, rejection) = await intake.GetPublishedAsync(publicId, cancellationToken);
            if (form is null || rejection is not null)
            {
                return Results.NotFound();
            }

            var responseToken = await editTokens.RedeemAsync(
                ResponseEditLink.HashOf(request.EditToken),
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);

            // ⚠️ **理由を区別して返さない。** 期限切れ・失効済み・無いトークンを
            // 見分けられると、総当たりで実在が分かってしまう
            return responseToken is null
                ? Results.NotFound()
                : Results.Ok(new { responseToken });
        }).RequireRateLimiting(SubmitRateLimitPolicy);

        // ---- 配布資産の引換券（Issue #318）-----------------------------------
        forms.MapPost("/{publicId}/asset-ticket", async (
            string publicId,
            AssetTicketRedeemRequest request,
            HttpContext context,
            ResponseIntake intake,
            IAssetTicketStore assetTickets,
            IAssetHistoryOutbox assetHistory,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.AssetTicket))
            {
                return Results.NotFound();
            }

            // **受付終了・停止中でも使える。** アーカイブ・削除だけはここで拒否する
            var form = await intake.GetAssetTicketFormAsync(publicId, cancellationToken);
            if (form is null)
            {
                return Results.NotFound();
            }

            var grant = await assetTickets.RedeemAsync(
                AssetTicket.HashOf(request.AssetTicket),
                form.SurveyId,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
            if (grant is null)
            {
                return Results.NotFound();
            }

            if (form.RecordsAssetHistory)
            {
                await assetHistory.EnqueueAsync(
                    form.SurveyId,
                    form.SurveyVersion,
                    grant.ResponseToken,
                    AssetHistoryEventType.Revisit,
                    assetId: null,
                    assetFileName: null,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken).ConfigureAwait(false);
            }

            context.Response.Cookies.Append(
                AssetTicket.CookieName,
                request.AssetTicket,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = $"/api/forms/{Uri.EscapeDataString(publicId)}/assets",
                    Expires = new DateTimeOffset(DateTime.SpecifyKind(
                        grant.ExpiresAtUtc, DateTimeKind.Utc)),
                });

            return Results.Ok(new FormResponse(
                publicId,
                form.Definition,
                RequiresProofOfWork: false,
                RecordsAssetHistory: form.RecordsAssetHistory));
        }).RequireRateLimiting(SubmitRateLimitPolicy);

        forms.MapGet("/{publicId}/responses/{responseToken}", async (
            string publicId,
            string responseToken,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            // **送信待ちを先に見る。** 未送信の回答は Pleasanter にまだ無い
            var pending = await intake.FindPendingAsync(responseToken, cancellationToken);
            return pending is null
                ? Results.NotFound()
                : Results.Ok(new { answers = pending.Answers });
        });

        // **添付は回答と同じ要求で受け取る**（`multipart/form-data`）。
        // 添付だけ先に預かる口を作ると、送信されないまま残った中身の始末が要る。
        // 添付が無いときは今までどおり JSON で送れる
        forms.MapPut("/{publicId}/responses/{responseToken}", async (
            HttpContext context,
            string publicId,
            string responseToken,
            ResponseIntake intake,
            SubmissionGuard guard,
            AltchaGuard altcha,
            CaptchaOptions captchaOptions,
            CaptchaVerifier captchaVerifier,
            ILogger<SubmissionGuard> logger,
            AttachmentOptions attachmentOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!IsWellFormedToken(responseToken))
            {
                return Results.BadRequest();
            }

            // **要求本文の上限を明示する。既定値に任せない**（_documents/非機能設計.md 1 章）
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = attachmentOptions.MaxRequestBodyBytes;
            }

            SubmitRequest? request;
            IReadOnlyList<AnsweredAttachment> attachments;

            try
            {
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(cancellationToken);
                    request = ReadSubmitRequest(form["answers"]);
                    attachments = await ReadAttachmentsAsync(form.Files, cancellationToken);
                }
                else
                {
                    request = await context.Request.ReadFromJsonAsync<SubmitRequest>(cancellationToken);
                    attachments = [];
                }
            }
            catch (BadHttpRequestException exception)
            {
                // 上限超過（413）と壊れた本文（400）を、そのままの意味で返す
                return Results.StatusCode(exception.StatusCode);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (request is null)
            {
                return Results.BadRequest();
            }

            // **DB を見る前に bot を弾く**（_documents/非機能設計.md 1 章）。
            // 書いてから弾いても、DB の消費はもう起きている。
            // **アンケートの実在を確かめる前に弾くので、ここで断った応答は
            // 公開 ID が実在するかを漏らさない。**
            //
            // **本文を読んだ後でしか判定できない。** チケットと罠は本文に載っている。
            // 添付を伴う要求では、その分だけ読み込みが先に走る（上限は上で掛けてある）
            var botRejection = guard.Check(request.Ticket, request.Trap, publicId, responseToken);
            if (botRejection is { } reason)
            {
                // **理由は外へ返さない。** 返すと、bot がどこを直せばよいか分かる。
                // 記録は残す（回答本文とトークンは書かない）
                logger.LogWarning("回答の送信を bot 対策で断った。理由: {Reason}", reason);
                return Results.Json(
                    new { reason = "rejected" }, statusCode: StatusCodes.Status403Forbidden);
            }

            // **proof-of-work も見る。** チケットと重ねる（Issue #55）。
            // **同じ解答は 2 度通らない**（使い終えた課題を覚えている）。
            //
            // **要否は必ず DB の旗で決める**（Issue #66）。画面へも要否を返しているが、
            // **画面が「要らない」と言ってきても信じない。** 信じると、
            // 解答を付けずに投げるだけで proof-of-work を外せる。
            //
            // **アンケートが無いときも「要る」**として扱う（`RequiresProofOfWorkAsync`）。
            // ここで「無いから要らない」にすると、公開 ID の実在が応答から分かる
            //
            // **外部の CAPTCHA を選んでいる導入先では、そちらを課す**（Issue #164）。
            // 要否の旗は共通で、**何を課すかだけが変わる**（設定を増やさない）
            var requiresChallenge = await intake.RequiresProofOfWorkAsync(publicId, cancellationToken);

            if (requiresChallenge && captchaOptions.IsExternalReady)
            {
                // ⚠️ **画面から「通った」と言われただけでは通さない。**
                // 解答はサービス側でしか確かめられない
                var outcome = await captchaVerifier
                    .VerifyAsync(request.Captcha, context.Connection.RemoteIpAddress?.ToString(), cancellationToken)
                    .ConfigureAwait(false);

                if (outcome is not CaptchaOutcome.Succeeded)
                {
                    // **理由は外へ返さない**（上と同じ）。
                    // ⚠️ **到達できないときも断る**（fail closed）。
                    // 通すと、外部が落ちている間だけ bot が素通りする
                    logger.LogWarning("回答の送信を CAPTCHA で断った。理由: {Reason}", outcome);
                    return Results.Json(
                        new { reason = "rejected" }, statusCode: StatusCodes.Status403Forbidden);
                }
            }
            else if (requiresChallenge
                && altcha.Options.Enabled
                && await altcha.CheckAsync(request.Altcha, cancellationToken) is { } altchaReason)
            {
                // **理由は外へ返さない**（上と同じ）
                logger.LogWarning("回答の送信を proof-of-work で断った。理由: {Reason}", altchaReason);
                return Results.Json(
                    new { reason = "rejected" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var answers = request.Answers
                .Select(answer => new Answer(answer.QuestionId, answer.Values)
                {
                    OtherText = answer.OtherText,
                    FileNames = answer.FileNames.IsDefault ? [] : answer.FileNames,

                    // **グリッドの回答はここにしか入っていない**（Issue #74）。
                    // 落とすと、答えたのに未回答として弾かれる
                    Rows = answer.Rows ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty,
                })
                .ToArray();

            // **回答者の言語を渡す**（Issue #189）。自動返信の文言をその言語で作る。
            // **回答そのものには残さない**（正本 JSON は言語を持たない）
            var result = await intake.SubmitAsync(
                publicId,
                responseToken,
                answers,
                attachments,
                RequestLanguage.Of(context),
                cancellationToken);

            if (result.Accepted)
            {
                if (result.GrantsInstantAssetAccess)
                {
                    // **セッション Cookie。** 署名内の発行時刻で 30 分に制限する。
                    // DB へ引換券を保存せず、この送信直後の画面だけで使わせる。
                    context.Response.Cookies.Append(
                        AssetTicket.CookieName,
                        guard.IssueAssetAccess(publicId, responseToken),
                        new CookieOptions
                        {
                            HttpOnly = true,
                            Secure = context.Request.IsHttps,
                            SameSite = SameSiteMode.Lax,
                            Path = $"/api/forms/{Uri.EscapeDataString(publicId)}/assets",
                        });
                }

                // **受付完了。** Pleasanter へはこの後ワーカーが送る
                return Results.Accepted(value: new { assetTicket = result.AssetTicket });
            }

            return result.Rejection switch
            {
                IntakeRejection.Invalid => Results.ValidationProblem(ToValidationErrors(result)),
                IntakeRejection.AttachmentRejected => ToAttachmentProblem(
                    result, loggerFactory.CreateLogger(typeof(FormEndpoints))),
                _ => ToProblem(result.Rejection),
            };
        }).RequireRateLimiting(SubmitRateLimitPolicy);

        return app;
    }

    /// <summary>再編集リンクの引き換えで受け取る中身（Issue #202）。</summary>
    /// <remarks>⚠️ **経路ではなく本文で受け取る。** ログへ載せないため。</remarks>
    public sealed record EditLinkRedeemRequest(string? EditToken);

    /// <summary>配布資産の引換券。⚠️ **経路ではなく本文で受け取る。**</summary>
    public sealed record AssetTicketRedeemRequest(string? AssetTicket);

    /// <summary>回答トークンを作る。**暗号論的乱数から作る。**</summary>
    /// <remarks>
    /// **これを知っている人はその回答を書き換えられる**
    /// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
    /// URL・メール・ログへ載せる経路を作らないこと。
    /// </remarks>
    public static string NewResponseToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));

    private static bool IsWellFormedToken(string token) =>
        token.Length == TokenBytes * 2 && token.All(Uri.IsHexDigit);

    /// <summary><c>multipart/form-data</c> の <c>answers</c> 欄を読む。</summary>
    private static SubmitRequest? ReadSubmitRequest(string? answersJson) =>
        string.IsNullOrWhiteSpace(answersJson)
            ? new SubmitRequest([])
            : JsonSerializer.Deserialize<SubmitRequest>(answersJson, JsonOptions);

    /// <summary>添付を読み取る。**欄の名前がどの設問かを表す。**</summary>
    /// <remarks>
    /// **ファイル名は画面が名乗ったまま渡す。** ここで整形すると、
    /// パス区切りを含む名前を弾く検査（<c>AttachmentInspector</c>）が働かなくなる。
    /// </remarks>
    private static async Task<IReadOnlyList<AnsweredAttachment>> ReadAttachmentsAsync(
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        var attachments = new List<AnsweredAttachment>(files.Count);

        foreach (var file in files)
        {
            using var buffer = new MemoryStream((int)Math.Max(file.Length, 0));
            await file.CopyToAsync(buffer, cancellationToken);
            attachments.Add(new AnsweredAttachment(
                file.Name, new IncomingAttachment(file.FileName, buffer.ToArray())));
        }

        return attachments;
    }

    /// <summary>添付を受け付けなかったことを応答にする。</summary>
    /// <remarks>
    /// **検出名を回答者へ出さない**（<c>_documents/添付ファイル検査-運用手順書.md</c> 6 章）。
    /// 検出したことも理由としては伏せ、「受け付けられない」とだけ返す。
    /// **スキャナへ到達できないときは 503。** 「スキャンできなかったので通す」にしない。
    /// </remarks>
    private static IResult ToAttachmentProblem(IntakeResult result, ILogger logger)
    {
        // **回答本文と中身は残さない。** 記録するのはファイル名と理由だけ
        foreach (var rejection in result.Attachments)
        {
            logger.LogWarning(
                "添付を受け付けなかった: 理由={Reason} 設問={QuestionId} ファイル={FileName}",
                rejection.Reason,
                rejection.QuestionId,
                rejection.FileName);
        }

        if (result.Attachments.Any(
            rejection => rejection.Reason is AttachmentRejectionReason.ScannerUnavailable))
        {
            // **管理者への通知が要る状態。** 添付を受け付けられないまま運用が続いている
            logger.LogError("ウイルススキャナへ到達できないため添付を受け付けていない");
            return Results.Json(
                new { reason = "scannerUnavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new
            {
                attachments = result.Attachments.Select(rejection => new
                {
                    questionId = rejection.QuestionId,
                    fileName = rejection.FileName,
                    reason = PublicReason(rejection.Reason),
                }),
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>回答者へ返してよい理由へ丸める。</summary>
    private static string PublicReason(AttachmentRejectionReason reason) => reason switch
    {
        // **何を検出したかはもちろん、検出したこと自体も伏せる**
        AttachmentRejectionReason.Infected => "rejected",
        _ => char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..],
    };

    /// <summary>断った理由を応答にする。</summary>
    /// <remarks>
    /// **存在しない公開 ID と未公開を区別しない。**
    /// 区別すると、総当たりで「実在するか」が分かってしまう。
    /// </remarks>
    private static IResult ToProblem(IntakeRejection? rejection) => rejection switch
    {
        IntakeRejection.NotStarted => Results.Json(
            new { reason = "notStarted" }, statusCode: StatusCodes.Status403Forbidden),
        IntakeRejection.Closed => Results.Json(
            new { reason = "closed" }, statusCode: StatusCodes.Status403Forbidden),
        IntakeRejection.Suspended => Results.Json(
            new { reason = "suspended" }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.NotFound(),
    };

    private static Dictionary<string, string[]> ToValidationErrors(IntakeResult result) =>
        result.Errors
            .GroupBy(error => error.QuestionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Code.ToString()).ToArray(),
                StringComparer.Ordinal);
}
