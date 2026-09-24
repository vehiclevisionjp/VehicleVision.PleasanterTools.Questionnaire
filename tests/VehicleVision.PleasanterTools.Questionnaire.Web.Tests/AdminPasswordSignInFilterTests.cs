using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>合言葉ログイン API の入口で拒否することを確かめる。</summary>
public class AdminPasswordSignInFilterTests
{
    private sealed class StaticSamlProvider(bool enabled) : ISamlOptionsProvider
    {
        public Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SamlOptionsSnapshot(
                new SamlOptions { Enabled = enabled },
                new SamlSettingValues(),
                new HashSet<string>()));

        public Task<SamlOptionsSnapshot> SaveAsync(
            SamlSettingValues values,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task 停止中はログイン処理を呼ばずに拒否する()
    {
        var filter = Filter(samlEnabled: true, passwordSignInEnabled: false);
        var called = false;

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(new DefaultHttpContext()),
            _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        Assert.False(called);
        Assert.Equal(
            StatusCodes.Status404NotFound,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task 既定ではログイン処理を呼ぶ()
    {
        var filter = Filter(samlEnabled: true, passwordSignInEnabled: true);
        var called = false;

        await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(new DefaultHttpContext()),
            _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        Assert.True(called);
    }

    private static AdminPasswordSignInFilter Filter(
        bool samlEnabled,
        bool passwordSignInEnabled)
    {
        var policy = new AdminPasswordSignInPolicy(
            new AdminPasswordSignInOptions(passwordSignInEnabled, RescueToken: null),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<AdminPasswordSignInPolicy>.Instance);
        return new AdminPasswordSignInFilter(new StaticSamlProvider(samlEnabled), policy);
    }
}
