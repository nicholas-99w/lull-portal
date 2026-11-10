using Lull.Portal.Domain;
using Lull.Portal.Pages;

namespace Lull.Portal.Tests;

public class PagingTests
{
    [Fact]
    public void ClampsRequestedPagesToThoseThatExist()
    {
        Assert.Equal(1, Paging.Clamp(null, 0));
        Assert.Equal(1, Paging.Clamp(5, 0));
        Assert.Equal(1, Paging.Clamp(-3, 120));
        Assert.Equal(2, Paging.Clamp(2, 120));
        Assert.Equal(3, Paging.Clamp(99, 120));
        Assert.Equal(2, Paging.PageCount(100));
        Assert.Equal(3, Paging.PageCount(101));
    }

    [Fact]
    public void ShowsTheRangeOfRowsOnThePage()
    {
        var last = new Paged<int>([.. Enumerable.Range(0, 20)], 3, 120);
        Assert.Equal((101, 120, 3), (last.From, last.To, last.Pages));
        var none = new Paged<int>([], 1, 0);
        Assert.Equal((0, 0, 1), (none.From, none.To, none.Pages));
    }

    [Fact]
    public void LinksKeepTheSearch()
    {
        var page = new Paged<int>([1], 1, 1);
        Assert.Equal("/Admin/Users?p=2", Pager.For(page, "/Admin/Users", null, "users").Link(2));
        Assert.Equal("/Admin/Users?q=a%26b%20c&p=2", Pager.For(page, "/Admin/Users", "a&b c", "users").Link(2));
    }
}
