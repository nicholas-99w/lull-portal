using System.Net;

namespace Lull.Portal.Tests.Integration;

/// <summary>The header and sidebar around every page.</summary>
public class NavigationTests(PortalFactory portal) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task EveryPageCanSkipToItsContent()
    {
        var signIn = await portal.Browser().GetStringAsync("/Login");
        Assert.Contains("href=\"#main\"", signIn);
        Assert.Contains("<main id=\"main\"", signIn);
        Assert.DoesNotContain("account-menu", signIn); // nothing to sign out of yet
    }

    [Fact]
    public async Task ParentsGetTheirAccountMenuAndNoAdminNavigation()
    {
        var parent = await portal.ParentAsync("navparent", "navkid01");
        var page = await parent.GetStringAsync("/");
        Assert.Contains("account-menu", page);
        Assert.Contains("href=\"/Account\"", page);
        Assert.Contains("action=\"/Logout\"", page);
        Assert.DoesNotContain("side-nav", page);
        Assert.DoesNotContain("/Admin/", page);
    }

    [Theory]
    [InlineData("/Admin/Devices", "/Admin/Devices", "page")]
    [InlineData("/Admin/Users/Edit", "/Admin/Users", "true")]
    [InlineData("/Admin/Settings", "/Admin/Settings", "page")]
    [InlineData("/Admin/Settings/Holidays", "/Admin/Settings", "true")]
    [InlineData("/Admin/Audit", "/Admin/Audit", "page")]
    public async Task MarksWhereYouAre(string path, string section, string current)
    {
        var admin = await portal.SignInAdminAsync();
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(path));
        Assert.Contains($"<a href=\"{section}\" aria-current=\"{current}\">", page);
        Assert.Equal(1, page.Split("side-nav\"")[1].Split("</nav>")[0].Split("aria-current").Length - 1);
    }

    [Fact]
    public async Task OneMacCountsAsWithinDevices()
    {
        var (mac, id) = await portal.RegisterMacAsync("C02NAV0001");
        await PortalFactory.CheckInAsync(mac, "navpupil", null);
        var admin = await portal.SignInAdminAsync();
        Assert.Contains("<a href=\"/Admin/Devices\" aria-current=\"true\">", await admin.GetStringAsync($"/Device/{id}"));
    }
}
