using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class DisplayDateTests
{
    // 2 am on the 6th in Sydney: shown in UTC whatever the offset it arrives with.
    static readonly DateTimeOffset Sydney = new(2026, 11, 6, 2, 5, 9, TimeSpan.FromHours(11));

    [Fact]
    public void WritesDatesAndMomentsTheSameWayEverywhere()
    {
        Assert.Equal("5 Nov 2026", DisplayDate.Date(Sydney));
        Assert.Equal("5 Nov 2026, 15:05 UTC", DisplayDate.DateTime(Sydney));
        Assert.Equal("5 Nov 2026, 15:05:09 UTC", DisplayDate.DateTimeWithSeconds(Sydney));
        Assert.Equal("2026-11-05T15:05:09Z", DisplayDate.Iso(Sydney));
    }
}
