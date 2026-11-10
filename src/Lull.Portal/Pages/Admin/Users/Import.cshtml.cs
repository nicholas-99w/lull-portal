using System.Text;
using Lull.Portal.Auth;
using Lull.Portal.Data;
using Lull.Portal.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lull.Portal.Pages.Admin.Users;

/// <summary>
/// Bulk import: upload → preview (nothing saved) → import. The file's text is
/// carried in the preview form, so the import applies exactly what was previewed.
/// Parents who sign in with Entra ID are skipped: their students come from Entra.
/// </summary>
[RequestSizeLimit(MaxBytes + 64 * 1024)]
public sealed class ImportUsersModel(UserStore users, PortalSettings settings) : PageModel
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public string Csv { get; private set; } = "";
    public List<string> Errors { get; } = [];
    public IReadOnlyList<ImportResult> Results { get; private set; } = [];
    public bool Applied { get; private set; }
    /// <summary>email,code for new parents, for a mail merge. Built in the response only, never stored.</summary>
    public string CodesCsvBase64 { get; private set; } = "";

    public int Count(string action) => Results.Count(r => r.Action == action);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => (await settings.GetAsync(ct)).LocalSignIn ? Page() : Redirect("/Admin/Users");

    public async Task<IActionResult> OnPostAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            Errors.Add("Choose a CSV file.");
            return Page();
        }
        if (file.Length > MaxBytes)
        {
            Errors.Add("The file is too large (2 MB at most).");
            return Page();
        }
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        Csv = await reader.ReadToEndAsync(ct);
        return await RunAsync(apply: false, ct);
    }

    public async Task<IActionResult> OnPostApplyAsync(string? csv, CancellationToken ct)
    {
        Csv = csv ?? "";
        return await RunAsync(apply: true, ct);
    }

    async Task<IActionResult> RunAsync(bool apply, CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct)).LocalSignIn) return Redirect("/Admin/Users");
        var (rows, errors) = UserCsv.Parse(Csv);
        Errors.AddRange(errors);
        if (rows.Count == 0 && Errors.Count == 0) Errors.Add("The file has no parents in it.");
        if (rows.Count == 0) return Page();
        // Never apply a file with problems: the admin fixes it and uploads again.
        Results = await users.ImportAsync(rows, apply && Errors.Count == 0, DeviceAccess.Actor(User), ct);
        Applied = apply && Errors.Count == 0;
        if (Applied)
        {
            var sb = new StringBuilder("parentName,parentMail,setupCode\n");
            foreach (var r in Results.Where(r => r.SetupCode is not null))
                sb.Append(Quote(r.Row.Name)).Append(',').Append(r.Row.Email).Append(',').Append(r.SetupCode).Append('\n');
            CodesCsvBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        return Page();
    }

    /// <summary>CSV-quote, and stop spreadsheet formula injection from a name like "=HYPERLINK(...)".</summary>
    static string Quote(string s)
    {
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
