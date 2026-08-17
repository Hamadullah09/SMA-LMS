using Library_Management_system.Domain.Entities;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid;
using Microsoft.EntityFrameworkCore;

namespace Library_Management_system.Data;

/// <summary>
/// Ensures the configured physical reader has a row to be managed by.
///
/// This is infrastructure, not sample data, so it runs regardless of the sample-data switch: the
/// reader host service dials readers listed in the database, and a library that has plugged a
/// D2184 into the LAN should not have to hand-create a row before the software will talk to it.
///
/// Deliberately additive. It creates the row for the configured address if none matches, and
/// otherwise corrects only the fields that describe how to reach the device. It never deletes or
/// disables a reader an administrator set up, and never touches health columns — those belong to
/// the host service.
/// </summary>
public static class RfidReaderSeeder
{
    /// <summary>The self-service station. Purpose drives which reader the kiosk binds to.</summary>
    public const string KioskReaderName = "Self-Checkout Kiosk 01";

    public static async Task SeedAsync(
        ApplicationDbContext context, RfidOptions options, CancellationToken ct = default)
    {
        if (options.IsSimulator || string.IsNullOrWhiteSpace(options.Host))
        {
            return;
        }

        var host = options.Host.Trim();

        // Match on the address rather than the name: the address is what identifies a device, and a
        // librarian is free to rename the station.
        var existing = await context.RfidReaders
            .FirstOrDefaultAsync(r => r.Transport == RfidTransport.Tcp
                                      && r.Host == host
                                      && r.Port == options.Port, ct);

        if (existing is not null)
        {
            existing.Model ??= "D2184";
            await context.SaveChangesAsync(ct);
            return;
        }

        // A reader row may already exist as a simulator placeholder from the demo seed. Promote the
        // checkout one to real hardware rather than adding a duplicate station beside it, so the
        // scan monitor and the kiosk do not end up pointing at different rows.
        var placeholder = await context.RfidReaders
            .Where(r => r.Transport == RfidTransport.Simulator
                        && (r.Purpose == RfidReaderPurpose.Checkout
                            || r.Purpose == RfidReaderPurpose.CirculationDesk))
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(ct);

        if (placeholder is not null)
        {
            placeholder.Name = KioskReaderName;
            placeholder.Model = "D2184";
            placeholder.Transport = RfidTransport.Tcp;
            placeholder.Host = host;
            placeholder.Port = options.Port;
            placeholder.Purpose = RfidReaderPurpose.Checkout;
            placeholder.AntennaCount ??= 1;
            placeholder.IsEnabled = true;

            // Let the host service establish the truth; anything else here would be a guess.
            placeholder.Status = RfidReaderStatus.Offline;
            placeholder.LastError = null;
            placeholder.LastErrorUtc = null;

            await context.SaveChangesAsync(ct);
            return;
        }

        context.RfidReaders.Add(new RfidReader
        {
            Name = KioskReaderName,
            Model = "D2184",
            Transport = RfidTransport.Tcp,
            Host = host,
            Port = options.Port,
            Purpose = RfidReaderPurpose.Checkout,
            LocationDescription = "Self-service station, library entrance",
            AntennaCount = 1,
            IsEnabled = true,
            Status = RfidReaderStatus.Offline
        });

        await context.SaveChangesAsync(ct);
    }
}
