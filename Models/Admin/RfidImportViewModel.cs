using Library_Management_system.Application.Rfid;

namespace Library_Management_system.Models.Admin;

/// <summary>Bulk RFID tag import (specification sections 35, 36).</summary>
public sealed class RfidImportViewModel
{
    /// <summary>True when the tag file shipped with the application is present on disk.</summary>
    public bool BundledFileAvailable { get; set; }

    public int BundledRowCount { get; set; }

    /// <summary>Pasted or uploaded CSV, kept so a preview can be applied without re-uploading.</summary>
    public string? Csv { get; set; }

    public TagImportDistribution Distribution { get; set; } = TagImportDistribution.ContiguousBlocks;

    public TagImportReport? Report { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Set once a real import has run, so the screen stops offering to apply again.</summary>
    public bool Applied { get; set; }

    // ---- current catalogue state, so the operator can see the effect ----

    public int TitleCount { get; set; }
    public int CopyCount { get; set; }
    public int TaggedCopyCount { get; set; }

    public int UntaggedCopyCount => Math.Max(CopyCount - TaggedCopyCount, 0);

    public bool HasReport => Report is not null;

    // ---- student cards ----

    public bool CardFileAvailable { get; set; }
    public int CardFileRowCount { get; set; }

    /// <summary>Pasted or bundled label sheet, kept so a preview can be applied unchanged.</summary>
    public string? CardText { get; set; }

    public StudentCardImportReport? CardReport { get; set; }
    public string? CardErrorMessage { get; set; }
    public bool CardsApplied { get; set; }

    public int StudentCount { get; set; }
    public int StudentsWithCardCount { get; set; }
    public int StudentsWithoutCardCount => Math.Max(StudentCount - StudentsWithCardCount, 0);

    public bool HasCardReport => CardReport is not null;

    /// <summary>Rows worth showing in full: everything that needs a decision, plus a sample.</summary>
    public IReadOnlyList<TagImportItem> DisplayItems => Report is null
        ? []
        : Report.Problems
            .Concat(Report.Items.Where(i => !i.IsProblem).Take(40))
            .ToList();
}
