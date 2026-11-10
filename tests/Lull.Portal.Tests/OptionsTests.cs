using Npgsql;

namespace Lull.Portal.Tests;

public class OptionsTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("has;semicolons;and=equals")]
    [InlineData("quotes\"and'apostrophes")]
    [InlineData("  spaces at the ends  ")]
    [InlineData("$dollar%percent\\backslash")]
    public void AnyPasswordSurvivesTheConnectionString(string password)
    {
        var o = new PortalOptions { ConnectionString = "Host=db;Database=lull;Username=lull", DatabasePassword = password };
        var parsed = new NpgsqlConnectionStringBuilder(o.EffectiveConnectionString());
        Assert.Equal(password, parsed.Password);
        Assert.Equal("db", parsed.Host);
    }

    [Fact]
    public void ABrokenConnectionStringGivesAClearMessageWithoutThePassword()
    {
        var o = new PortalOptions { ConnectionString = "Host=db;Database=lull;Username=lull;Password=abc;de\"f" };
        var e = Assert.Throws<InvalidOperationException>(() => o.EffectiveConnectionString());
        Assert.Contains("Lull__DatabasePassword", e.Message);
        Assert.DoesNotContain("abc", e.Message);
    }

    [Fact]
    public void GssEncryptionIsOffUnlessAskedFor()
    {
        var o = new PortalOptions { ConnectionString = "Host=db;Username=lull" };
        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(o.EffectiveConnectionString()).GssEncryptionMode);
        o.ConnectionString = "Host=db;Username=lull;GSS Encryption Mode=Prefer";
        Assert.Equal(GssEncryptionMode.Prefer, new NpgsqlConnectionStringBuilder(o.EffectiveConnectionString()).GssEncryptionMode);
    }

    [Fact]
    public void APasswordInTheConnectionStringStillWorks()
    {
        var o = new PortalOptions { ConnectionString = "Host=db;Username=lull;Password=plain" };
        Assert.Equal("plain", new NpgsqlConnectionStringBuilder(o.EffectiveConnectionString()).Password);
    }

    [Fact]
    public void EntraIsOffUnlessConfiguredAndMustBeComplete()
    {
        Assert.Null(new PortalOptions().Problem());
        Assert.False(new PortalOptions().Entra.Enabled);
        var o = new PortalOptions { Entra = { TenantId = Guid.NewGuid().ToString() } };
        Assert.True(o.Entra.Enabled);
        Assert.Contains("ClientId", o.Problem());
        o.Entra.ClientId = "not-a-guid";
        Assert.Contains("ClientId", o.Problem());
        o.Entra.ClientId = Guid.NewGuid().ToString();
        Assert.Contains("ClientSecret", o.Problem());
        o.Entra.ClientSecret = "secret";
        Assert.Null(o.Problem());
        o.Entra.TenantId = "school.example"; // a domain, not the tenant ID
        Assert.Contains("TenantId", o.Problem());
    }

    [Fact]
    public void LocalSignInCanOnlyBeOffWithEntra()
    {
        var o = new PortalOptions { LocalSignIn = false };
        Assert.Contains("LocalSignIn", o.Problem());
        o.Entra = new() { TenantId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString(), ClientSecret = "secret" };
        Assert.Null(o.Problem());
    }

    [Fact]
    public void ForwardedHeadersAreTrustedFromPrivateAddressesUnlessProxiesAreNamed()
    {
        var networks = new PortalOptions().TrustedProxyNetworks();
        Assert.Contains(networks, n => n.Contains(System.Net.IPAddress.Parse("172.18.0.1"))); // Docker's bridge
        Assert.Contains(networks, n => n.Contains(System.Net.IPAddress.Loopback));
        Assert.DoesNotContain(networks, n => n.Contains(System.Net.IPAddress.Parse("203.0.113.9"))); // the internet

        var named = new PortalOptions { TrustedProxies = "10.0.0.5, 2001:db8::/32" }.TrustedProxyNetworks();
        Assert.Equal(2, named.Count);
        Assert.Contains(named, n => n.Contains(System.Net.IPAddress.Parse("10.0.0.5")));
        Assert.DoesNotContain(named, n => n.Contains(System.Net.IPAddress.Parse("10.0.0.6")));
        Assert.Contains(named, n => n.Contains(System.Net.IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void ABadTrustedProxyIsAStartupProblem()
    {
        Assert.Contains("Lull__TrustedProxies", new PortalOptions { TrustedProxies = "10.0.0.0/8, proxy.local" }.Problem());
        Assert.Null(new PortalOptions { TrustedProxies = "10.0.0.0/8" }.Problem());
    }
}
