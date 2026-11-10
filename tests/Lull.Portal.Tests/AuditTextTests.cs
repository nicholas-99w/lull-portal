using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class AuditTextTests
{
    [Theory]
    [InlineData("lock_now", "Locked down")]
    [InlineData("register_refused", "Registration refused")]
    [InlineData("something_new", "Something new")] // a new action reads well enough before it's listed
    public void ActionsReadAsWords(string action, string words) => Assert.Equal(words, AuditText.Action(action));

    [Theory]
    [InlineData("device", "The Mac")]
    [InlineData("system", "The portal")]
    [InlineData("it@school.test", "it@school.test")]
    public void TheActorIsAPersonOrSays(string actor, string words) => Assert.Equal(words, AuditText.Actor(actor));

    [Fact]
    public void PlainDetailsReadAsLines()
    {
        Assert.Equal(
            ["Lock until: 5 Nov 2026, 10:32 UTC", "Username: pupil060", "Enabled: yes", "Students: a1, a2"],
            AuditText.Details("""{"revision":1793872921046,"lockUntil":"2026-11-05T10:32:01+00:00","username":"pupil060","enabled":true,"students":["a1","a2"],"nested":{"x":1},"empty":null}"""));
    }

    [Fact]
    public void AHolidaysDatesAreSaidOnce() => Assert.Equal(
        ["Name: Term 1 break", "Dates: Fri 2 Apr – Sun 18 Apr 2027"],
        AuditText.Details("""{"last":"2027-04-18","name":"Term 1 break","dates":"Fri 2 Apr – Sun 18 Apr 2027","first":"2027-04-02"}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void AnythingElseHasNoLines(string? json) => Assert.Empty(AuditText.Details(json));
}
