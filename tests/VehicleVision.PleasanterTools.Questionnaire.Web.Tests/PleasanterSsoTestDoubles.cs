using Microsoft.Extensions.Logging;
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
        IPleasanterSsoVerificationOptions options,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastCookies = cookieHeaders;
        return Task.FromResult(Result);
    }
}

/// <summary>書かれたログを文字列で覚えるロガー。**秘密がログへ出ないことを確かめる。**</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Messages.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
