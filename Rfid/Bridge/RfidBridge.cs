using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Library_Management_system.Rfid.Bridge;

/// <summary>
/// Settings for relaying a reader between two copies of the application.
/// </summary>
/// <remarks>
/// The problem this solves: the reader sits on the library LAN with a private address, and the
/// public site runs in a datacentre that has no route to it. Neither port forwarding nor a static
/// address is wanted — one exposes an unauthenticated industrial device to the internet, the other
/// breaks whenever the ISP hands out a new lease.
///
/// So the connection is made the other way. The library PC dials out to the public site and holds
/// a WebSocket open; reads travel up it. Outbound is the direction NAT already allows, which is
/// why no router configuration and no fixed address are needed.
/// </remarks>
public sealed class RfidBridgeOptions
{
    public const string SectionName = "Rfid:Bridge";

    /// <summary>
    /// Where the library PC dials, e.g. wss://library.sma-techno.net/rfid/bridge. Empty on the
    /// server, which listens rather than dials.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Shared secret. Both ends need the same value, and without one the server refuses every
    /// bridge — an open relay would let anyone inject tag reads into a live library.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Which reader row the relayed reads belong to.</summary>
    public int ReaderId { get; set; } = 1;

    public int ReconnectSecondsMin { get; set; } = 3;
    public int ReconnectSecondsMax { get; set; } = 60;

    /// <summary>
    /// How often the client says it is still there. The server drops a bridge that goes quiet for
    /// three of these, so a PC that is switched off stops claiming the reader is present.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 10;

    public bool ClientEnabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Secret);
    public bool ServerEnabled => !string.IsNullOrWhiteSpace(Secret);
}

/// <summary>One message on the wire. Deliberately small and flat: it crosses the internet often.</summary>
public sealed class RfidBridgeMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("readerId")] public int ReaderId { get; set; }
    [JsonPropertyName("readerName")] public string? ReaderName { get; set; }
    [JsonPropertyName("firmware")] public string? Firmware { get; set; }
    [JsonPropertyName("online")] public bool Online { get; set; }
    [JsonPropertyName("epc")] public string? Epc { get; set; }
    [JsonPropertyName("observedUtc")] public DateTime? ObservedUtc { get; set; }
    [JsonPropertyName("rssi")] public int? Rssi { get; set; }
    [JsonPropertyName("antenna")] public int? Antenna { get; set; }

    public static class Types
    {
        public const string Hello = "hello";
        public const string Status = "status";
        public const string Observation = "observation";
        public const string Heartbeat = "heartbeat";
    }
}

/// <summary>Which readers are currently being relayed to this process, and by whom.</summary>
public interface IRfidBridgeRegistry
{
    void Report(int readerId, string? readerName, bool readerOnline);
    void Disconnected(int readerId);

    /// <summary>True when a bridge is present and reporting its reader as connected.</summary>
    bool IsOnline(int readerId);

    IReadOnlyCollection<RfidBridgeStatus> All();
}

public sealed record RfidBridgeStatus(
    int ReaderId, string? ReaderName, bool ReaderOnline, DateTime LastSeenUtc);

public sealed class RfidBridgeRegistry : IRfidBridgeRegistry
{
    /// <summary>
    /// A bridge that stops talking is not a bridge. Three missed heartbeats rather than one, so a
    /// moment of packet loss does not blink the kiosk offline in front of a student.
    /// </summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(35);

    private readonly ConcurrentDictionary<int, RfidBridgeStatus> _bridges = new();
    private readonly ILogger<RfidBridgeRegistry> _logger;

    public RfidBridgeRegistry(ILogger<RfidBridgeRegistry> logger) => _logger = logger;

    public void Report(int readerId, string? readerName, bool readerOnline)
    {
        var known = _bridges.TryGetValue(readerId, out var previous);

        _bridges[readerId] = new RfidBridgeStatus(readerId, readerName, readerOnline, DateTime.UtcNow);

        if (!known || previous!.ReaderOnline != readerOnline)
        {
            _logger.LogInformation(
                "Bridge for reader {ReaderId} ({Name}) reports the reader {State}.",
                readerId, readerName ?? "unnamed", readerOnline ? "connected" : "disconnected");
        }
    }

    public void Disconnected(int readerId)
    {
        if (_bridges.TryRemove(readerId, out _))
        {
            _logger.LogInformation("Bridge for reader {ReaderId} disconnected.", readerId);
        }
    }

    public bool IsOnline(int readerId) =>
        _bridges.TryGetValue(readerId, out var status)
        && status.ReaderOnline
        && DateTime.UtcNow - status.LastSeenUtc < Stale;

    public IReadOnlyCollection<RfidBridgeStatus> All() => _bridges.Values.ToList();
}
