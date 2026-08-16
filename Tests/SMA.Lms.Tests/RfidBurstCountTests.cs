using Library_Management_system.Rfid.Abstractions;
using Library_Management_system.Rfid.Pipeline;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Read-count write-back for completed bursts (specification section 4D).
///
/// Regression cover for a defect found in live testing: the scan row was written when a burst
/// STARTED, with ReadCount = 1, and never revisited. A 20-read burst was stored as 1, so the RFID
/// activity report showed raw reads equal to logical scans instead of exceeding them.
/// </summary>
public class RfidBurstCountTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);
    private const string Epc = "E20034120102030405060708";

    private static RfidObservation At(DateTime when, string epc = Epc, int reader = 1) =>
        new(reader, epc, when, 70, 1);

    [Fact]
    public void A_completed_burst_reports_its_full_read_count()
    {
        var processor = new RfidScanProcessor();
        var start = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);

        var scan = processor.Process(At(start), Window);
        Assert.NotNull(scan);
        Assert.Equal(1, scan!.ReadCount);        // what gets persisted immediately

        processor.AttachScanEvent(1, Epc, scanEventId: 42);

        // 19 further observations inside the window - the rest of the burst.
        for (var i = 1; i < 20; i++)
        {
            Assert.Null(processor.Process(At(start.AddMilliseconds(i * 20)), Window));
        }

        // The tag leaves the field.
        var completed = processor.CollectCompleted(Window, start.AddSeconds(5));

        var burst = Assert.Single(completed);
        Assert.Equal(42, burst.ScanEventId);
        Assert.Equal(20, burst.ReadCount);       // the correction that was missing
    }

    [Fact]
    public void A_burst_still_in_the_field_is_not_flushed_early()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start), Window);
        processor.AttachScanEvent(1, Epc, 1);
        processor.Process(At(start.AddMilliseconds(20)), Window);

        // Only 100ms later: the student is still holding the book to the reader.
        Assert.Empty(processor.CollectCompleted(Window, start.AddMilliseconds(100)));
    }

    [Fact]
    public void A_single_read_burst_produces_no_correction()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start), Window);
        processor.AttachScanEvent(1, Epc, 7);

        // Stored as 1 and genuinely was 1 - writing it back would be a pointless update.
        Assert.Empty(processor.CollectCompleted(Window, start.AddSeconds(5)));
    }

    [Fact]
    public void A_burst_whose_row_was_never_persisted_is_dropped_rather_than_flushed()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start), Window);
        // No AttachScanEvent: persistence failed, so there is no row to correct.
        processor.Process(At(start.AddMilliseconds(20)), Window);

        Assert.Empty(processor.CollectCompleted(Window, start.AddSeconds(5)));
    }

    [Fact]
    public void Collecting_a_burst_frees_the_tag_for_a_new_scan()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start), Window);
        processor.AttachScanEvent(1, Epc, 1);
        processor.CollectCompleted(Window, start.AddSeconds(5));

        // Presenting the tag again is a fresh intention and must scan.
        Assert.NotNull(processor.Process(At(start.AddSeconds(6)), Window));
    }

    [Fact]
    public void Bursts_on_different_readers_are_counted_separately()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start, reader: 1), Window);
        processor.AttachScanEvent(1, Epc, 101);
        processor.Process(At(start.AddMilliseconds(20), reader: 1), Window);

        processor.Process(At(start, reader: 2), Window);
        processor.AttachScanEvent(2, Epc, 202);
        processor.Process(At(start.AddMilliseconds(20), reader: 2), Window);
        processor.Process(At(start.AddMilliseconds(40), reader: 2), Window);

        var completed = processor.CollectCompleted(Window, start.AddSeconds(5))
            .ToDictionary(c => c.ScanEventId, c => c.ReadCount);

        Assert.Equal(2, completed[101]);
        Assert.Equal(3, completed[202]);
    }

    [Fact]
    public void The_flushed_timestamp_is_when_the_tag_was_last_seen_not_when_it_was_flushed()
    {
        var processor = new RfidScanProcessor();
        var start = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var lastSeen = start.AddMilliseconds(400);

        processor.Process(At(start), Window);
        processor.AttachScanEvent(1, Epc, 5);
        processor.Process(At(lastSeen), Window);

        // Flushed a minute later; the record must still say when the tag actually left.
        var burst = Assert.Single(processor.CollectCompleted(Window, start.AddMinutes(1)));
        Assert.Equal(lastSeen, burst.LastObservedUtc);
    }

    [Fact]
    public void Nothing_is_returned_twice()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(At(start), Window);
        processor.AttachScanEvent(1, Epc, 1);
        processor.Process(At(start.AddMilliseconds(20)), Window);

        Assert.Single(processor.CollectCompleted(Window, start.AddSeconds(5)));
        Assert.Empty(processor.CollectCompleted(Window, start.AddSeconds(6)));
    }
}
