using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>取説へ載せる「DB から直に直す」手順が本当に通るかを確かめる。</summary>
/// <remarks>
/// ⚠️ **手順書の PowerShell が作る文字列を、実物の照合器へ通す。**
/// 合っていなければ、締め出された人が二度と入れない手順を配ることになる。
/// </remarks>
public class PasswordResetProcedureTests
{
    [Fact]
    public void 手順書が作る形の文字列で照合が通る()
    {
        // scripts/New-AdminPasswordHash.ps1 が出した実物
        const string password = "Sample-Reset-Pass-1";
        const string stored =
            "v1$210000$x7WKywwHgWfk0CKEsn16ZQ==$vCzawc0gaupdem6h7jULGuXuSvkK6xAxYcn4oEl0fSY=";

        var (verified, needsRehash) = new PasswordHasher().Verify(password, stored);

        Assert.True(verified);
        // **今の反復回数で作っているので、作り直しは要らない**
        Assert.False(needsRehash);
    }
}
