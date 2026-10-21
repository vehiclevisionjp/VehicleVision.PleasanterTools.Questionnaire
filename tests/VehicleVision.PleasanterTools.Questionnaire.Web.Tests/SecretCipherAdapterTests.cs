using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 管理者の共有鍵の暗号化（Core.Data の <see cref="ISecretCipher"/> に合わせたアダプタ）と、
/// 管理者セッションまわりの小さな部品。
/// </summary>
public class SecretCipherAdapterTests
{
    private static readonly string Context = $"{QuestionnaireDatabase.Instance.AppId}:totp:{Guid.NewGuid():D}";

    private static SecretProtector NewProtector() => new(SecretProtector.GenerateKey());

    [Fact]
    public void 旧の文脈を持たない暗号文を新しい文脈の窓口でそのまま読める()
    {
        // 旧の AdminAuthenticator は protector.Protect(secret)（文脈なし）で暗号化していた
        var protector = NewProtector();
        var legacy = protector.Protect("JBSWY3DPEHPK3PXP");

        var cipher = new SecretCipherAdapter(protector);

        Assert.Equal("JBSWY3DPEHPK3PXP", cipher.Unprotect(legacy, Context));
        Assert.Equal("JBSWY3DPEHPK3PXP", cipher.Unprotect(legacy, "別の文脈"));
    }

    [Fact]
    public void 書き込みは文脈に束縛し別の文脈では読めない()
    {
        var protector = NewProtector();
        var cipher = new SecretCipherAdapter(protector);

        var written = cipher.Protect("SECRET", Context);

        Assert.NotEqual("SECRET", written);
        Assert.Equal("SECRET", cipher.Unprotect(written, Context));
        Assert.Throws<CryptographicException>(
            () => cipher.Unprotect(written, $"{QuestionnaireDatabase.Instance.AppId}:totp:{Guid.NewGuid():D}"));

        // 文脈を持たない読み方では、束縛した暗号文を読まない
        Assert.Null(protector.Unprotect(written));
    }

    [Fact]
    public void 鍵が違えば旧の暗号文も読めない()
    {
        var legacy = NewProtector().Protect("SECRET");

        Assert.Throws<CryptographicException>(() => new SecretCipherAdapter(NewProtector()).Unprotect(legacy, Context));
    }

    [Fact]
    public void 文脈を持たない暗号化の書式は変えていない()
    {
        // 旧の書式（v1）のまま。ほかの用途（設定の秘密・メールの本文など）の暗号文を読めなくしない
        var protector = NewProtector();

        Assert.StartsWith("v1$", protector.Protect("x"), StringComparison.Ordinal);
        Assert.StartsWith("v2$", protector.Protect("x", Context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task セッションの一括失効は消した端末の識別子を返し残す1つは消さない()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T00:00:00Z"));
        var store = new FakeAdminSessionStore(time);
        var userId = Guid.NewGuid();
        var keep = await store.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        var other = await store.CreateAsync(userId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        var pending = await store.CreateAsync(userId, AdminSessionKinds.Pending, TimeSpan.FromMinutes(5));
        var stranger = await store.CreateAsync(Guid.NewGuid(), AdminSessionKinds.Session, TimeSpan.FromHours(8));

        var revoked = await store.RevokeAllExceptAsync(userId, keep);

        // 返すのは、ログイン中の端末のうち残す 1 つ以外（途中状態は消すが数えない）
        Assert.Equal([store.HandleOf(other)], revoked);
        Assert.NotNull(await store.FindAsync(keep));
        Assert.Null(await store.FindAsync(other));
        Assert.Null(await store.FindAsync(pending));
        Assert.NotNull(await store.FindAsync(stranger));

        // 残す 1 つを指定しなければ、すべて消える
        Assert.Equal([store.HandleOf(keep)], await store.RevokeAllExceptAsync(userId, exceptSessionId: null));
        Assert.Null(await store.FindAsync(keep));
    }

    [Fact]
    public void 途中状態の印はSAMLとPleasanterの両方を引き継ぐ()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(AdminAuthSchemes.SamlNameIdClaim, "name-id"),
                new Claim(AdminAuthSchemes.SamlSessionIndexClaim, "idx"),
                new Claim(PleasanterSsoClaims.TenantId, "1"),
                new Claim(PleasanterSsoClaims.UserId, "7"),
                new Claim(PleasanterSsoClaims.VerifiedAt, "1790000000"),
            ],
            "test");

        var sso = AdminAuthEndpoints.SsoOf(new ClaimsPrincipal(identity));

        Assert.Equal(new SamlSessionKeys("name-id", "idx"), sso!.Saml);
        Assert.Equal(new PleasanterSsoKeys(1, 7, DateTimeOffset.FromUnixTimeSeconds(1790000000)), sso.Pleasanter);

        // パスワードで来たなら、印は無い
        Assert.Null(AdminAuthEndpoints.SsoOf(new ClaimsPrincipal(new ClaimsIdentity([], "test"))));
        Assert.Null(AdminAuthEndpoints.SsoOf(null));
    }
}
