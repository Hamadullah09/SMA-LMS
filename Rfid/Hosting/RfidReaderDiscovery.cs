using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Library_Management_system.Rfid.Hosting;

/// <summary>A reader found on the network, confirmed by its firmware reply.</summary>
public sealed record DiscoveredReader(string Host, int Port, string? FirmwareVersion);

/// <summary>
/// Finds a D2184 on the local network without being told its address.
/// </summary>
/// <remarks>
/// The reader's address was configured once and written down. That breaks the moment anything
/// moves: the reader takes a new DHCP lease, or the application runs on a different PC on a
/// different subnet, and the kiosk reports the pad offline while the hardware sits there working.
/// Discovery removes the written-down address from the equation — whatever machine the application
/// runs on, it looks around its own networks and finds the reader.
///
/// It is a scan, so it is deliberately narrow:
///
///   * Only IPv4 interfaces that are up, not loopback, and carry a private (RFC 1918) address.
///     Scanning a public range would be scanning somebody else's network.
///   * Only /24 at most. A /16 is 65,000 probes and would take minutes; the reader is on the same
///     LAN segment as the PC or it is not reachable at all.
///   * A TCP handshake alone is not accepted as a reader. Plenty of things listen on port 4001, so
///     each candidate has to answer GetFirmwareVersion correctly, which is what the probe checks.
/// </remarks>
public interface IRfidReaderDiscovery
{
    Task<DiscoveredReader?> FindAsync(int port, byte address, CancellationToken ct = default);
}

public sealed class RfidReaderDiscovery : IRfidReaderDiscovery
{
    /// <summary>Long enough for a switched LAN, short enough that 254 of them stay quick.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>Sockets in flight. Bounded so the scan cannot exhaust the port table.</summary>
    private const int Parallelism = 64;

    private readonly IRfidConnectionProbe _probe;
    private readonly ILogger<RfidReaderDiscovery> _logger;

    public RfidReaderDiscovery(IRfidConnectionProbe probe, ILogger<RfidReaderDiscovery> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    public async Task<DiscoveredReader?> FindAsync(int port, byte address, CancellationToken ct = default)
    {
        var candidates = LocalCandidates().ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "Reader discovery found no private IPv4 network to search. Set Rfid:Host instead.");
            return null;
        }

        _logger.LogInformation(
            "Searching {Count} address(es) on {Networks} for a reader on port {Port}.",
            candidates.Count,
            string.Join(", ", LocalNetworks().Select(n => n.Description)),
            port);

        // Two passes rather than one. Opening a socket is cheap and most addresses answer
        // nothing, so the whole range is swept for an open port first; only the few that
        // answered are then asked to prove they are a reader, which is the slow part.
        var open = await SweepAsync(candidates, port, ct);

        if (open.Count == 0)
        {
            _logger.LogInformation("Nothing is listening on port {Port} on this network.", port);
            return null;
        }

        _logger.LogInformation(
            "Port {Port} is open on {Hosts}. Checking which speaks the reader protocol.",
            port, string.Join(", ", open));

        foreach (var host in open)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _probe.ProbeAsync(host, port, address, ct);

            if (result.SpokeProtocol)
            {
                _logger.LogInformation(
                    "Found a reader at {Host}:{Port}, firmware {Firmware}.",
                    host, port, result.FirmwareVersion ?? "unknown");

                return new DiscoveredReader(host, port, result.FirmwareVersion);
            }

            _logger.LogDebug(
                "{Host}:{Port} accepted a connection but is not a D2184 ({Message}).",
                host, port, result.Message);
        }

        _logger.LogInformation(
            "Something is listening on port {Port} but nothing answered as a D2184.", port);

        return null;
    }

    /// <summary>Addresses with an open port, in the order they replied.</summary>
    private static async Task<List<string>> SweepAsync(
        List<string> candidates, int port, CancellationToken ct)
    {
        var found = new List<string>();
        var gate = new SemaphoreSlim(Parallelism);
        var sync = new object();

        var work = candidates.Select(async host =>
        {
            await gate.WaitAsync(ct);

            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);

                await client.ConnectAsync(host, port, timeout.Token);

                lock (sync)
                {
                    found.Add(host);
                }
            }
            catch
            {
                // Nothing there, refused, or timed out. All the same answer for a scan.
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(work);
        return found;
    }

    private sealed record LocalNetwork(uint Network, uint Mask, string Description);

    private static IEnumerable<LocalNetwork> LocalNetworks()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork ||
                    info.IPv4Mask is null ||
                    !IsPrivate(info.Address))
                {
                    continue;
                }

                var ip = ToUInt32(info.Address);
                var mask = ToUInt32(info.IPv4Mask);

                // Never widen past /24: a /16 sweep is 65,000 probes and minutes of waiting.
                var narrowed = Math.Max(mask, 0xFFFFFF00u);

                yield return new LocalNetwork(
                    ip & narrowed,
                    narrowed,
                    $"{info.Address}/{CountBits(narrowed)}");
            }
        }
    }

    private static IEnumerable<string> LocalCandidates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var net in LocalNetworks())
        {
            var hostCount = ~net.Mask;

            // Skip .0 and the broadcast address at the top.
            for (var offset = 1u; offset < hostCount; offset++)
            {
                var candidate = FromUInt32(net.Network + offset);

                if (seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();

        return b[0] switch
        {
            10 => true,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => b[1] == 168,
            _ => false
        };
    }

    private static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string FromUInt32(uint value) =>
        $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    private static int CountBits(uint mask)
    {
        var count = 0;
        while (mask != 0)
        {
            count += (int)(mask & 1);
            mask >>= 1;
        }
        return count;
    }
}
