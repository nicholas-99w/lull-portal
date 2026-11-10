namespace Lull.Portal.Domain;

/// <summary>
/// How people sign in, and how long the audit log is kept. Their first values come from the
/// deployment's .env (LULL_LOCAL_SIGN_IN, LULL_ENTRA_LINK_LOCAL_ACCOUNTS, LULL_AUDIT_RETENTION_DAYS);
/// once the owner saves them in Settings, the saved values replace those.
/// </summary>
public sealed record AccountSettings(bool LocalSignIn, bool LinkLocalAccountsByEmail, int AuditRetentionDays)
{
    /// <summary>About 100 years: long enough for "keep everything", short enough to stay a date.</summary>
    public const int MaxAuditRetentionDays = 36500;

    /// <summary>What's wrong with these settings, as messages for the owner; empty if they're fine.</summary>
    public IReadOnlyList<string> Check(bool entraEnabled)
    {
        var errors = new List<string>();
        if (!LocalSignIn && !entraEnabled)
            errors.Add("Local sign-in can only be turned off once Sign in with Microsoft is set up: otherwise nobody but you could sign in.");
        if (LinkLocalAccountsByEmail && !entraEnabled)
            errors.Add("Taking over local accounts needs Sign in with Microsoft to be set up.");
        if (AuditRetentionDays is < 0 or > MaxAuditRetentionDays)
            errors.Add($"Keep audit log entries for 0 (forever) to {MaxAuditRetentionDays:N0} days.");
        return errors;
    }

    /// <summary>
    /// As they can take effect: without Microsoft sign-in, local sign-in stays on (or nobody but
    /// the owner could sign in) and there's nothing to link.
    /// </summary>
    public AccountSettings Effective(bool entraEnabled) =>
        entraEnabled ? this : this with { LocalSignIn = true, LinkLocalAccountsByEmail = false };
}
