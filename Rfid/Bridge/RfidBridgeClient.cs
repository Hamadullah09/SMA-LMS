using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.Bridge;

/// <summary>
/// The library PC's end of the relay: holds a WebSocket open to the public site and sends every
/// tag read up it.
/// </summary>
/// <remarks>
/// Runs only where <c>Rfid:Bridge:Url</c> is set, which is the machine physically wired to the
/// reader. It dials out, so the router needs no forwarded port and the connection survives the ISP
/// changing the address — the far end never has to know where this PC is.
///
/// It subscribes to the observation sink rather than opening its own connection to the reader: a
/// D2184 accepts one TCP client at a time, so a second consumer would take the pad away from the
/// application that is already using it.
/// </remarks>
public sealed class RfidBridgeClient : BackgroundService
{
    /// <summary>
    /// Bounded, and oldest-dropped. If the link stalls, the useful reads are the recent ones — a
    /// student is standing at the pad now, and replaying a queue of stale tags when the connection
    /// returns would put books on the screen that were taken away minutes ago.
    /// </summary>
    private readonly Channel<RfidBridgeMessage> _outbound =
        Channel.CreateBounded<RfidBridgeMessage>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    private readonly IRfidObservationSink _sink;
    private readonly IServiceScopeFactory _scopes;
    private readonly RfidBridgeOptions _options;
    private readonly RfidOptions _rfid;
    private readonly ILogger<RfidBridgeClient> _logger;

    public RfidBridgeClient(
        IRfidObservationSink sink,
        IServiceScopeFactory scopes,
        IOptions<RfidBridgeOptions> options,
        IOptions<RfidOptions> rfid,
        ILogger<RfidBridgeClient> logger)
    {
        _sink = sink;
        _scopes = scopes;
        _options = options.Value;
        _rfid = rfid.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ClientEnabled)
        {
            return;   // this copy is not the one attached to a reader
        }

        _logger.LogInformation(
            "Relaying reader {ReaderId} to {Url}.", _options.ReaderId, _options.Url);

        _sink.Observed += OnObserved;

        var attempt = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(stoppingToken);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    attempt++;
                    _logger.LogWarning(
                        "Bridge to {Url} is down ({Message}). Retrying.", _options.Url, ex.Message);
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var wait = Math.Min(
                    _options.ReconnectSecondsMin * Math.Max(attempt, 1),
                    _options.ReconnectSecondsMax);

                await Task.Delay(TimeSpan.FromSeconds(wait), stoppingToken);
            }
        }
        finally
        {
            _sink.Observed -= OnObserved;
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Bridge-Secret", _options.Secret);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(_options.HeartbeatSeconds);

        await socket.ConnectAsync(new Uri(_options.Url!), ct);

        _logger.LogInformation("Bridge to {Url} is up.", _options.Url);

        await SendAsync(socket, new RfidBridgeMessage
        {
            Type = RfidBridgeMessage.Types.Hello,
            ReaderId = _options.ReaderId,
            ReaderName = await ReaderNameAsync(ct),
            Online = await ReaderOnlineAsync(ct)
        }, ct);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var pump = PumpAsync(socket, session.Token);
        var beat = HeartbeatAsync(socket, session.Token);

        // Whichever stops first ends the session; the outer loop then redials.
        await Task.WhenAny(pump, beat);
        await session.CancelAsync();

        try { await Task.WhenAll(pump, beat); } catch { /* already reported */ }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            await SendAsync(socket, message, ct);
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatSeconds), ct);

            // Carries the reader's state, so the far end learns the pad was unplugged even during a
            // quiet spell with no tags to report.
            await SendAsync(socket, new RfidBridgeMessage
            {
                Type = RfidBridgeMessage.Types.Heartbeat,
                ReaderId = _options.ReaderId,
                ReaderName = await ReaderNameAsync(ct),
                Online = await ReaderOnlineAsync(ct)
            }, ct);
        }
    }

    private static async Task SendAsync(WebSocket socket, RfidBridgeMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private void OnObserved(RfidObservation observation)
    {
        _outbound.Writer.TryWrite(new RfidBridgeMessage
        {
            Type = RfidBridgeMessage.Types.Observation,
            ReaderId = _options.ReaderId,
            Epc = observation.Epc,
            ObservedUtc = observation.ObservedUtc,
            Rssi = observation.Rssi,
            Antenna = observation.Antenna
        });
    }

    private async Task<bool> ReaderOnlineAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();

        var row = await db.RfidReaders
            .AsNoTracking()
            .Where(r => r.Id == _options.ReaderId)
            .Select(r => new { r.Status, r.LastHeartbeatUtc })
            .FirstOrDefaultAsync(ct);

        var window = TimeSpan.FromSeconds(Math.Max(_rfid.HeartbeatSeconds, 5) * 3);

        return row?.Status == Domain.Enums.RfidReaderStatus.Online
               && row.LastHeartbeatUtc is { } beat
               && DateTime.UtcNow - beat < window;
    }

    private async Task<string?> ReaderNameAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();

        return await db.RfidReaders
            .AsNoTracking()
            .Where(r => r.Id == _options.ReaderId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct);
    }
}
