using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class ChildListTests
{
    [Fact]
    public void ParsesNormalisesAndDeduplicates()
    {
        var (names, invalid) = ChildList.Parse(" JSmith26, asmith29;jsmith26\njsmith26@example.edu.au ");
        Assert.Equal(["jsmith26", "asmith29"], names);
        Assert.Empty(invalid);
    }

    [Fact]
    public void RejectsOddNames()
    {
        var (names, invalid) = ChildList.Parse("ok.name, bad/name, -dash");
        Assert.Equal(["ok.name"], names);
        Assert.Equal(["bad/name", "-dash"], invalid);
    }

    [Fact]
    public void OwnershipNeedsAPinnedPrimaryUser()
    {
        string[] children = ["jsmith26"];
        Assert.True(ChildList.Owns(children, "JSmith26"));
        Assert.False(ChildList.Owns(children, "asmith29"));
        Assert.False(ChildList.Owns(children, null));
    }
}
