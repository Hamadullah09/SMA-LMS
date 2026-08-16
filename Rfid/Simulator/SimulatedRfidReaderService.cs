using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.Simulator;

/// <summary>
/// A reader with no hardware behind it (specification sections 4G, 82).
///
/// Implements the same <see cref="IRfidReaderService"/> as the real D2184, so the application
/// genuinely cannot tell them apart - that is the point, and it is what makes the whole RFID
/// pipeline testable and demonstrable without a physical reader.
///
/// Must never be registered in Production; the composition root enforces that.
/// </summary>
public sealed class SimulatedRfidReaderService : IRfidReaderService
{
    private readonly ILogger<SimulatedRfidReaderService> _logger;
    private readonly object _gate = new();

    private RfidReaderStatus _status = RfidReaderStatus.Offline;
    private DateTime? _lastHeartbeatUtc;
    private DateTime? _lastScanUtc;
    private int _consecutiveFailures;
    private string? _lastError;
    private bool _inventoryRunning;

    /// <summary>Set to make Connect fail, so the manual-fallback path can be exercised (scenario 9).</summary>
    public bool SimulateOffline { get; set; }

    /// <summary>Set to make every command fail, simulating a reader that answers but misbehaves.</summary>
    public bool SimulateErrors { get; set; }

    public int ReaderId { get; }
    public RfidReaderStatus Status => _status;

    public event RfidObservationHandler? ObservationReceived;

    public SimulatedRfidReaderService(int readerId, ILogger<SimulatedRfidReaderService> logger)
    {
        ReaderId = readerId;
        _logger = logger;
    }

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (SimulateOffline)
        {
            _status = RfidReaderStatus.Offline;
            _lastError = "Simulated reader is switched off.";
            _consecutiveFailures++;
            return Task.FromResult(false);
        }

        _status = RfidReaderStatus.Online;
        _lastHeartbeatUtc = DateTime.UtcNow;
        _consecutiveFailures = 0;
        _lastError = null;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _inventoryRunning = false;
        _status = RfidReaderStatus.Offline;
        return Task.CompletedTask;
    }

    public Task<bool> StartInventoryAsync(CancellationToken ct = default)
    {
        if (_status != RfidReaderStatus.Online)
        {
            return Task.FromResult(false);
        }

        _inventoryRunning = true;
        return Task.FromResult(true);
    }

    public Task StopInventoryAsync(CancellationToken ct = default)
    {
        _inventoryRunning = false;
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (SimulateOffline || SimulateErrors)
        {
            _consecutiveFailures++;
            _lastError = "Simulated reader did not respond.";
            if (_consecutiveFailures >= 3)
            {
                _status = RfidReaderStatus.Error;
            }
            return Task.FromResult(false);
        }

        _lastHeartbeatUtc = DateTime.UtcNow;
        _consecutiveFailures = 0;
        return Task.FromResult(true);
    }

    // ---------------------------------------------------------- test controls

    /// <summary>Present a tag once.</summary>
    public void PresentTag(string epc, int? rssi = 70, int? antenna = 1)
    {
        lock (_gate)
        {
            if (!_inventoryRunning)
            {
                _logger.LogDebug("Tag {Epc} ignored: simulated reader is not scanning.", epc);
                return;
            }

            _lastScanUtc = DateTime.UtcNow;
            ObservationReceived?.Invoke(
                new RfidObservation(ReaderId, epc, _lastScanUtc.Value, rssi, antenna));
        }
    }

    /// <summary>
    /// Present a tag repeatedly, as real hardware does while an item sits in the field.
    /// Used to prove duplicate suppression collapses the burst into one scan.
    /// </summary>
    public void PresentTagBurst(string epc, int times, int? rssi = 70, int? antenna = 1)
    {
        for (var i = 0; i < times; i++)
        {
            PresentTag(epc, rssi, antenna);
        }
    }

    /// <summary>Several different tags in the field at once, e.g. a stack of books.</summary>
    public void PresentTags(params string[] epcs)
    {
        foreach (var epc in epcs)
        {
            PresentTag(epc);
        }
    }

    /// <summary>Drop the connection mid-operation (scenario 9).</summary>
    public void SimulateDisconnect()
    {
        _inventoryRunning = false;
        _status = RfidReaderStatus.Offline;
        _lastError = "Simulated connection loss.";
        _consecutiveFailures++;
    }

    public async Task SimulateReconnectAsync()
    {
        SimulateOffline = false;
        await ConnectAsync();
        await StartInventoryAsync();
    }

    public RfidReaderHealth GetHealth() => new(
        ReaderId, _status, _lastHeartbeatUtc, _lastScanUtc, _consecutiveFailures, _lastError);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
