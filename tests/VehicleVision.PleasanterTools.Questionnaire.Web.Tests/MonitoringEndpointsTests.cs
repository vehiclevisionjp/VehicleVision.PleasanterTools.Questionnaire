using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class MonitoringEndpointsTests
{
    [Fact]
    public void 正しいBearerトークンだけを受け入れる()
    {
        var token = new MonitoringToken("correct-token");
        var context = new DefaultHttpContext();

        context.Request.Headers.Authorization = "Bearer wrong-token";
        Assert.False(token.IsAuthorized(context.Request));

        context.Request.Headers.Authorization = "Bearer correct-token";
        Assert.True(token.IsAuthorized(context.Request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic correct-token")]
    [InlineData("Bearer")]
    public void Bearerトークンでなければ拒否する(string? authorization)
    {
        var token = new MonitoringToken("correct-token");
        var context = new DefaultHttpContext();
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        Assert.False(token.IsAuthorized(context.Request));
    }

    [Fact]
    public void 滞留時刻を経過秒へ変換して未来の時刻は0秒にする()
    {
        var now = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            90,
            MonitoringService.AgeSeconds(now, new DateTime(2026, 9, 16, 8, 58, 30)));
        Assert.Equal(
            0,
            MonitoringService.AgeSeconds(now, new DateTime(2026, 9, 16, 9, 0, 1)));
        Assert.Null(MonitoringService.AgeSeconds(now, null));
    }
}
