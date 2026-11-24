using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>管理 API（<c>/api/admin/**</c>）の OpenAPI 文書の組み立て（Issue #1021）。</summary>
/// <remarks>
/// <para>
/// **文書へ載せるのは管理 API だけ。** 回答者向けの口（<c>/api/forms</c> など）は、
/// 外部連携として約束する範囲がまだ決まっていないので載せない。
/// </para>
/// <para>
/// **文書と画面は認証済みの管理者にだけ返す**（Program.cs の <c>MapOpenApi</c> /
/// <c>MapScalarApiReference</c> に <c>SessionPolicy</c> を掛ける）。未認証には 401 を返し、API の一覧を出さない。
/// </para>
/// <para>
/// **個人トークン（API キー）の記述は、口ごとの許可宣言（<c>AllowAutomation(scope)</c>）から生成する**
/// （Issue #1030。親 #1012）。宣言のある口には、セッションと並べて Bearer の要求（スコープつき）を書く。
/// **宣言の無い口は、セッションのままで、Bearer を書かない**（使えない口を、使えるように見せない）。
/// 手書きの一覧は持たない。宣言を足せば文書に出る。食い違いは <c>AdminOpenApiTests</c> が拾う。
/// </para>
/// <para>
/// 閲覧画面（Scalar）の「Try it」（Test Request）は出さない。ブラウザ上のスクリプトからトークンを
/// 送らせないため（方針案 3.5 章・3.11 章）。
/// </para>
/// </remarks>
public static class AdminOpenApi
{
    /// <summary>文書へ載せる経路の先頭（PathBase を除く）。</summary>
    public const string PathPrefix = "api/admin";

    /// <summary>現行のセッション方式の securityScheme の名前。</summary>
    public const string SessionSchemeName = "adminSession";

    /// <summary>個人トークン（API キー）の securityScheme の名前。<c>Authorization: Bearer</c>。</summary>
    public const string TokenSchemeName = "adminApiToken";

    /// <summary>セッション cookie の名前（Program.cs の <c>AddCookie</c> と同じ値）。</summary>
    public const string SessionCookieName = "q.admin";

    /// <summary>文書の組み立てを <see cref="OpenApiOptions"/> へ当てる。</summary>
    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ShouldInclude = description =>
            description.RelativePath is { } path
            && (string.Equals(path, PathPrefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(PathPrefix + "/", StringComparison.OrdinalIgnoreCase));

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info ??= new OpenApiInfo();
            document.Info.Title = "アンケート 管理 API";
            document.Info.Description =
                "管理画面が使う API。外部連携として互換性を約束するものではない。"
                + "認証は管理者のセッション cookie（adminSession）。状態を変える要求は同一オリジンの画面から送ること（CSRF 対策）。"
                + "一部の口だけは、個人トークン（API キー。adminApiToken）を Authorization: Bearer で送っても呼べる。"
                + "呼べる口と要るスコープは、各操作の security に書いてある。security にトークンが無い口は、トークンでは 403。";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SessionSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = SessionCookieName,
                Description = "ログイン（2 要素まで完了）で発行されるセッション cookie。HttpOnly・SameSite=Strict。",
            };

            document.Components.SecuritySchemes[TokenSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "opaque",
                Description =
                    "個人トークン（API キー）。管理画面の「自分のアカウント」で発行し、Authorization: Bearer で送る"
                    + "（形式は qat_ で始まる文字列）。URL・クエリ・本文・cookie では受けない。"
                    + "Bearer を付けた要求は cookie を評価しない。"
                    + "スコープは " + string.Join("・", AdminApiTokenScopes.Allowed)
                    + "。実効の権限は「持ち主の今の役割」と「スコープ」の共通部分。"
                    + "キーの発行・失効はセッションだけ（キーではできない）。"
                    + "ブラウザ上のスクリプトでは使わず、curl・CI・サーバ側のスクリプトから使うこと。",
            };

            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var requiresAuthorization =
                metadata.OfType<IAuthorizeData>().Any()
                && !metadata.OfType<IAllowAnonymous>().Any();
            if (!requiresAuthorization)
            {
                return Task.CompletedTask;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SessionSchemeName, context.Document)] = [],
            });

            // **トークンを書くのは、口が許可を宣言しているときだけ**（`AllowAutomation`）。要求を並べると「いずれか」（OR）。
            // スコープは宣言の値をそのまま使う（認可の判定が見るのと同じ値）
            var access = metadata.OfType<AutomationAccessMetadata>().FirstOrDefault();
            if (access is not null)
            {
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(TokenSchemeName, context.Document)] = [access.Scope],
                });

                var note = "個人トークン（API キー）でも呼べる。要るスコープ: " + access.Scope + "。"
                    + (metadata.OfType<AuditAutomationReadMetadata>().Any()
                        ? "トークンでの読み取りは操作の記録へ残る。"
                        : string.Empty);
                operation.Description = string.IsNullOrEmpty(operation.Description)
                    ? note
                    : operation.Description + Environment.NewLine + Environment.NewLine + note;
            }

            operation.Responses ??= [];
            operation.Responses.TryAdd(
                "401",
                new OpenApiResponse
                {
                    Description = access is null
                        ? "未ログイン、またはセッションの期限切れ"
                        : "未ログイン、またはセッションの期限切れ。トークンの場合は、無効・期限切れ・失効・接続元の不許可（理由は区別しない）",
                });
            operation.Responses.TryAdd(
                "403",
                new OpenApiResponse
                {
                    Description = access is null
                        ? "権限が足りない"
                        : "権限が足りない（トークンのスコープ不足、持ち主の役割の権限不足を含む）",
                });
            return Task.CompletedTask;
        });
    }
}
