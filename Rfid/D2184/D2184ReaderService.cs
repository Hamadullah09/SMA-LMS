using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.D2184;

/// <summary>
/// The real D2184 reader, wiring transport -> framing -> inventory parsing -> observations.
///
/// Layering (RFID_ARCHITECTURE.md section 2):
///   IRfidReaderService  <- this
///   IRfidDeviceConnection + D2184FrameReader + D2184InventoryParser
///   TCP or serial
/// </summary>
public sealed class D2184ReaderService : IRfidReaderService
{
    private readonly IRfidDeviceConnection _connection;
    private readonly D2184FrameReader _frames = new();
    private readonly ILogger<D2184ReaderService> _logger;
    private readonly byte _address;

    private RfidReaderStatus _status = RfidReaderStatus.Offline;
    private DateTime? _lastHeartbeatUtc;
    private DateTime? _lastScanUtc;
    private int _consecutiveFailures;
    private string? _lastError;
    private bool _inventoryRunning;

    public int ReaderId { get; }
    public RfidReaderStatus Status => _status;

    public event RfidObservationHandler? ObservationReceived;

    public D2184ReaderService(
        int readerId,
        IRfidDeviceConnection connection,
        ILogger<D2184ReaderService> logger,
        byte address = D2184Defaults.ReaderAddress)
    {
        ReaderId = readerId;
        _connection = connection;
        _logger = logger;
        _address = address;

        _connection.BytesReceived += OnBytesReceived;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _status = RfidReaderStatus.Connecting;

        if (await _connection.ConnectAsync(ct))
        {
            _status = RfidReaderStatus.Online;
            _lastHeartbeatUtc = DateTime.UtcNow;
            _consecutiveFailures = 0;
            _lastError = null;
            return true;
        }

        _status = RfidReaderStatus.Offline;
        _consecutiveFailures++;
        _lastError = "Could not open a connection to the reader.";
        return false;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _inventoryRunning = false;
        await _connection.DisconnectAsync(ct);
        _frames.Reset();
        _status = RfidReaderStatus.Offline;
    }

    public async Task<bool> StartInventoryAsync(CancellationToken ct = default)
    {
        // 0x89 with Repeat = 0xFF: the reader streams tags as it sees them.
        var frame = new D2184Frame(
            _address, D2184Command.RealTimeInventory, [D2184Defaults.ShortestInventoryRepeat]);

        var sent = await _connection.SendAsync(frame.ToBytes(), ct);
        _inventoryRunning = sent;

        if (!sent)
        {
            RecordFailure("Could not start scanning on the reader.");
        }

        return sent;
    }

    public Task StopInventoryAsync(CancellationToken ct = default)
    {
        // Real-time inventory ends when its repeat count is exhausted; the reader has no explicit
        // stop for 0x89. Suppressing locally is what "stop" means here, and it is honest about
        // the protocol rather than sending a command the device does not implement.
        _inventoryRunning = false;
        return Task.CompletedTask;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var frame = new D2184Frame(_address, D2184Command.GetFirmwareVersion);
        var sent = await _connection.SendAsync(frame.ToBytes(), ct);

        if (sent)
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
            _consecutiveFailures = 0;
            if (_status == RfidReaderStatus.Error)
            {
                _status = RfidReaderStatus.Online;
            }
        }
        else
        {
            RecordFailure("The reader did not respond to a health check.");
        }

        return sent;
    }

    private void OnBytesReceived(byte[] bytes)
    {
        foreach (var frame in _frames.Append(bytes))
        {
            HandleFrame(frame);
        }
    }

    private void HandleFrame(D2184Frame frame)
    {
        _lastHeartbeatUtc = DateTime.UtcNow;

        if (frame.Command != D2184Command.RealTimeInventory)
        {
            // Firmware version, antenna config and similar. Their arrival alone proves the
            // reader is alive, which is all the health check needs.
            return;
        }

        switch (D2184InventoryParser.Parse(frame))
        {
            case D2184InventoryResult.Tag tag when _inventoryRunning:
                _lastScanUtc = DateTime.UtcNow;
                _consecutiveFailures = 0;
                ObservationReceived?.Invoke(new RfidObservation(
                    ReaderId,
                    tag.Report.Epc,
                    _lastScanUtc.Value,
                    tag.Report.Rssi,
                    tag.Report.Antenna));
                break;

            case D2184InventoryResult.Completed completed:
                _logger.LogDebug(
                    "Reader {ReaderId} finished a round: {TotalRead} reads on antenna {Antenna}.",
                    ReaderId, completed.Summary.TotalRead, completed.Summary.Antenna);

                // A round ends on its own; restart so detection stays continuous.
                if (_inventoryRunning)
                {
                    _ = StartInventoryAsync();
                }
                break;

            case D2184InventoryResult.Failed failed:
                // "No tag" is the normal idle answer, not a fault.
                if (failed.ErrorCode != D2184ErrorCode.NoTagError)
                {
                    RecordFailure(D2184ErrorCode.FriendlyMessage(failed.ErrorCode));
                    _logger.LogWarning(
                        "Reader {ReaderId} reported {Error}.",
                        ReaderId, D2184ErrorCode.TechnicalName(failed.ErrorCode));
                }
                else if (_inventoryRunning)
                {
                    _ = StartInventoryAsync();
                }
                break;

            case D2184InventoryResult.Unrecognised unknown:
                _logger.LogWarning(
                    "Reader {ReaderId} sent an unrecognised inventory frame: {Reason}",
                    ReaderId, unknown.Reason);
                break;
        }
    }

    private void RecordFailure(string message)
    {
        _consecutiveFailures++;
        _lastError = message;

        // One blip is not an outage; a run of them is.
        if (_consecutiveFailures >= 3)
        {
            _status = RfidReaderStatus.Error;
        }
    }

    public RfidReaderHealth GetHealth() => new(
        ReaderId, _status, _lastHeartbeatUtc, _lastScanUtc, _consecutiveFailures, _lastError);

    public async ValueTask DisposeAsync()
    {
        _connection.BytesReceived -= OnBytesReceived;
        await _connection.DisposeAsync();
    }
}
