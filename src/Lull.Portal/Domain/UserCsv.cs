using System.Text;
using System.Text.RegularExpressions;

namespace Lull.Portal.Domain;

/// <summary>One parent from an import file.</summary>
public sealed record UserCsvRow(int Line, string Name, string Email, IReadOnlyList<string> Students);

/// <summary>
/// Parses the bulk-import CSV: a header row with <c>parentName</c>,
/// <c>parentMail</c> and <c>studentUsernames</c> (any order, any case; other
/// columns ignored), then one parent per row. <c>studentUsernames</c> is a
/// comma-separated list in double quotes, e.g. <c>"jsmith26,asmith29"</c>.
/// RFC 4180 quoting (<c>""</c> inside quotes), LF or CRLF, and a UTF-8 BOM are handled.
/// Pure: the import page previews the result before anything is saved.
/// </summary>
public static partial class UserCsv
{
    public const int MaxRows = 10_000;

    [GeneratedRegex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")]
    private static partial Regex EmailShape();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static bool IsEmail(string email) => email.Length <= 254 && EmailShape().IsMatch(email);

    public static (IReadOnlyList<UserCsvRow> Rows, IReadOnlyList<string> Errors) Parse(string text)
    {
        var rows = new List<UserCsvRow>();
        var errors = new List<string>();
        var records = Records(text.TrimStart('﻿'), errors);
        if (records.Count == 0)
        {
            errors.Add("The file is empty.");
            return (rows, errors);
        }

        var header = records[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name.ToLowerInvariant());
        int nameCol = Col("parentName"), mailCol = Col("parentMail"), studentsCol = Col("studentUsernames");
        foreach (var (col, label) in new[] { (nameCol, "parentName"), (mailCol, "parentMail"), (studentsCol, "studentUsernames") })
            if (col < 0) errors.Add($"Missing column: {label}. The first line must be the headers parentName,parentMail,studentUsernames.");
        if (errors.Count > 0) return (rows, errors);
        if (records.Count - 1 > MaxRows)
        {
            errors.Add($"Too many rows ({records.Count - 1}); import at most {MaxRows} at a time.");
            return (rows, errors);
        }

        var seen = new Dictionary<string, int>();
        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            string Field(int col) => col < fields.Count ? fields[col].Trim() : "";
            var name = Field(nameCol);
            var email = NormalizeEmail(Field(mailCol));
            var (students, invalid) = ChildList.Parse(Field(studentsCol));
            var problems = new List<string>();
            if (name.Length == 0) problems.Add("parentName is empty");
            else if (name.Length > 200) problems.Add("parentName is longer than 200 characters");
            if (!IsEmail(email)) problems.Add($"parentMail \"{Field(mailCol)}\" isn't an email address");
            else if (seen.TryGetValue(email, out var first)) problems.Add($"{email} is already on line {first}");
            if (invalid.Count > 0) problems.Add($"not a valid student username: {string.Join(", ", invalid)}");
            if (problems.Count > 0)
            {
                errors.Add($"Line {line}: {string.Join("; ", problems)}.");
                continue;
            }
            seen[email] = line;
            rows.Add(new UserCsvRow(line, name, email, students));
        }
        return (rows, errors);
    }

    /// <summary>RFC 4180 records with the line each starts on (1-based).</summary>
    static List<(int Line, List<string> Fields)> Records(string text, List<string> errors)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        int line = 1, recordLine = 1;
        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRecord()
        {
            EndField();
            if (fields.Count > 1 || fields[0].Length > 0) records.Add((recordLine, fields));
            fields = [];
            any = false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: quoted = true; any = true; break;
                case ',': EndField(); any = true; break;
                case '\r': break;
                case '\n':
                    EndRecord();
                    line++;
                    recordLine = line;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (quoted) errors.Add($"Line {recordLine}: a quote is never closed.");
        if (any || field.Length > 0) EndRecord();
        return records;
    }
}
