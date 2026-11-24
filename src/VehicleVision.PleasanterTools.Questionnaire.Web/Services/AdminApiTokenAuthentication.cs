using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 管理 API の個人トークン（API キー）を <c>Authorization: Bearer</c> で受ける認証の部品（Issue #1026。親 #1012）。
/// </summary>
/// <remarks>
/// <para>
/// <b>守る条件</b>（<c>_documents/管理APIのトークン認証-方針案.md</c> 3.5 章）。
/// </para>
/// <list type="number">
/// <item><description>
/// <b><c>Authorization: Bearer</c> がある管理 API の要求は、トークンだけで認証する。cookie は評価しない。</b>
/// 認証スキームの選択は、ヘッダの有無だけで決める（<see cref="UsesBearer"/>）。
/// 既定の認証スキームもこの選択に載せてあり、要求の先頭の認証も cookie を読まない。
/// </description></item>
/// <item><description>
/// <b>トークンを URL・クエリ・本文で受けない。</b>読むのは <c>Authorization</c> ヘッダだけ。
/// </description></item>
/// <item><description>
/// <b>拒まれても cookie へ戻らない。</b>失敗の理由は外へ区別せず、同じ <c>401</c>（理由は <see cref="ValidationOf"/> に残す）。
/// </description></item>
/// <item><description>
/// <b>トークンを許す口は、口ごとに宣言する</b>（<see cref="AutomationEndpointExtensions.AllowAutomation"/>。既定は拒否）。
/// 群のポリシーへ Bearer を足さない（群の全口が開く）。
/// </description></item>
/// </list>
/// </remarks>
public static class AdminApiTokenAuthentication
{
    /// <summary>認証の結果（<see cref="AdminApiTokenAuthenticationResult"/>）を要求の中で使い回す鍵。</summary>
    private const string ResultItemKey = "Questionnaire.AdminApiToken.AuthenticationResult";

    /// <summary>管理 API の範囲の先頭（PathBase を剥がした後の Path）。<b>公開の回答画面へは Bearer を持ち込まない。</b></summary>
    private const string ProtectedPrefix = "/api/admin";

    /// <summary>認証の主体の種類を表す claim。<b>セッションの principal にはこの claim が無い。</b></summary>
    public const string AuthKindClaim = "q.auth.kind";

    /// <summary><see cref="AuthKindClaim"/> の値（個人トークン）。</summary>
    public const string AuthKindApiToken = "api-token";

    /// <summary>トークンの識別子（<see cref="AdminApiTokenRecord.TokenId"/>）の claim。監査へ渡す用。</summary>
    public const string TokenIdClaim = "q.token.id";

    /// <summary>トークンの接頭辞の claim。監査へ渡す用（<b>値そのものではない</b>）。</summary>
    public const string TokenPrefixClaim = "q.token.prefix";

    /// <summary>実効の権限の claim（権限 1 つにつき 1 本）。<b>役割の claim は持たせない。</b></summary>
    public const string PermissionClaim = "q.token.permission";

    /// <summary>この要求は、Bearer で認証する要求か。</summary>
    /// <remarks>
    /// 管理 API（<c>/api/admin/**</c>）で、<c>Authorization</c> ヘッダに <c>Bearer</c> がある要求。
    /// <b>他の方式（Basic など）や、公開の回答画面の要求は対象にしない。</b>
    /// </remarks>
    public static bool UsesBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Path.StartsWithSegments(ProtectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var value in request.Headers.Authorization)
        {
            if (IsBearer(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>認証の結果から、トークンの検証の結果を取り出す。<b>認証を通っていない（Bearer でない）なら <c>null</c></b>。</summary>
    /// <remarks>
    /// <b>#1027 が、認証の失敗（<see cref="AdminApiTokenValidation.Rejection"/>）を監査へ流すための接続点。</b>
    /// 失敗の応答は区別しないので、理由はここにしか残らない。
    /// </remarks>
    public static AdminApiTokenValidation? ValidationOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.Items[ResultItemKey] as AdminApiTokenAuthenticationResult)?.Validation;
    }

    /// <summary>この principal は、個人トークンで認証されたものか。</summary>
    public static bool IsApiToken(ClaimsPrincipal? principal) =>
        principal?.HasClaim(AuthKindClaim, AuthKindApiToken) == true;

    /// <summary>トークンの principal の実効の権限。</summary>
    public static IReadOnlySet<string> PermissionsOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindAll(PermissionClaim).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
    }

    internal static async Task<AdminApiTokenAuthenticationResult> AuthenticateAsync(HttpContext context)
    {
        if (context.Items[ResultItemKey] is AdminApiTokenAuthenticationResult cached)
        {
            return cached;
        }

        var result = await ValidateAsync(context).ConfigureAwait(false);
        context.Items[ResultItemKey] = result;
        return result;
    }

    private static async Task<AdminApiTokenAuthenticationResult> ValidateAsync(HttpContext context)
    {
        // **ヘッダが 2 本以上なら、どちらも信じない。** 経路の途中で足された値と取り違えない
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || !IsBearer(headers[0]))
        {
            return new AdminApiTokenAuthenticationResult(null, null);
        }

        var token = headers[0]!["Bearer".Length..].Trim();
        var service = context.RequestServices.GetRequiredService<AdminApiTokenService>();
        var validation = await service
            .ValidateAsync(token, context.Connection.RemoteIpAddress, context.RequestAborted)
            .ConfigureAwait(false);

        if (!validation.Succeeded)
        {
            return new AdminApiTokenAuthenticationResult(null, validation);
        }

        // ⚠️ **役割の claim を持たせない。** 役割で判定する経路（Role を見る口）へ、持ち主の役割のまま入らせない。
        // 権限は、実効の権限（持ち主の役割 ∩ スコープ）の claim だけ
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, validation.Owner!.Id.ToString()),
            new(ClaimTypes.Name, validation.Owner.LoginId),
            new(AuthKindClaim, AuthKindApiToken),
            new(TokenIdClaim, validation.Token!.TokenId.ToString()),
            new(TokenPrefixClaim, validation.Token.Prefix),
        };
        claims.AddRange(validation.EffectivePermissions.Select(permission => new Claim(PermissionClaim, permission)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AdminAuthSchemes.ApiToken));
        return new AdminApiTokenAuthenticationResult(principal, validation);
    }

    private static bool IsBearer(string? value) =>
        value is not null
        && value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)
        && (value.Length == "Bearer".Length || char.IsWhiteSpace(value["Bearer".Length]));
}

/// <summary>Bearer の認証の 1 回分の結果。</summary>
/// <param name="Principal">通ったときの principal。通らなければ <c>null</c>。</param>
/// <param name="Validation">トークンの検証の結果。ヘッダの形が違うときは <c>null</c>。</param>
internal sealed record AdminApiTokenAuthenticationResult(ClaimsPrincipal? Principal, AdminApiTokenValidation? Validation);

/// <summary>Bearer のトークンを認証する。<b>cookie には触らない。</b></summary>
/// <remarks>
/// 失敗は理由を区別せず、同じ <c>401</c>（<c>WWW-Authenticate: Bearer</c> だけ）にする。
/// 認可で足りなかったときは <c>403</c>。
/// </remarks>
public sealed class AdminApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var result = await AdminApiTokenAuthentication.AuthenticateAsync(Context).ConfigureAwait(false);
        if (result.Principal is null)
        {
            // **理由は返さない。** 認証失敗の理由は、要求の中の検証の結果にだけ残る
            return AuthenticateResult.Fail("invalid_token");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(result.Principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 個人トークンで入ってよい口の宣言（エンドポイントのメタデータ）。<b>資格情報の種類に依らない名前</b>
/// （後で OAuth を足しても、宣言は変えない。設計案 3.10.4 章）。
/// </summary>
/// <param name="Scope">この口が要るスコープ（<see cref="AdminApiTokenScopes.Allowed"/> のどれか）。</param>
public sealed record AutomationAccessMetadata(string Scope);

/// <summary>
/// 個人トークンでこの口を読んだときも、監査へ残す印（エンドポイントのメタデータ。Issue #1027）。
/// <b>書き出しのように、読み取りでも定義の一括持ち出しになる口だけ</b>に付ける（通常の読み取りは残さない）。
/// </summary>
public sealed record AuditAutomationReadMetadata;

/// <summary>口ごとの許可宣言。</summary>
public static class AutomationEndpointExtensions
{
    /// <summary>
    /// この口を、個人トークンで読んだときも監査へ残す（書き出し用）。ブラウザのセッションの読み取りは今までどおり残さない。
    /// </summary>
    public static TBuilder AuditAutomationReads<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new AuditAutomationReadMetadata()));
        return builder;
    }

    /// <summary>
    /// この口を、個人トークン（自動操作）からも使えるようにする。<b>宣言の無い口は、トークンでは入れない</b>（既定は拒否）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>群へ宣言しない。</b>群の全口が開く（完全削除の口まで）。口ごとに、1 つずつ。
    /// 宣言できるスコープは <see cref="AdminApiTokenScopes.Allowed"/> だけで、外れていれば起動時に落ちる。
    /// </para>
    /// <para>
    /// 口の認可（<c>RequireAuthorization</c>）はそのまま残す。ブラウザのセッションは今までどおりの認可を通る。
    /// 宣言は「トークンの要求を、この口の認可へ通してよい」という許可で、通る条件は
    /// 実効の権限（持ち主の役割 ∩ スコープ）に <paramref name="scope"/> があること。
    /// </para>
    /// </remarks>
    public static TBuilder AllowAutomation<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!AdminApiTokenScopes.IsAllowed(scope))
        {
            throw new ArgumentException($"トークンに付けられないスコープは宣言できない: {scope}", nameof(scope));
        }

        builder.Add(endpoint => endpoint.Metadata.Add(new AutomationAccessMetadata(scope)));
        return builder;
    }
}

/// <summary>
/// 管理 API の認可の要求。<b>ブラウザのセッションは役割の権限で、トークンは口の宣言と実効の権限で判定する。</b>
/// </summary>
/// <param name="Permission">要る権限。<c>null</c> は「トークンでは通さない（セッションだけ）」の印。</param>
public sealed record AdminPermissionRequirement(string? Permission) : IAuthorizationRequirement;

/// <summary><see cref="AdminPermissionRequirement"/> の判定。</summary>
/// <remarks>
/// <para>
/// セッションの principal は、今までと同じ（役割の claim から、その都度、対応表を引く）。
/// </para>
/// <para>
/// トークンの principal は、次をすべて満たしたときだけ通す。
/// </para>
/// <list type="number">
/// <item><description>この口に <see cref="AutomationAccessMetadata"/>（許可宣言）がある。<b>無ければ拒否。</b></description></item>
/// <item><description>宣言のスコープが、実効の権限に入っている（<b>入っていなければ 403</b>）。</description></item>
/// <item><description>
/// 要求された権限が、トークンに付けられるスコープ（許可リスト）に入っている。
/// <c>users.*</c>・<c>settings.*</c>・<c>lookups.manage</c>・<c>surveys.delete</c> などの権限を要求する口は、
/// 宣言を誤って付けても開かない。
/// </description></item>
/// </list>
/// <para>
/// 口が複数の権限を要求するとき（群の「閲覧」と、口の「公開」）、トークンは<b>口が宣言したスコープ</b>で判定する
/// （群の「閲覧」を別に要求しない。<see cref="AdminPermissions"/> は役割が両方持つ前提のため）。
/// </para>
/// </remarks>
public sealed class AdminPermissionAuthorizationHandler : AuthorizationHandler<AdminPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminPermissionRequirement requirement)
    {
        if (!AdminApiTokenAuthentication.IsApiToken(context.User))
        {
            if (requirement.Permission is null
                || AdminPermissions.Has(context.User.FindFirstValue(ClaimTypes.Role), requirement.Permission))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (requirement.Permission is null || !AdminApiTokenScopes.IsAllowed(requirement.Permission))
        {
            return Task.CompletedTask;
        }

        var endpoint = context.Resource switch
        {
            HttpContext http => http.GetEndpoint(),
            Endpoint resource => resource,
            _ => null,
        };

        if (endpoint?.Metadata.GetMetadata<AutomationAccessMetadata>() is { } access
            && AdminApiTokenAuthentication.PermissionsOf(context.User).Contains(access.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
