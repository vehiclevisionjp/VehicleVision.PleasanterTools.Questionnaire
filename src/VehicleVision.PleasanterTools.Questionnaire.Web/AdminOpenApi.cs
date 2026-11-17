using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

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
/// </remarks>
public static class AdminOpenApi
{
    /// <summary>文書へ載せる経路の先頭（PathBase を除く）。</summary>
    public const string PathPrefix = "api/admin";

    /// <summary>現行のセッション方式の securityScheme の名前。</summary>
    public const string SessionSchemeName = "adminSession";

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
                + "認証は管理者のセッション cookie。状態を変える要求は同一オリジンの画面から送ること（CSRF 対策）。";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SessionSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = SessionCookieName,
                Description = "ログイン（2 要素まで完了）で発行されるセッション cookie。HttpOnly・SameSite=Strict。",
            };

            // 追記の場所（Issue #1012 トークン認証。設計案は PR #1015。未実装なので定義しない）:
            //   1. ここの SecuritySchemes へ、実装した方式のスキームを足す。
            //      例: document.Components.SecuritySchemes["adminToken"] =
            //              new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer" };
            //   2. 下の操作ごとの変換で、認可の方針が受け付ける方式に合わせて
            //      operation.Security へ要求を足す。要求を複数並べると「いずれか」（OR）になる。
            //   3. 受け付ける方式は、認可の方針（Program.cs の AddAuthorization）と一致させる。
            //      食い違いは AdminOpenApiDriftTests が拾う。
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
            operation.Responses ??= [];
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "未ログイン、またはセッションの期限切れ" });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "権限が足りない" });
            return Task.CompletedTask;
        });
    }
}
