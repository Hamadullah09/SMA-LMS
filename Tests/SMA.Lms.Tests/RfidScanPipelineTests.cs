using Library_Management_system.Rfid.Abstractions;
using Library_Management_system.Rfid.Pipeline;
using Library_Management_system.Rfid.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Duplicate-scan suppression and the simulator (specification sections 17, 4D, 4G, 82).
///
/// The scenario this guards against is concrete: a student holds a book near the antenna for two
/// seconds, the reader reports the tag forty times, and without suppression that becomes forty
/// issue attempts.
/// </summary>
public class RfidScanPipelineTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);
    private const string BookEpc = "E20034120102030405060708";
    private const string CardEpc = "E20034129999888877776666";

    private static RfidObservation Obs(string epc, DateTime at, int readerId = 1, int? rssi = 70) =>
        new(readerId, epc, at, rssi, 1);

    [Fact]
    public void First_sighting_of_a_tag_produces_a_scan()
    {
        var processor = new RfidScanProcessor();

        var scan = processor.Process(Obs(BookEpc, DateTime.UtcNow), Window);

        Assert.NotNull(scan);
        Assert.Equal(BookEpc, scan!.Epc);
        Assert.Equal(1, scan.ReadCount);
    }

    [Fact]
    public void Burst_of_forty_reads_collapses_to_one_scan()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;
        var scans = 0;

        // 40 observations, 25ms apart - roughly what a UHF reader produces in a second.
        for (var i = 0; i < 40; i++)
        {
            if (processor.Process(Obs(BookEpc, start.AddMilliseconds(i * 25)), Window) is not null)
            {
                scans++;
            }
        }

        Assert.Equal(1, scans);
        Assert.Equal(40, processor.GetReadCount(1, BookEpc));
    }

    [Fact]
    public void Re_presenting_the_tag_after_the_window_is_a_new_scan()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        var first = processor.Process(Obs(BookEpc, start), Window);
        // Student walks away and comes back: a genuinely new intention.
        var second = processor.Process(Obs(BookEpc, start.AddSeconds(5)), Window);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.CorrelationId, second!.CorrelationId);
    }

    [Fact]
    public void The_window_slides_so_a_tag_held_continuously_never_re_triggers()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;
        var scans = 0;

        // Ten seconds of continuous presence, sampled every second - each gap is inside the
        // 1.5s window, so the tag was never absent and must not re-trigger.
        for (var i = 0; i < 10; i++)
        {
            if (processor.Process(Obs(BookEpc, start.AddSeconds(i)), Window) is not null)
            {
                scans++;
            }
        }

        Assert.Equal(1, scans);
    }

    [Fact]
    public void Two_different_tags_are_independent()
    {
        var processor = new RfidScanProcessor();
        var now = DateTime.UtcNow;

        // A student card and a book on the same reader are two separate events.
        Assert.NotNull(processor.Process(Obs(CardEpc, now), Window));
        Assert.NotNull(processor.Process(Obs(BookEpc, now.AddMilliseconds(50)), Window));
    }

    [Fact]
    public void Same_tag_on_two_readers_is_two_events()
    {
        var processor = new RfidScanProcessor();
        var now = DateTime.UtcNow;

        // Matters for gate readers: a book seen at the desk and then at the exit is two facts.
        Assert.NotNull(processor.Process(Obs(BookEpc, now, readerId: 1), Window));
        Assert.NotNull(processor.Process(Obs(BookEpc, now, readerId: 2), Window));
    }

    [Fact]
    public void Strongest_signal_in_a_burst_is_retained()
    {
        var processor = new RfidScanProcessor();
        var start = DateTime.UtcNow;

        processor.Process(Obs(BookEpc, start, rssi: 40), Window);
        processor.Process(Obs(BookEpc, start.AddMilliseconds(20), rssi: 90), Window);
        processor.Process(Obs(BookEpc, start.AddMilliseconds(40), rssi: 55), Window);

        // Re-present after the window; the new scan starts fresh, proving the burst ended.
        var next = processor.Process(Obs(BookEpc, start.AddSeconds(10), rssi: 60), Window);
        Assert.Equal(60, next!.Rssi);
    }

    [Fact]
    public void Reset_clears_only_the_named_reader()
    {
        var processor = new RfidScanProcessor();
        var now = DateTime.UtcNow;

        processor.Process(Obs(BookEpc, now, readerId: 1), Window);
        processor.Process(Obs(BookEpc, now, readerId: 2), Window);

        processor.Reset(1);

        Assert.Equal(0, processor.GetReadCount(1, BookEpc));
        Assert.Equal(1, processor.GetReadCount(2, BookEpc));
    }

    // ---------------------------------------------------------------- simulator

    private static SimulatedRfidReaderService NewSimulator() =>
        new(readerId: 99, NullLogger<SimulatedRfidReaderService>.Instance);

    [Fact]
    public async Task Simulator_emits_observations_through_the_shared_interface()
    {
        // Typed as the interface deliberately: the application must not be able to tell the
        // simulator from real hardware (specification section 4G).
        IRfidReaderService reader = NewSimulator();
        var seen = new List<RfidObservation>();
        reader.ObservationReceived += seen.Add;

        Assert.True(await reader.ConnectAsync());
        Assert.True(await reader.StartInventoryAsync());
        ((SimulatedRfidReaderService)reader).PresentTag(BookEpc);

        Assert.Single(seen);
        Assert.Equal(BookEpc, seen[0].Epc);
    }

    [Fact]
    public async Task Simulator_ignores_tags_while_not_scanning()
    {
        var sim = NewSimulator();
        var seen = new List<RfidObservation>();
        sim.ObservationReceived += seen.Add;

        await sim.ConnectAsync();
        sim.PresentTag(BookEpc);   // inventory never started

        Assert.Empty(seen);
    }

    [Fact]
    public async Task Offline_reader_refuses_to_connect_so_manual_fallback_is_reachable()
    {
        // Specification section 64, scenario 9.
        var sim = NewSimulator();
        sim.SimulateOffline = true;

        Assert.False(await sim.ConnectAsync());
        Assert.Equal(Library_Management_system.Domain.Enums.RfidReaderStatus.Offline, sim.Status);
        Assert.False(await sim.StartInventoryAsync());
    }

    [Fact]
    public async Task Reader_recovers_after_a_disconnect()
    {
        var sim = NewSimulator();
        await sim.ConnectAsync();
        await sim.StartInventoryAsync();

        sim.SimulateDisconnect();
        Assert.Equal(Library_Management_system.Domain.Enums.RfidReaderStatus.Offline, sim.Status);

        await sim.SimulateReconnectAsync();
        Assert.Equal(Library_Management_system.Domain.Enums.RfidReaderStatus.Online, sim.Status);

        var seen = new List<RfidObservation>();
        sim.ObservationReceived += seen.Add;
        sim.PresentTag(BookEpc);
        Assert.Single(seen);
    }

    [Fact]
    public async Task Repeated_failures_mark_the_reader_in_error()
    {
        var sim = NewSimulator();
        await sim.ConnectAsync();
        sim.SimulateErrors = true;

        await sim.PingAsync();
        await sim.PingAsync();
        await sim.PingAsync();

        Assert.Equal(Library_Management_system.Domain.Enums.RfidReaderStatus.Error, sim.Status);
        Assert.Equal(3, sim.GetHealth().ConsecutiveFailures);
        Assert.NotNull(sim.GetHealth().LastError);
    }

    [Fact]
    public async Task Simulated_burst_through_the_full_pipeline_yields_one_scan()
    {
        // The end-to-end shape of specification section 64, scenario 6.
        var sim = NewSimulator();
        var processor = new RfidScanProcessor();
        var scans = new List<RfidScan>();

        sim.ObservationReceived += o =>
        {
            if (processor.Process(o, Window) is { } scan)
            {
                scans.Add(scan);
            }
        };

        await sim.ConnectAsync();
        await sim.StartInventoryAsync();
        sim.PresentTagBurst(BookEpc, times: 25);

        Assert.Single(scans);
    }

    [Fact]
    public async Task Multiple_books_in_the_field_each_produce_one_scan()
    {
        var sim = NewSimulator();
        var processor = new RfidScanProcessor();
        var scans = new List<RfidScan>();

        sim.ObservationReceived += o =>
        {
            if (processor.Process(o, Window) is { } scan)
            {
                scans.Add(scan);
            }
        };

        await sim.ConnectAsync();
        await sim.StartInventoryAsync();

        // A stack of three books, each re-read several times.
        for (var i = 0; i < 5; i++)
        {
            sim.PresentTags("EPC000000000000000000001", "EPC000000000000000000002", "EPC000000000000000000003");
        }

        Assert.Equal(3, scans.Count);
        Assert.Equal(3, scans.Select(s => s.Epc).Distinct().Count());
    }
}
