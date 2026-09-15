using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

public class PleasanterDateTimeTests
{
    [Fact]
    public void APIキー保有ユーザのタイムゾーンへ変換して渡す()
    {
        // **Pleasanter は API の境界でキー保有ユーザの TimeZone との間で変換する**
        // （_documents/実機検証結果.md 4 章）
        var jst = new PleasanterDateTime("Asia/Tokyo");
        var utcNoon = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-03-01T21:00:00", jst.ToPleasanter(utcNoon));
    }

    [Fact]
    public void タイムゾーンが違えば同じ瞬間でも別の文字列になる()
    {
        var utcNoon = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var asJst = new PleasanterDateTime("Asia/Tokyo").ToPleasanter(utcNoon);
        var asUtc = new PleasanterDateTime("UTC").ToPleasanter(utcNoon);

        Assert.Equal("2026-03-01T21:00:00", asJst);
        Assert.Equal("2026-03-01T12:00:00", asUtc);
    }

    [Fact]
    public void Windows形式のタイムゾーンIDも受ける()
    {
        var jst = new PleasanterDateTime("Tokyo Standard Time");
        var utcNoon = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-03-01T21:00:00", jst.ToPleasanter(utcNoon));
    }

    [Fact]
    public void オフセットの無い応答はキー保有ユーザのタイムゾーンとして読む()
    {
        var jst = new PleasanterDateTime("Asia/Tokyo");

        var read = jst.FromPleasanter("2026-03-01T21:00:00");

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), read!.Value.ToUniversalTime());
    }

    [Fact]
    public void 未設定の日付はnullとして読む()
    {
        // **「未回答」と「1899-12-30 と回答」を API 応答だけでは区別できない**
        // （_documents/実機検証結果.md 5 章）
        var jst = new PleasanterDateTime("Asia/Tokyo");

        Assert.Null(jst.FromPleasanter("1899-12-30T00:00:00"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("来週")]
    public void 読めない値はnullを返す(string value)
    {
        var jst = new PleasanterDateTime("Asia/Tokyo");

        Assert.Null(jst.FromPleasanter(value));
    }

    [Fact]
    public void 日付だけの文字列はその日の0時にする()
    {
        Assert.Equal("2026-03-01T00:00:00", PleasanterDateTime.DateOnlyToPleasanter("2026-03-01"));
    }

    [Fact]
    public void 日付として読めなければnullを返す()
    {
        Assert.Null(PleasanterDateTime.DateOnlyToPleasanter("2026-13-45"));
    }
}
