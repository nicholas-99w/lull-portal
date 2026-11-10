using Lull.Portal.Domain;

namespace Lull.Portal.Tests;

public class UserCsvTests
{
    [Fact]
    public void ParsesTheDocumentedFormat()
    {
        var (rows, errors) = UserCsv.Parse(
            "﻿parentName,parentMail,studentUsernames\r\n" +
            "Jane Smith,Jane.Smith@Example.com,\"jsmith26,asmith29\"\r\n" +
            "Sam Lee,sam.lee@example.com,slee27\r\n" +
            "\"O'Brien, Pat\",pat@example.com,\"\"\r\n");
        Assert.Empty(errors);
        Assert.Equal(3, rows.Count);
        Assert.Equal((2, "Jane Smith", "jane.smith@example.com"), (rows[0].Line, rows[0].Name, rows[0].Email)); // email lower-cased
        Assert.Equal(["jsmith26", "asmith29"], rows[0].Students);
        Assert.Equal(["slee27"], rows[1].Students);
        Assert.Equal("O'Brien, Pat", rows[2].Name);
        Assert.Empty(rows[2].Students); // no students yet is allowed
    }

    [Fact]
    public void HeadersInAnyOrderAndCaseExtraColumnsIgnored()
    {
        var (rows, errors) = UserCsv.Parse("StudentUsernames,Notes,PARENTMAIL,parentname\n\"a1, b2\",x,p@example.com,P\n\n");
        Assert.Empty(errors);
        Assert.Equal(["a1", "b2"], Assert.Single(rows).Students);
    }

    [Fact]
    public void QuotesInsideFieldsAndMultiLineQuotedValues()
    {
        var (rows, errors) = UserCsv.Parse("parentName,parentMail,studentUsernames\n\"The \"\"Boss\"\"\",b@example.com,\"x1,\nx2\"\nNext,n@example.com,y1\n");
        Assert.Empty(errors);
        Assert.Equal("The \"Boss\"", rows[0].Name);
        Assert.Equal(["x1", "x2"], rows[0].Students);
        Assert.Equal(4, rows[1].Line); // line numbers count the line break inside the quotes
    }

    [Fact]
    public void ReportsProblemsByLine()
    {
        var (rows, errors) = UserCsv.Parse(
            "parentName,parentMail,studentUsernames\n" +
            ",nobody@example.com,a1\n" +
            "Bad Mail,not-an-email,a1\n" +
            "Dup,dup@example.com,a1\n" +
            "Dup Again,DUP@example.com,a2\n" +
            "Bad Student,bs@example.com,\"ok1,bad/name\"\n");
        Assert.Equal(["dup@example.com"], rows.Select(r => r.Email));
        Assert.Contains(errors, e => e.StartsWith("Line 2:") && e.Contains("parentName is empty"));
        Assert.Contains(errors, e => e.StartsWith("Line 3:") && e.Contains("isn't an email address"));
        Assert.Contains(errors, e => e.StartsWith("Line 5:") && e.Contains("already on line 4"));
        Assert.Contains(errors, e => e.StartsWith("Line 6:") && e.Contains("bad/name"));
    }

    [Fact]
    public void BadFiles()
    {
        Assert.Contains("The file is empty.", UserCsv.Parse("").Errors);
        Assert.Contains(UserCsv.Parse("name,email\nA,a@example.com\n").Errors, e => e.Contains("Missing column: parentMail"));
        Assert.Contains(UserCsv.Parse("parentName,parentMail,studentUsernames\nA,a@example.com,\"x1\n").Errors, e => e.Contains("never closed"));
    }
}
