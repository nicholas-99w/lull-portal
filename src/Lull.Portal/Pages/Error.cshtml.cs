using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages;

/// <summary>
/// Shown for unhandled errors (Production) and for empty 4xx responses. The
/// failed request is re-run here with its original method, so POST is handled
/// too, and without an antiforgery check (the form that failed may be stale).
/// </summary>
[IgnoreAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorModel : PageModel
{
    public int Code { get; private set; }

    /// <summary>The RequestId in the log lines for the failed request.</summary>
    public string Reference => HttpContext.TraceIdentifier;

    public void OnGet(int? code) => Code = code ?? StatusCodes.Status500InternalServerError;

    public void OnPost(int? code) => OnGet(code);
}
