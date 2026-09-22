using System.Net;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

public class PleasanterApiClientConnectionTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task 接続試験は認証拒否だけを失敗として扱う(
        HttpStatusCode statusCode,
        bool expected)
    {
        var handler = new StubHandler(statusCode);
        var client = new PleasanterApiClient(
            new HttpClient(handler),
            new PleasanterOptions
            {
                BaseUrl = "https://pleasanter.example.test",
                ApiKey = "secret",
                ApiVersion = 1.1m,
                ApiKeyUserTimeZoneId = "Asia/Tokyo",
            });

        Assert.Equal(expected, await client.CheckConnectionAsync());
        Assert.Equal(
            "https://pleasanter.example.test/api/users/Get",
            handler.RequestUri?.AbsoluteUri);
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
