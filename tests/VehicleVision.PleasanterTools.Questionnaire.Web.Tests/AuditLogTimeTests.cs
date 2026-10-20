using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 管理操作の記録の時刻の目盛りが、旧と同じであること
/// （旧表から写した行と新しい行で、画面の時刻が食い違わないように）。
/// </summary>
public class AuditLogTimeTests
{
    [Fact]
    public void ローカル時刻の数字を動かさずに記録へ渡す()
    {
        // 種別の無い値（DB から読んだ形）も、ローカルの種別の値も、数字は動かさない
        var wall = new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Local);

        Assert.Equal(wall, AuditLogTime.ToStored(wall).UtcDateTime);
        Assert.Equal(local, AuditLogTime.ToStored(local).UtcDateTime);
        Assert.Equal(TimeSpan.Zero, AuditLogTime.ToStored(local).Offset);
    }

    [Fact]
    public void 未指定の絞り込みは未指定のまま()
    {
        Assert.Null(AuditLogTime.ToStored((DateTime?)null));
        Assert.NotNull(AuditLogTime.ToStored((DateTime?)DateTime.UnixEpoch));
    }

    [Fact]
    public void 読んだ時刻は時間帯の印の無い日時へ戻す()
    {
        var stored = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

        var wall = AuditLogTime.ToWallClock(stored);

        Assert.Equal(DateTimeKind.Unspecified, wall.Kind);
        Assert.Equal(new DateTime(2026, 10, 20, 9, 30, 0), wall);
    }

    [Fact]
    public void 応答の日時は旧と同じく時間帯の印を付けない()
    {
        // **応答の形は変えない。** 旧は列の値（時間帯の印の無い日時）をそのまま JSON にしていた
        var json = JsonSerializer.Serialize(new
        {
            occurredAt = AuditLogTime.ToWallClock(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero)),
        });

        Assert.Equal("{\"occurredAt\":\"2026-10-20T09:30:00\"}", json);
    }
}
