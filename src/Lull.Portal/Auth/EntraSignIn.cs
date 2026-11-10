using System.Security.Claims;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Lull.Portal.Auth;

/// <summary>Who Entra ID signed in, read from the ID token.</summary>
public sealed record EntraIdentity(Guid ObjectId, string? Email, string? Name, string Role, IReadOnlyList<string> Students);

/// <summary>
/// Sign-in with Entra ID (OpenID Connect, authorization code with PKCE). The
/// ID token's claims become a portal account (<see cref="UserStore.SignInEntraAsync"/>),
/// and the cookie then carries the same claims as a password sign-in, so
/// everything after sign-in works the same for both kinds of account.
/// </summary>
public static class EntraSignIn
{
    public const string Scheme = "Entra";
    public const string CallbackPath = "/signin-oidc";
    public const string SignedOutCallbackPath = "/signout-callback-oidc";

    /// <summary>Why Entra signed someone in but the portal didn't: <c>/Login?entra=…</c>.</summary>
    public static class Refused
    {
        public const string NoRole = "no-role";
        public const string Conflict = "conflict";
        public const string NoEmail = "no-email";
        public const string Failed = "failed";

        /// <summary>The Login page's message for a code, or null for an unknown one.</summary>
        public static string? Message(string? code) => code switch
        {
            NoRole => "Your Microsoft account isn't set up to use the portal. Ask your school.",
            Conflict => "Your email already belongs to another portal account. Ask your school to sort it out.",
            NoEmail => "Your Microsoft account has no email address, which the portal needs. Ask your school.",
            Failed => "Signing in with Microsoft didn't work. Try again.",
            _ => null,
        };
    }

    /// <summary>
    /// The identity in a validated ID token (claim names as Entra issues them), or
    /// why it can't sign in. Student usernames that aren't valid are left out and returned.
    /// </summary>
    public static (EntraIdentity? Who, string? Refused, IReadOnlyList<string> InvalidStudents) Read(ClaimsPrincipal token, EntraOptions o)
    {
        if (!Guid.TryParse(token.FindFirstValue("oid"), out var oid)) return (null, Refused.Failed, []);
        var roles = token.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        string? role = roles.Contains(o.AdminRole) ? Roles.Admin
            : o.ParentRole.Length == 0 || roles.Contains(o.ParentRole) ? Roles.Parent
            : null;
        if (role is null) return (null, Refused.NoRole, []);
        // email needs the optional claim in the app registration; the UPN is the fallback.
        var email = token.FindFirstValue("email") ?? token.FindFirstValue("preferred_username");
        // One comma-separated value (an extension attribute), or one claim per student.
        var (students, invalid) = ChildList.Parse(string.Join(",", token.FindAll(o.StudentsClaim).Select(c => c.Value)));
        return (new(oid, email, token.FindFirstValue("name"), role, students), null, invalid);
    }

    public static void Configure(OpenIdConnectOptions oidc, EntraOptions o)
    {
        oidc.Authority = o.Authority;
        oidc.ClientId = o.ClientId;
        oidc.ClientSecret = o.ClientSecret;
        oidc.ResponseType = "code";
        oidc.CallbackPath = CallbackPath;
        oidc.SignedOutCallbackPath = SignedOutCallbackPath;
        oidc.SignedOutRedirectUri = "/Login";
        oidc.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }) oidc.Scope.Add(scope);
        // Entra's own claim names (oid, roles, …), not the long WS-* ones.
        oidc.MapInboundClaims = false;
        // Nothing is called on the person's behalf, so no tokens are kept (and the cookie stays small).
        oidc.SaveTokens = false;
        oidc.Events.OnTokenValidated = TokenValidatedAsync;
        oidc.Events.OnRemoteFailure = ctx =>
        {
            // Cancelled, an expired or replayed sign-in, a misconfigured app registration: logged, and a plain message.
            Log(ctx.HttpContext).LogWarning("Entra sign-in failed: {Error}", ctx.Failure?.Message);
            ctx.HandleResponse();
            ctx.Response.Redirect($"/Login?entra={Refused.Failed}");
            return Task.CompletedTask;
        };
    }

    static async Task TokenValidatedAsync(TokenValidatedContext ctx)
    {
        var o = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<PortalOptions>>().Value.Entra;
        var log = Log(ctx.HttpContext);
        var (who, refused, invalid) = Read(ctx.Principal!, o);
        if (who is null)
        {
            log.LogWarning("Entra sign-in refused ({Reason}): object ID {Oid}", refused, ctx.Principal!.FindFirstValue("oid"));
            Refuse(ctx, refused!);
            return;
        }
        if (invalid.Count > 0)
            log.LogWarning("Entra sign-in for {Email}: ignored student usernames that aren't valid: {Invalid}", who.Email, string.Join(", ", invalid));

        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var link = (await ctx.HttpContext.RequestServices.GetRequiredService<PortalSettings>().GetAsync(ctx.HttpContext.RequestAborted)).LinkLocalAccountsByEmail;
        var result = await users.SignInEntraAsync(who, link, ctx.HttpContext.RequestAborted);
        if (result.Outcome != EntraSignInOutcome.Ok)
        {
            log.LogWarning("Entra sign-in refused ({Outcome}): {Email}, object ID {Oid}", result.Outcome, who.Email, who.ObjectId);
            Refuse(ctx, result.Outcome == EntraSignInOutcome.Conflict ? Refused.Conflict : Refused.NoEmail);
            return;
        }
        ctx.Principal = AccountClaims.For(result.User!);
        // A fixed lifetime, not sliding: role and students are refreshed from Entra at
        // least that often, and someone disabled in Entra is signed out by then.
        ctx.Properties!.AllowRefresh = false;
        log.LogInformation("{Email} signed in with Entra ({Role})", result.User!.Email, result.User.Role);
    }

    static void Refuse(TokenValidatedContext ctx, string code)
    {
        ctx.HandleResponse();
        ctx.Response.Redirect($"/Login?entra={code}");
    }

    static ILogger Log(HttpContext http) => http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("EntraSignIn");
}
