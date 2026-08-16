using System.Net.Sockets;
using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.D2184;

/// <summary>
/// TCP transport to a D2184. The reader listens as a server (default 192.168.0.178:4001) and the
/// host dials out to it, matching the vendor SDK's ReaderMethod.ConnectServer.
///
/// This is transport only - it moves bytes. Framing belongs to <see cref="D2184FrameReader"/> and
/// meaning to <see cref="D2184InventoryParser"/>.
///
/// Differences from the vendor implementation, deliberate:
///   * the read loop is async and cancellable rather than a Thread that gets Abort()ed
///   * read failures surface instead of being swallowed by an empty catch (section 87 forbids
///     silently ignoring RFID errors)
/// </summary>
public sealed class D2184TcpConnection : IRfidDeviceConnection
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<D2184TcpConnection> _logger;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoop;

    public event Action<byte[]>? BytesReceived;

    /// <summary>Raised when the read loop stops unexpectedly, so the reader can be marked offline.</summary>
    public event Action<Exception>? ConnectionLost;

    public D2184TcpConnection(string host, int port, ILogger<D2184TcpConnection> logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
    }

    public bool IsConnected => _client?.Connected == true;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, ct);
            _stream = _client.GetStream();

            _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), CancellationToken.None);

            _logger.LogInformation("Connected to D2184 at {Host}:{Port}.", _host, _port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect to D2184 at {Host}:{Port}.", _host, _port);
            await CleanUpAsync();
            return false;
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                var read = await _stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    // Orderly shutdown by the reader.
                    throw new IOException("The reader closed the connection.");
                }

                BytesReceived?.Invoke(buffer[..read]);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "D2184 read loop for {Host}:{Port} stopped.", _host, _port);
            ConnectionLost?.Invoke(ex);
        }
    }

    public async Task<bool> SendAsync(byte[] payload, CancellationToken ct = default)
    {
        if (_stream is null || !IsConnected)
        {
            return false;
        }

        try
        {
            await _stream.WriteAsync(payload, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed writing to D2184 at {Host}:{Port}.", _host, _port);
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default) => await CleanUpAsync();

    private async Task CleanUpAsync()
    {
        if (_readLoopCts is not null)
        {
            await _readLoopCts.CancelAsync();
        }

        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* already logged */ }
        }

        _stream?.Dispose();
        _client?.Dispose();

        _stream = null;
        _client = null;
        _readLoop = null;
        _readLoopCts?.Dispose();
        _readLoopCts = null;
    }

    public async ValueTask DisposeAsync() => await CleanUpAsync();
}
