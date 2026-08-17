using System.Diagnostics;
using System.Net.Sockets;
using Library_Management_system.Rfid.D2184;

namespace Library_Management_system.Rfid.Hosting;

/// <summary>Outcome of a one-shot reachability test against a reader address.</summary>
public sealed record RfidProbeResult(
    bool Reachable,
    bool SpokeProtocol,
    string? FirmwareVersion,
    int? LatencyMs,
    string Message)
{
    public static RfidProbeResult Unreachable(string message) => new(false, false, null, null, message);
}

/// <summary>
/// Answers "is there a D2184 at this address" without disturbing the running connection.
///
/// This is separate from the reader host on purpose. A librarian testing a reader is usually testing
/// one that is <em>not</em> working, or an address that has not been saved yet, so the check has to
/// stand alone. It is also why the probe reports two distinct facts: a TCP socket that opens proves
/// only that something is listening on port 4001, whereas a correct firmware reply proves it is
/// actually a reader speaking the V3.1 protocol.
/// </summary>
public interface IRfidConnectionProbe
{
    Task<RfidProbeResult> ProbeAsync(string host, int port, byte address, CancellationToken ct = default);
}

public sealed class RfidConnectionProbe : IRfidConnectionProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<RfidConnectionProbe> _logger;

    public RfidConnectionProbe(ILogger<RfidConnectionProbe> logger)
    {
        _logger = logger;
    }

    public async Task<RfidProbeResult> ProbeAsync(
        string host, int port, byte address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return RfidProbeResult.Unreachable("No host address is configured for this reader.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout + ReplyTimeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);

            await using var stream = client.GetStream();

            var request = new D2184Frame(address, D2184Command.GetFirmwareVersion).ToBytes();
            await stream.WriteAsync(request, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            var frames = new D2184FrameReader();
            var buffer = new byte[512];
            var deadline = DateTime.UtcNow + ReplyTimeout;

            while (DateTime.UtcNow < deadline)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                foreach (var frame in frames.Append(buffer.AsSpan(0, read)))
                {
                    if (frame.Command != D2184Command.GetFirmwareVersion || frame.Data.Length < 2)
                    {
                        continue;
                    }

                    stopwatch.Stop();

                    // Two bytes, major then minor - the vendor demo renders 08 02 as "8.2".
                    var version = $"{frame.Data[0]}.{frame.Data[1]}";

                    return new RfidProbeResult(
                        true, true, version, (int)stopwatch.ElapsedMilliseconds,
                        $"Reader answered on {host}:{port}. Firmware {version}.");
                }
            }

            stopwatch.Stop();

            // The port accepted a connection but nothing recognisable came back. Almost always the
            // wrong device, or a reader whose address is not what we asked for.
            return new RfidProbeResult(
                true, false, null, (int)stopwatch.ElapsedMilliseconds,
                $"Something is listening on {host}:{port} but it did not answer as a D2184. "
                + $"Check that this is the reader's address and that its reader address is {address}.");
        }
        catch (OperationCanceledException)
        {
            return RfidProbeResult.Unreachable(
                $"No answer from {host}:{port} within {(ConnectTimeout + ReplyTimeout).TotalSeconds:0} seconds. "
                + "Check the reader is powered on and on the same network.");
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Probe of {Host}:{Port} failed.", host, port);

            // The D2184 accepts one TCP client at a time, so "connection refused" while the
            // application is already connected is expected rather than a fault.
            return RfidProbeResult.Unreachable(
                $"Could not connect to {host}:{port}: {ex.SocketErrorCode}. "
                + "If the application is already connected to this reader, that is why — the D2184 "
                + "accepts only one connection at a time.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected failure probing {Host}:{Port}.", host, port);
            return RfidProbeResult.Unreachable($"Could not test {host}:{port}: {ex.Message}");
        }
    }
}
