using System.Threading.Channels;
using Library_Management_system.Application.Rfid;
using Library_Management_system.Data;
using Library_Management_system.Domain.Enums;
using Library_Management_system.Rfid.Abstractions;
using Library_Management_system.Rfid.D2184;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Rfid.Hosting;

/// <summary>
/// Owns the live connection to every enabled network reader, and is the missing link between the
/// hardware and the application.
///
/// Before this existed the D2184 driver was complete but unreachable: nothing constructed it, so
/// the only way a scan could enter the system was the development-only simulate button. A reader
/// could be plugged into the LAN and the software would never notice.
///
/// Shape of the pipeline, and why:
///
///   socket read loop  ->  D2184FrameReader  ->  D2184ReaderService  ->  observation
///                     ->  IRfidScanProcessor (debounce)             ->  logical scan
///                     ->  Channel                                   ->  IRfidScanRecorder (DB)
///                     ->  IRfidLiveFeed                             ->  kiosk / monitor screens
///
/// The Channel in the middle is not decoration. Observations arrive on the socket read loop, and a
/// UHF reader will happily deliver them faster than SQL Server can commit. Recording inline would
/// mean database latency backing up into the read loop, and a slow write turning into dropped tag
/// reports. Queueing hands the socket back immediately and lets a single consumer write at whatever
/// pace the database allows.
///
/// Reader unavailability must never block circulation (specification sections 3, 51), so every
/// failure here degrades to a status on the reader row and a log line. Nothing throws out of the
/// supervisor loop.
/// </summary>
public sealed class RfidReaderHostService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IRfidScanProcessor _processor;
    private readonly IRfidLiveFeed _feed;
    private readonly Application.Security.IRfidAlarmTransport _alarm;
    private readonly RfidOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RfidReaderHostService> _logger;

    /// <summary>
    /// Bounded on purpose. If the database stalls badly enough to fill this, the honest outcome is
    /// to drop the oldest queued scans and say so, rather than grow until the process dies.
    /// </summary>
    private readonly Channel<RfidScan> _queue = Channel.CreateBounded<RfidScan>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public RfidReaderHostService(
        IServiceScopeFactory scopes,
        IRfidScanProcessor processor,
        IRfidLiveFeed feed,
        Application.Security.IRfidAlarmTransport alarm,
        IOptions<RfidOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<RfidReaderHostService> logger)
    {
        _scopes = scopes;
        _processor = processor;
        _feed = feed;
        _alarm = alarm;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>How long to wait before looking again when there is nothing to connect to.</summary>
    private static readonly TimeSpan IdleRecheckInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.IsSimulator)
        {
            _logger.LogInformation(
                "Rfid:Provider is 'Simulator', so no hardware connection is opened. "
                + "Set it to 'D2184' to connect to a physical reader.");
            return;
        }

        // Re-checked on a timer rather than decided once at start-up. Enabling a reader on the
        // Reader health screen, or correcting its address, used to require restarting the site,
        // which on shared hosting means a redeploy. Now it takes effect within
        // IdleRecheckInterval and nothing has to be redeployed to turn the pad on or off.
        var announcedIdle = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var readers = _options.AutoConnect
                ? await LoadReadersAsync(stoppingToken)
                : [];

            if (readers.Count == 0)
            {
                if (!announcedIdle)
                {
                    _logger.LogInformation(
                        _options.AutoConnect
                            ? "No enabled TCP reader is configured. Waiting - add or enable one on the "
                              + "Reader health screen and it will be picked up within {Seconds}s."
                            : "RFID auto-connect is off (Rfid:AutoConnect = false). Waiting, and "
                              + "re-checking every {Seconds}s in case a reader is enabled.",
                        IdleRecheckInterval.TotalSeconds);

                    announcedIdle = true;
                }

                try
                {
                    await Task.Delay(IdleRecheckInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            announcedIdle = false;

            _logger.LogInformation(
                "Starting {Count} RFID reader connection(s): {Readers}",
                readers.Count,
                string.Join(", ", readers.Select(r => $"{r.Name} @ {r.Host}:{r.Port}")));

            // The consumer and the supervisors run until cancellation. Task.WhenAll rather than
            // fire-and-forget so a crash in any of them surfaces rather than vanishing.
            var work = readers
                .Select(reader => SuperviseAsync(reader, stoppingToken))
                .Append(ConsumeAsync(stoppingToken))
                .ToList();

            await Task.WhenAll(work);
        }
    }

    // ------------------------------------------------------------------ configuration

    private sealed record ReaderTarget(int Id, string Name, string Host, int Port, byte Address);

    /// <summary>
    /// Enabled readers that can actually be dialled. A reader row with no host is a placeholder,
    /// not a fault, so it is skipped quietly rather than reported as broken.
    /// </summary>
    private async Task<List<ReaderTarget>> LoadReadersAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.RfidReaders
            .AsNoTracking()
            .Where(r => r.IsEnabled && r.Transport == RfidTransport.Tcp)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        return rows
            .Select(r => new ReaderTarget(
                r.Id,
                r.Name,
                // Configuration is the fallback, not the override: the reader row is what an
                // administrator edits, and it must win over a value baked into appsettings.
                string.IsNullOrWhiteSpace(r.Host) ? _options.Host : r.Host!,
                r.Port ?? _options.Port,
                _options.ReaderAddressByte))
            .Where(t => !string.IsNullOrWhiteSpace(t.Host))
            .ToList();
    }

    // ------------------------------------------------------------------ per-reader supervisor

    /// <summary>
    /// Keeps one reader connected for as long as the application runs: connect, inventory, watch,
    /// and on any loss wait out the backoff and start again. A reader that is unplugged for an hour
    /// and plugged back in recovers on its own.
    /// </summary>
    private async Task SuperviseAsync(ReaderTarget target, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var connectionLost = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var connection = new D2184TcpConnection(
                target.Host, target.Port, _loggerFactory.CreateLogger<D2184TcpConnection>());

            var reader = new D2184ReaderService(
                target.Id, connection, _loggerFactory.CreateLogger<D2184ReaderService>(), target.Address);

            connection.ConnectionLost += _ => connectionLost.TrySetResult();
            reader.ObservationReceived += OnObservation;

            try
            {
                if (await reader.ConnectAsync(ct))
                {
                    attempt = 0;

                    // Give the alarm a way to reach this device for as long as the connection lasts.
                    // It receives a send delegate rather than the socket, so nothing outside this
                    // class ever holds transport (specification section 87).
                    _alarm.Attach(target.Id, (bytes, token) => connection.SendAsync(bytes, token));

                    // A reconnect must not inherit the debounce state of the previous session:
                    // tags observed before the drop are not "still in the field".
                    _processor.Reset(target.Id);

                    await reader.StartInventoryAsync(ct);
                    await PersistHealthAsync(target.Id, reader, ct);

                    _logger.LogInformation(
                        "Reader {Name} ({Host}:{Port}) is online and scanning.",
                        target.Name, target.Host, target.Port);

                    await WatchAsync(target, reader, connectionLost.Task, ct);
                }
                else
                {
                    attempt++;
                    await PersistUnreachableAsync(target, attempt, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Application shutdown.
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(ex,
                    "Unexpected failure supervising reader {Name}. Retrying.", target.Name);
                await PersistErrorAsync(target.Id, ex.Message, ct);
            }
            finally
            {
                // Detach before disposing, so a violation arriving mid-teardown cannot try to beep
                // down a socket that is closing.
                _alarm.Detach(target.Id);
                reader.ObservationReceived -= OnObservation;
                await reader.DisposeAsync();
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Back off so a reader that is switched off does not produce a connect attempt every
            // few milliseconds in the log for the rest of the day.
            var delay = TimeSpan.FromSeconds(
                Math.Min(_options.ReconnectDelaySeconds * Math.Max(attempt, 1), _options.MaxReconnectDelaySeconds));

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await MarkOfflineAsync(target.Id, CancellationToken.None);
    }

    /// <summary>
    /// Heartbeat until the connection drops. The ping doubles as the keep-alive: it proves the
    /// socket still carries traffic in both directions, which a silent reader with no tags in
    /// front of it otherwise cannot.
    /// </summary>
    private async Task WatchAsync(
        ReaderTarget target, D2184ReaderService reader, Task connectionLost, CancellationToken ct)
    {
        var heartbeat = TimeSpan.FromSeconds(Math.Max(_options.HeartbeatSeconds, 2));

        while (!ct.IsCancellationRequested)
        {
            var tick = Task.Delay(heartbeat, ct);

            if (await Task.WhenAny(tick, connectionLost) == connectionLost)
            {
                _logger.LogWarning(
                    "Reader {Name} dropped its connection. Reconnecting.", target.Name);
                return;
            }

            await reader.PingAsync(ct);
            await PersistHealthAsync(target.Id, reader, ct);

            // The driver marks Error after repeated silence. Tearing the socket down and dialling
            // again recovers from a reader that is reachable but wedged, which a ping cannot.
            if (reader.Status == RfidReaderStatus.Error)
            {
                _logger.LogWarning(
                    "Reader {Name} stopped answering health checks. Reconnecting.", target.Name);
                return;
            }
        }
    }

    // ------------------------------------------------------------------ scan pipeline

    /// <summary>
    /// Runs on the socket read loop, so it must not block and must not throw. Debounce here (cheap,
    /// in memory) and hand anything that survives to the consumer.
    /// </summary>
    private void OnObservation(RfidObservation observation)
    {
        try
        {
            var scan = _processor.Process(
                observation, TimeSpan.FromMilliseconds(_options.DuplicateWindowMs));

            if (scan is null)
            {
                // A repeat inside the window: the tag is still sitting on the antenna.
                return;
            }

            if (!_queue.Writer.TryWrite(scan))
            {
                _logger.LogWarning(
                    "Dropped scan of {Epc} on reader {ReaderId}: the recording queue is full.",
                    scan.Epc, scan.ReaderId);
            }
        }
        catch (Exception ex)
        {
            // Section 87 forbids silently ignoring RFID errors, but an exception escaping here
            // would take down the read loop and with it the reader.
            _logger.LogError(ex, "Failed to queue an observation from reader {ReaderId}.", observation.ReaderId);
        }
    }

    /// <summary>
    /// Single consumer: persists each scan, resolves it to a student or a copy, and republishes the
    /// resolved form for any screen that is watching.
    /// </summary>
    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var scan in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var recorder = scope.ServiceProvider.GetRequiredService<IRfidScanRecorder>();

                var resolution = await recorder.RecordAsync(scan, ct);

                _feed.Publish(
                    resolution.ScanEventId,
                    scan.ReaderId,
                    scan.Epc,
                    scan.LastObservedUtc,
                    scan.Rssi,
                    scan.Antenna,
                    resolution.Kind,
                    resolution.StudentId,
                    resolution.BookCopyId,
                    resolution.Description);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad scan must not stop the pipeline for every later one.
                _logger.LogError(ex,
                    "Failed to record scan of {Epc} from reader {ReaderId}.", scan.Epc, scan.ReaderId);
            }
        }
    }

    // ------------------------------------------------------------------ health persistence

    private async Task PersistHealthAsync(int readerId, IRfidReaderService reader, CancellationToken ct)
    {
        var health = reader.GetHealth();

        await UpdateReaderAsync(readerId, row =>
        {
            row.Status = health.Status;
            row.LastHeartbeatUtc = health.LastHeartbeatUtc;
            row.ConsecutiveFailures = health.ConsecutiveFailures;

            if (health.LastScanUtc is not null)
            {
                row.LastScanUtc = health.LastScanUtc;
            }

            if (health.Status == RfidReaderStatus.Online)
            {
                row.LastSuccessfulCommunicationUtc = health.LastHeartbeatUtc;
                row.LastError = null;
                row.LastErrorUtc = null;
            }
            else if (health.LastError is not null)
            {
                row.LastError = health.LastError;
                row.LastErrorUtc = DateTime.UtcNow;
            }
        }, ct);
    }

    private Task PersistUnreachableAsync(ReaderTarget target, int attempt, CancellationToken ct)
    {
        _logger.LogWarning(
            "Could not reach reader {Name} at {Host}:{Port} (attempt {Attempt}).",
            target.Name, target.Host, target.Port, attempt);

        return UpdateReaderAsync(target.Id, row =>
        {
            row.Status = RfidReaderStatus.Offline;
            row.ConsecutiveFailures = attempt;
            row.ReconnectAttempts = attempt;
            row.LastError = $"Could not connect to {target.Host}:{target.Port}. "
                            + "Check the network cable, the reader's IP address, and that no other "
                            + "application is holding its single TCP connection.";
            row.LastErrorUtc = DateTime.UtcNow;
        }, ct);
    }

    private Task PersistErrorAsync(int readerId, string message, CancellationToken ct) =>
        UpdateReaderAsync(readerId, row =>
        {
            row.Status = RfidReaderStatus.Error;
            row.LastError = message;
            row.LastErrorUtc = DateTime.UtcNow;
        }, ct);

    private Task MarkOfflineAsync(int readerId, CancellationToken ct) =>
        UpdateReaderAsync(readerId, row => row.Status = RfidReaderStatus.Offline, ct);

    private async Task UpdateReaderAsync(
        int readerId, Action<Domain.Entities.RfidReader> mutate, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var row = await db.RfidReaders.FirstOrDefaultAsync(r => r.Id == readerId, ct);
            if (row is null)
            {
                return;
            }

            mutate(row);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Health reporting is diagnostics. Failing to write it must never take a reader down.
            _logger.LogDebug(ex, "Could not persist health for reader {ReaderId}.", readerId);
        }
    }
}
