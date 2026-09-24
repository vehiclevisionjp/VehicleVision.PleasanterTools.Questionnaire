using System.Net;
using System.Text;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>決まった設定を返すだけの <see cref="IPleasanterSsoOptionsProvider"/>。</summary>
internal sealed class StaticPleasanterSsoOptionsProvider(PleasanterSsoOptions options)
    : IPleasanterSsoOptionsProvider
{
    public PleasanterSsoOptions Options { get; set; } = options;

    public Task<PleasanterSsoOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PleasanterSsoOptionsSnapshot(
            Options,
            new PleasanterSsoSettingValues(),
            new HashSet<string>()));

    public Task<PleasanterSsoOptionsSnapshot> PreviewAsync(
        PleasanterSsoSettingValues values,
        bool forceEnabled,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PleasanterSsoOptionsSnapshot> SaveAsync(
        PleasanterSsoSettingValues values,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>決まった答えを返し、渡された cookie を覚える <see cref="IPleasanterSessionVerifier"/>。</summary>
internal sealed class FakePleasanterSessionVerifier(PleasanterSessionResult result) : IPleasanterSessionVerifier
{
    public PleasanterSessionResult Result { get; set; } = result;

    public int Calls { get; private set; }

    public StringValues LastCookies { get; private set; }

    public Task<PleasanterSessionResult> VerifyAsync(
        StringValues cookieHeaders,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastCookies = cookieHeaders;
        return Task.FromResult(Result);
    }
}

/// <summary>決まった応答を返し、受け取った要求を覚える HTTP ハンドラ。</summary>
internal sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public static FakeHttpMessageHandler Json(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return await respond(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>1 つのハンドラを包んだ HttpClient を返すだけの工場。</summary>
internal sealed class SingleHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
