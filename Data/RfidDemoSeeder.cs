using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// Gives sample copies an accession number and a simulated RFID tag so the circulation desk can be
/// exercised end to end without hardware (specification sections 4G, 82).
///
/// Development only - gated on SeedSampleData:Enabled, the same switch as the sample catalogue.
/// The EPCs are obviously synthetic (SMA-prefixed) so they can never be mistaken for tags read
/// from real stock.
/// </summary>
public static class RfidDemoSeeder
{
    /// <summary>
    /// <paramref name="simulatedTags"/> must be false whenever a real reader is configured. A
    /// synthetic SMAB/SMAC EPC cannot be presented to an antenna, so on real hardware these tags are
    /// not merely useless: they occupy the one active-tag slot per copy that a genuine manufacturer
    /// EPC needs, and turn every real enrolment into a replacement of something that never existed.
    /// </summary>
    public static async Task SeedAsync(
        ApplicationDbContext context, bool simulatedTags = true, CancellationToken ct = default)
    {
        await AssignAccessionNumbersAsync(context, ct);

        if (simulatedTags)
        {
            await AssignBookTagsAsync(context, ct);
            await AssignStudentCardsAsync(context, ct);
        }

        await SeedReadersAsync(context, ct);

        await context.SaveChangesAsync(ct);
    }

    private static async Task AssignAccessionNumbersAsync(ApplicationDbContext context, CancellationToken ct)
    {
        var copies = await context.BookCopies
            .Where(c => c.AccessionNumber == null && c.CopyNumber != "LEGACY")
            .ToListAsync(ct);

        foreach (var copy in copies)
        {
            copy.AccessionNumber = $"ACC-{copy.Id:D5}";
        }
    }

    private static async Task AssignBookTagsAsync(ApplicationDbContext context, CancellationToken ct)
    {
        var untagged = await context.BookCopies
            .Where(c => c.CopyNumber != "LEGACY"
                        && !context.BookRfidTags.Any(t => t.BookCopyId == c.Id && t.IsActive))
            .ToListAsync(ct);

        foreach (var copy in untagged)
        {
            context.BookRfidTags.Add(new BookRfidTag
            {
                BookCopyId = copy.Id,
                // 24 characters, the length of a 96-bit EPC. The SMAB prefix is not valid hex,
                // which is deliberate: a demo tag can never be confused with one read from stock.
                Epc = $"SMAB{copy.Id:D20}",
                State = RfidTagState.Active,
                IsActive = true,
                AssignedBy = "Demo Seed",
                AssignedUtc = DateTime.UtcNow
            });
        }
    }

    private static async Task AssignStudentCardsAsync(ApplicationDbContext context, CancellationToken ct)
    {
        var uncarded = await context.Students
            .Where(s => !context.StudentRfidTags.Any(t => t.StudentId == s.Id && t.IsActive))
            .ToListAsync(ct);

        foreach (var student in uncarded)
        {
            context.StudentRfidTags.Add(new StudentRfidTag
            {
                StudentId = student.Id,
                Epc = $"SMAC{student.Id:D20}",
                State = RfidTagState.Active,
                IsActive = true,
                AssignedBy = "Demo Seed",
                AssignedUtc = DateTime.UtcNow
            });
        }
    }

    private static async Task SeedReadersAsync(ApplicationDbContext context, CancellationToken ct)
    {
        if (await context.RfidReaders.AnyAsync(ct))
        {
            return;
        }

        context.RfidReaders.AddRange(
            new RfidReader
            {
                Name = "Circulation Desk 01",
                Model = "D2184",
                Transport = RfidTransport.Simulator,
                Purpose = RfidReaderPurpose.Checkout,
                LocationDescription = "Main circulation desk, ground floor",
                IsEnabled = true,
                Status = RfidReaderStatus.Online,
                LastHeartbeatUtc = DateTime.UtcNow,
                LastScanUtc = DateTime.UtcNow.AddMinutes(-2)
            },
            new RfidReader
            {
                Name = "Exit Gate 01",
                Model = "D2184",
                Transport = RfidTransport.Simulator,
                Purpose = RfidReaderPurpose.SecurityGate,
                LocationDescription = "Main entrance",
                IsEnabled = true,
                Status = RfidReaderStatus.Offline,
                LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-14),
                LastError = "No response to the last health check."
            });
    }
}
