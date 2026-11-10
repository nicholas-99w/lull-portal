using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lull.Portal.Domain;

/// <summary>The audit log's entries in words. What's stored stays as it is: the log still shows it on request, and search still finds it.</summary>
public static partial class AuditText
{
    static readonly Dictionary<string, string> Actions = new()
    {
        ["account_locked"] = "Account locked",
        ["account_settings_changed"] = "Sign-in settings changed",
        ["account_unlocked"] = "Account unlocked",
        ["audit_pruned"] = "Old log entries deleted",
        ["bounds_changed"] = "School hours or parent controls changed",
        ["branding_changed"] = "Branding changed",
        ["deregistered"] = "Mac de-registered",
        ["device_reset"] = "Mac reset",
        ["holiday_added"] = "School holiday added",
        ["holiday_removed"] = "School holiday removed",
        ["lock_down_ended"] = "Lock-down ended",
        ["lock_now"] = "Locked down",
        ["overrides_reset"] = "Lock-down, unlock or skip cancelled",
        ["password_changed"] = "Password changed",
        ["password_set"] = "Password set",
        ["portal_set_up"] = "Portal set up",
        ["primary_user_changed"] = "Primary user changed",
        ["primary_user_pinned"] = "Primary user pinned",
        ["re_enrolled"] = "Mac enrolled again",
        ["re_registered"] = "Mac registered again",
        ["re_registered_same_hardware"] = "Mac registered again",
        ["register_refused"] = "Registration refused",
        ["registered"] = "Mac registered",
        ["released"] = "Unlocked",
        ["settings_changed"] = "Schedule changed",
        ["settings_clamped"] = "Schedule trimmed to fit school hours",
        ["settings_holidays_changed"] = "School holidays sent to Mac",
        ["setup_code_issued"] = "Setup code issued",
        ["skipped"] = "Next downtime skipped",
        ["two_factor_enabled"] = "Two-step sign-in turned on",
        ["user_created"] = "User created",
        ["user_deleted"] = "User deleted",
        ["user_linked_to_entra"] = "User moved to Microsoft sign-in",
        ["user_updated"] = "User updated",
        ["users_imported"] = "Users imported",
    };

    /// <summary>"lock_now" as "Locked down"; one not listed as "Some new action".</summary>
    public static string Action(string action)
    {
        if (Actions.TryGetValue(action, out var label)) return label;
        var words = action.Replace('_', ' ');
        return words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>Who did it: an email, or the Mac or the portal itself.</summary>
    public static string Actor(string actor) => actor switch
    {
        "device" => "The Mac",
        "system" => "The portal",
        _ => actor,
    };

    /// <summary>
    /// The plain values of an entry's details, as "Lock until: 5 Nov 2026, 10:32 UTC", in a line. Empty if
    /// there are none, as when all it has is nested (the raw details have the rest).
    /// </summary>
    public static IReadOnlyList<string> Details(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            var lines = new List<string>();
            // A holiday's dates are also stored in words: its first and last day would say them twice.
            var worded = doc.RootElement.TryGetProperty("dates", out _);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name != "revision" && !(worded && p.Name is "first" or "last") && Value(p.Value) is { Length: > 0 } value)
                    lines.Add($"{Name(p.Name)}: {value}");
            return lines;
        }
    }

    static string? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() is { } s && Moment().IsMatch(s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? DisplayDate.DateTime(t) : e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Value).OfType<string>()),
        _ => null,
    };

    /// <summary>"lockUntil" as "Lock until".</summary>
    static string Name(string name)
    {
        var words = CamelHump().Replace(name, " $1").ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex("([A-Z])")]
    private static partial Regex CamelHump();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")]
    private static partial Regex Moment();
}
