using Lull.Portal.Data;

namespace Lull.Portal.Auth;

/// <summary>
/// One warning for each refused sign-in, so that someone guessing shows up in the logs. In the log
/// rather than the audit trail, which anyone could otherwise flood from the sign-in page. It says
/// which step, why, which account and from where, and never what was typed: someone's password can
/// end up in the email box, and typed text could forge log lines.
/// </summary>
public static class SignInLog
{
    public const string Category = "SignIn";

    /// <param name="step">Which form: password, code, setup or change-password.</param>
    /// <param name="result">Logged only if it was refused (<see cref="SignInResult.Failure"/>).</param>
    public static void LogFailedSignIn(this HttpContext http, string step, SignInResult result)
    {
        if (result.Failure is not { } why) return;
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Category).LogWarning(
            "Sign-in failed at {Step}: {Reason}, account {AccountId}, from {ClientIp}",
            step, why.ToString(), result.AccountId?.ToString() ?? "none", http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
