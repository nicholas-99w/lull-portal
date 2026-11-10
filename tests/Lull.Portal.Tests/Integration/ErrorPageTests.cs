using System.Net;
using System.Net.Http.Json;
using Lull.Portal.Api;
using Npgsql;

namespace Lull.Portal.Tests.Integration;

/// <summary>Its own Production portal: these tests break its database on purpose.</summary>
public sealed class ErrorPagePortalFactory : ProductionPortalFactory;

public class ErrorPageTests(ErrorPagePortalFactory portal) : IClassFixture<ErrorPagePortalFactory>
{
    /// <summary>Runs <paramref name="body"/> with the audit log missing, so anything that audits fails.</summary>
    async Task WithoutAuditLogAsync(Func<Task> body)
    {
        async Task RunAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(portal.ConnectionString);
            await conn.OpenAsync();
            await new NpgsqlCommand(sql, conn).ExecuteNonQueryAsync();
        }
        await RunAsync("ALTER TABLE audit_log RENAME TO audit_log_hidden");
        try
        {
            await body();
        }
        finally
        {
            await RunAsync("ALTER TABLE audit_log_hidden RENAME TO audit_log");
        }
    }

    [Fact]
    public async Task AMissingPageGetsTheNotFoundPage()
    {
        var res = await portal.Browser().GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("Page not found", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheApiKeepsPlainStatusCodes()
    {
        var res = await portal.CreateClient().GetAsync("/api/device/v1/no-such-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("<html", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ErrorsOnPagesAndFormsShowTheErrorPage()
    {
        var admin = await portal.SignInAdminAsync(); // signing in is audited, so before breaking it
        await WithoutAuditLogAsync(async () =>
        {
            var page = await admin.GetAsync("/Admin/Audit");
            Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("Something went wrong", html);
            Assert.Contains("quote reference", html);
            Assert.DoesNotContain("audit_log", html); // no internals

            // A form post (creating a user is audited) is handled the same way.
            var post = await PortalFactory.PostFormAsync(admin, "/Admin/Users/Edit",
                new() { ["name"] = "Error Test", ["email"] = "error.test@parents.test", ["role"] = "Parent", ["students"] = "etest26" });
            Assert.Equal(HttpStatusCode.InternalServerError, post.StatusCode);
            Assert.Contains("Something went wrong", await post.Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task ErrorsInTheApiAreProblemDetails() => await WithoutAuditLogAsync(async () =>
    {
        // Registering is audited.
        var res = await portal.CreateClient().PostAsJsonAsync("/api/device/v1/register",
            new RegisterRequest("C02ERRORS001", "HW-1", PortalFactory.Secret, "0.2.0"));
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("audit_log", await res.Content.ReadAsStringAsync());
    });
}
