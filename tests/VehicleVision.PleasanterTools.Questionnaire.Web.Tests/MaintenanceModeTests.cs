using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class MaintenanceModeTests
{
    private static readonly Guid AdminUserId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task 環境変数で有効ならDBを読まずに停止する()
    {
        var store = new FakeStore { ThrowsOnRead = true };
        var maintenance = new MaintenanceMode(
            store,
            new MaintenanceModeOptions(true, "環境メンテ", "Environment maintenance"));

        Assert.True(await maintenance.IsActiveAsync());

        var status = await maintenance.GetPublicStatusAsync();
        Assert.True(status.IsActive);
        Assert.True(status.EnvironmentEnabled);
        Assert.False(status.DatabaseEnabled);
        Assert.Equal("環境メンテ", status.MessageJa);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task DBで有効にした状態は画面から解除できる()
    {
        var store = new FakeStore();
        var maintenance = new MaintenanceMode(
            store,
            new MaintenanceModeOptions(
                false,
                MaintenanceModeOptions.DefaultMessageJa,
                MaintenanceModeOptions.DefaultMessageEn));

        var enabled = await maintenance.SetDatabaseAsync(
            true,
            "作業中です",
            "Maintenance in progress",
            AdminUserId);
        Assert.True(enabled.IsActive);
        Assert.True(enabled.DatabaseEnabled);

        var disabled = await maintenance.SetDatabaseAsync(
            false,
            enabled.MessageJa,
            enabled.MessageEn,
            AdminUserId);
        Assert.False(disabled.IsActive);
        Assert.False(disabled.DatabaseEnabled);
    }

    [Fact]
    public async Task 環境変数とDBの停止元を別々に返す()
    {
        var store = new FakeStore
        {
            Value = new MaintenanceModeRecord(true, "DB", "Database", DateTime.UtcNow, AdminUserId),
        };
        var maintenance = new MaintenanceMode(
            store,
            new MaintenanceModeOptions(true, "環境", "Environment"));

        var status = await maintenance.GetStatusAsync();

        Assert.True(status.IsActive);
        Assert.True(status.EnvironmentEnabled);
        Assert.True(status.DatabaseEnabled);
        Assert.Equal("環境", status.MessageJa);
    }

    private sealed class FakeStore : IMaintenanceModeStore
    {
        public MaintenanceModeRecord Value { get; set; } =
            new(false, null, null, null, null);

        public bool ThrowsOnRead { get; set; }

        public int ReadCount { get; private set; }

        public Task<MaintenanceModeRecord> GetAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ThrowsOnRead
                ? Task.FromException<MaintenanceModeRecord>(
                    new InvalidOperationException("DBを読んではいけない"))
                : Task.FromResult(Value);
        }

        public Task SetAsync(
            bool enabled,
            string? messageJa,
            string? messageEn,
            Guid adminUserId,
            DateTime changedAt,
            CancellationToken cancellationToken = default)
        {
            Value = new(
                enabled,
                messageJa,
                messageEn,
                enabled ? changedAt : null,
                enabled ? adminUserId : null);
            return Task.CompletedTask;
        }
    }
}
