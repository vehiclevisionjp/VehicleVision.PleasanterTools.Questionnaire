using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 認可の判定（Issue #1026）。ブラウザのセッションは今までどおり役割の権限、トークンは口の許可宣言と実効の権限。
/// <b>宣言の無い口・トークンに付けられない権限を要求する口は、トークンでは通さない。</b>
/// </summary>
public class AdminPermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal Session(string role) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
            "Admin.Session"));

    private static ClaimsPrincipal Token(params string[] permissions) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(AdminApiTokenAuthentication.AuthKindClaim, AdminApiTokenAuthentication.AuthKindApiToken),
            }.Concat(permissions.Select(p => new Claim(AdminApiTokenAuthentication.PermissionClaim, p))),
            "Admin.ApiToken"));

    private static Endpoint EndpointOf(string? declaredScope) =>
        new(
            _ => Task.CompletedTask,
            declaredScope is null
                ? new EndpointMetadataCollection()
                : new EndpointMetadataCollection(new AutomationAccessMetadata(declaredScope)),
            "test");

    private static async Task<bool> EvaluateAsync(ClaimsPrincipal user, string? permission, Endpoint endpoint)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(endpoint);
        var requirement = new AdminPermissionRequirement(permission);
        var context = new AuthorizationHandlerContext([requirement], user, http);
        await new AdminPermissionAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task セッションは今までどおり_役割の権限で判定する_宣言は関係しない()
    {
        Assert.True(await EvaluateAsync(Session("Administrator"), "users.write", EndpointOf(null)));
        Assert.True(await EvaluateAsync(Session("Editor"), "surveys.write", EndpointOf(null)));
        Assert.False(await EvaluateAsync(Session("Editor"), "surveys.publish", EndpointOf("surveys.publish")));
        Assert.True(await EvaluateAsync(Session("Editor"), null, EndpointOf(null)));
    }

    [Fact]
    public async Task トークンは_宣言の無い口には入れない()
    {
        var token = Token("surveys.read", "surveys.write", "surveys.publish");

        Assert.False(await EvaluateAsync(token, "surveys.read", EndpointOf(null)));
        Assert.False(await EvaluateAsync(token, "surveys.write", EndpointOf(null)));
    }

    [Fact]
    public async Task トークンは_宣言のスコープを実効の権限に持つときだけ通る()
    {
        Assert.True(await EvaluateAsync(Token("surveys.write"), "surveys.write", EndpointOf("surveys.write")));
        Assert.False(await EvaluateAsync(Token("surveys.read"), "surveys.write", EndpointOf("surveys.write")));
        Assert.False(await EvaluateAsync(Token(), "surveys.read", EndpointOf("surveys.read")));
    }

    [Fact]
    public async Task 群の閲覧は_口が宣言したスコープで判定する()
    {
        // 公開の口は、群の surveys.read と口の surveys.publish の両方を要求する。公開だけのトークンが通る
        var token = Token("surveys.publish");
        var endpoint = EndpointOf("surveys.publish");

        Assert.True(await EvaluateAsync(token, "surveys.read", endpoint));
        Assert.True(await EvaluateAsync(token, "surveys.publish", endpoint));
    }

    [Theory]
    [InlineData("users.read")]
    [InlineData("users.write")]
    [InlineData("users.resetTwoFactor")]
    [InlineData("settings.saml")]
    [InlineData("settings.manage")]
    [InlineData("lookups.manage")]
    [InlineData("surveys.delete")]
    [InlineData("surveys.deleteTestResponses")]
    [InlineData("audit.read")]
    [InlineData("maintenance.manage")]
    public async Task トークンに付けられない権限を要求する口は_宣言を誤って付けても通さない(string permission)
    {
        // 持ち主の役割が持つ権限でも、スコープの許可リストに無い権限は、実効の権限へ載らない。載っていても通さない
        var token = Token("surveys.read", "surveys.write", "surveys.publish", permission);

        Assert.False(await EvaluateAsync(token, permission, EndpointOf("surveys.read")));
    }

    [Fact]
    public async Task セッション専用の印_Permission_nullは_トークンを通さない()
    {
        Assert.False(await EvaluateAsync(
            Token("surveys.read", "surveys.write", "surveys.publish"), null, EndpointOf("surveys.read")));
    }

    [Fact]
    public async Task 役割の_claim_を持たないトークンは_役割で通る道に入らない()
    {
        // Administrator の役割を名乗る claim を足しても、トークンの判定は実効の権限だけ
        var identity = new ClaimsIdentity(
            [
                new Claim(AdminApiTokenAuthentication.AuthKindClaim, AdminApiTokenAuthentication.AuthKindApiToken),
                new Claim(ClaimTypes.Role, "Administrator"),
            ],
            "Admin.ApiToken");

        Assert.False(await EvaluateAsync(new ClaimsPrincipal(identity), "users.write", EndpointOf("surveys.read")));
    }

    [Fact]
    public void Bearerを使う要求は_管理APIで_Authorizationヘッダがあるものだけ()
    {
        static HttpRequest Request(string path, string? authorization)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            if (authorization is not null)
            {
                http.Request.Headers.Authorization = authorization;
            }

            return http.Request;
        }

        Assert.True(AdminApiTokenAuthentication.UsesBearer(Request("/api/admin/surveys", "Bearer qat_x")));
        Assert.True(AdminApiTokenAuthentication.UsesBearer(Request("/API/ADMIN/surveys", "bearer qat_x")));
        Assert.True(AdminApiTokenAuthentication.UsesBearer(Request("/api/admin/surveys", "Bearer")));
        Assert.False(AdminApiTokenAuthentication.UsesBearer(Request("/api/admin/surveys", null)));
        Assert.False(AdminApiTokenAuthentication.UsesBearer(Request("/api/admin/surveys", "Basic abc")));
        Assert.False(AdminApiTokenAuthentication.UsesBearer(Request("/api/admin/surveys", "Bearerx qat_x")));
        // **公開の回答画面の要求には持ち込まない**
        Assert.False(AdminApiTokenAuthentication.UsesBearer(Request("/api/forms/abc", "Bearer qat_x")));
        Assert.False(AdminApiTokenAuthentication.UsesBearer(Request("/api/administrators", "Bearer qat_x")));
    }
}
