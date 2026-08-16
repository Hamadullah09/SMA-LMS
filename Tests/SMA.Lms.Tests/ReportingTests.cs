using Library_Management_system.Application.Reporting;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// CSV export (specification section 54).
///
/// The formula-injection guard matters: a book titled "=cmd|..." or an author name starting with
/// "-" would otherwise be executed by spreadsheet software when a librarian opens the export.
/// </summary>
public class CsvExportTests
{
    private static readonly ReportingService Service = new(null!);

    private static string Csv(params string[] cells) =>
        Service.ToCsv(new ReportResult("t", "T", "d", ["A", "B", "C"], [new ReportRow(cells)]));

    [Fact]
    public void Header_row_is_written_first()
    {
        var csv = Csv("1", "2", "3");
        Assert.StartsWith("A,B,C", csv);
    }

    [Fact]
    public void Fields_containing_commas_are_quoted()
    {
        Assert.Contains("\"Martin, Robert C.\"", Csv("Martin, Robert C.", "b", "c"));
    }

    [Fact]
    public void Embedded_quotes_are_doubled()
    {
        // RFC 4180: a literal " inside a quoted field is written as "".
        Assert.Contains("\"He said \"\"hello\"\"\"", Csv("He said \"hello\"", "b", "c"));
    }

    [Fact]
    public void Newlines_inside_a_field_are_quoted_rather_than_breaking_the_row()
    {
        var csv = Csv("line one\nline two", "b", "c");
        Assert.Contains("\"line one\nline two\"", csv);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+44 7700 900000")]
    [InlineData("-lookup")]
    [InlineData("@SUM(A1)")]
    public void Formula_like_values_are_neutralised(string dangerous)
    {
        var csv = Csv(dangerous, "b", "c");

        // Prefixed with an apostrophe so a spreadsheet treats it as text.
        Assert.Contains("'" + dangerous.TrimStart('"'), csv.Replace("\"", ""));
    }

    [Fact]
    public void Ordinary_values_are_not_quoted_unnecessarily()
    {
        var csv = Csv("Dune", "Frank Herbert", "6");
        Assert.Contains("Dune,Frank Herbert,6", csv);
    }

    [Fact]
    public void Empty_report_still_produces_a_header()
    {
        var csv = Service.ToCsv(new ReportResult("t", "T", "d", ["A", "B"], []));
        Assert.Equal("A,B", csv.Trim());
    }
}

/// <summary>Every report the UI offers must actually be dispatchable.</summary>
public class ReportCatalogueTests
{
    [Fact]
    public void Report_keys_are_unique()
    {
        var service = new ReportingService(null!);
        var keys = service.Available.Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Every_report_has_a_title_and_a_description()
    {
        var service = new ReportingService(null!);

        Assert.All(service.Available, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
        });
    }

    [Fact]
    public void The_reports_named_in_the_specification_are_present()
    {
        var service = new ReportingService(null!);
        var keys = service.Available.Select(d => d.Key).ToHashSet();

        // Specification section 54.
        Assert.Contains("circulation", keys);
        Assert.Contains("most-borrowed", keys);
        Assert.Contains("overdue", keys);
        Assert.Contains("fines", keys);
        Assert.Contains("active-students", keys);
        Assert.Contains("rfid-activity", keys);
        Assert.Contains("manual-transactions", keys);
    }
}
