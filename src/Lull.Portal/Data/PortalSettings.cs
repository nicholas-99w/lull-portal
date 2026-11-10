using Lull.Portal.Domain;
using Microsoft.Extensions.Options;

namespace Lull.Portal.Data;

/// <summary>
/// The sign-in and audit log settings in force: the owner's from Settings once saved,
/// otherwise the deployment's (.env), as they can take effect (<see cref="AccountSettings.Effective"/>).
/// </summary>
public sealed class PortalSettings(PortalStore store, IOptions<PortalOptions> options)
{
    public bool EntraEnabled => options.Value.Entra.Enabled;

    /// <summary>The .env's values, which apply until the owner saves their own.</summary>
    public AccountSettings FromEnvironment => new(
        options.Value.LocalSignIn, options.Value.Entra.LinkLocalAccountsByEmail, options.Value.AuditRetentionDays);

    /// <summary>The settings as configured (saved, or the .env's), and whether they were saved in the portal.</summary>
    public async Task<(AccountSettings Settings, bool Saved)> GetConfiguredAsync(CancellationToken ct) =>
        await store.GetAccountSettingsAsync(ct) is { } saved ? (saved, true) : (FromEnvironment, false);

    public async Task<AccountSettings> GetAsync(CancellationToken ct) =>
        (await GetConfiguredAsync(ct)).Settings.Effective(EntraEnabled);
}
