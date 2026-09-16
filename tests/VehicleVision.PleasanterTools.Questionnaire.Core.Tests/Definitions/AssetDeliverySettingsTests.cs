using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

public class AssetDeliverySettingsTests
{
    private static readonly DateTime AnsweredAt =
        new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 回答からの日数は受付終了で切り詰めない()
    {
        var settings = new AssetDeliverySettings
        {
            Expiration = AssetTicketExpiration.DaysAfterResponse,
            Days = 30,
        };

        Assert.Equal(
            AnsweredAt.AddDays(30),
            settings.ExpiresAt(AnsweredAt, AnsweredAt.AddDays(1)));
    }

    [Fact]
    public void 受付終了が無ければ既定日数へ倒す()
    {
        var settings = new AssetDeliverySettings
        {
            Expiration = AssetTicketExpiration.AcceptTo,
        };

        Assert.Equal(
            AnsweredAt.AddDays(AssetDeliverySettings.DefaultDays),
            settings.ExpiresAt(AnsweredAt, null));
    }

    [Fact]
    public void 受付期間に合わせると受付終了を使う()
    {
        var acceptTo = AnsweredAt.AddDays(5);
        var settings = new AssetDeliverySettings
        {
            Expiration = AssetTicketExpiration.AcceptTo,
        };

        Assert.Equal(acceptTo, settings.ExpiresAt(AnsweredAt, acceptTo));
    }
}
