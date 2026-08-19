using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Rfid.Abstractions;

/// <summary>
/// A single raw observation from a reader, before deduplication.
/// A UHF reader emits these many times per second for a tag sitting in the field.
/// </summary>
public sealed record RfidObservation(
    int ReaderId,
    string Epc,
    DateTime ObservedUtc,
    int? Rssi = null,
    int? Antenna = null,
    string? Tid = null);

/// <summary>
/// One logical scan, after a burst of observations has been collapsed
/// (specification sections 17, 4D).
/// </summary>
public sealed record RfidScan(
    int ReaderId,
    string Epc,
    DateTime FirstObservedUtc,
    DateTime LastObservedUtc,
    int ReadCount,
    int? Rssi,
    int? Antenna,
    string CorrelationId);

public sealed record RfidReaderHealth(
    int ReaderId,
    RfidReaderStatus Status,
    DateTime? LastHeartbeatUtc,
    DateTime? LastScanUtc,
    int ConsecutiveFailures,
    string? LastError);

/// <summary>
/// Raised when the transport layer produces a tag observation. The application subscribes to this
/// rather than to sockets - specification section 87 forbids depending on raw socket or serial code.
/// </summary>
public delegate void RfidObservationHandler(RfidObservation observation);

/// <summary>
/// Transport only: move bytes to and from one device. Implemented for TCP, serial, or a
/// simulator. Knows nothing about EPCs or library rules.
/// </summary>
public interface IRfidDeviceConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<bool> SendAsync(byte[] payload, CancellationToken ct = default);

    /// <summary>Raised with whatever bytes arrived; framing is the protocol layer's problem.</summary>
    event Action<byte[]>? BytesReceived;
}

/// <summary>
/// Device-agnostic reader operations. The application depends on this and never on the D2184
/// specifically, so a different reader can be supported by adding an implementation
/// (specification sections 4, 4B).
/// </summary>
public interface IRfidReaderService : IAsyncDisposable
{
    int ReaderId { get; }
    RfidReaderStatus Status { get; }

    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Begin continuous tag detection.</summary>
    Task<bool> StartInventoryAsync(CancellationToken ct = default);
    Task StopInventoryAsync(CancellationToken ct = default);

    /// <summary>Round-trip check used for the health poll.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    RfidReaderHealth GetHealth();

    /// <summary>Raw observations, pre-deduplication.</summary>
    event RfidObservationHandler? ObservationReceived;
}

/// <summary>
/// A burst that has ended, carrying the total observations it accumulated.
///
/// The scan row is written when a burst STARTS, because circulation must react immediately and
/// cannot wait to find out how long a student holds the book near the antenna. The final read
/// count is only knowable once the burst ends, so it is written back then
/// (specification section 4D).
/// </summary>
public sealed record RfidBurstCompletion(long ScanEventId, int ReadCount, DateTime LastObservedUtc);

/// <summary>
/// Collapses observation bursts into logical scans (specification sections 17, 4D).
///
/// The reader seeing EPC001 fifty times in two seconds is ONE scan. Without this, a student
/// holding a book near the antenna would generate dozens of issue attempts.
/// </summary>
public interface IRfidScanProcessor
{
    /// <summary>
    /// Returns a scan when the observation starts a new logical scan, or null when it is a
    /// repeat inside the duplicate window.
    /// </summary>
    RfidScan? Process(RfidObservation observation, TimeSpan duplicateWindow);

    /// <summary>
    /// Associates the persisted scan row with the in-flight burst, so its read count can be
    /// written back when the burst ends. Without this the row keeps its initial count of 1.
    /// </summary>
    void AttachScanEvent(int readerId, string epc, long scanEventId);

    /// <summary>
    /// Removes and returns every burst whose duplicate window has lapsed. Bursts with no
    /// additional reads are omitted - there is nothing to correct.
    /// </summary>
    IReadOnlyList<RfidBurstCompletion> CollectCompleted(TimeSpan duplicateWindow, DateTime asOfUtc);

    /// <summary>Forget history for a reader, e.g. after it reconnects.</summary>
    void Reset(int readerId);
}

/// <summary>
/// The single way an observation enters the pipeline, whoever saw it.
/// </summary>
/// <remarks>
/// A tag read reaches this application two ways: from a reader this process is holding a socket
/// to, or relayed from another machine that is. Both must land in the same place, or the second
/// one would need its own copy of the debounce, persistence and kiosk plumbing and the two would
/// drift.
///
/// <see cref="Observed"/> is what lets a library PC forward its reads to a hosted copy of the
/// application: the bridge subscribes to it rather than tapping the reader a second time, so the
/// reader keeps its single TCP client.
/// </remarks>
public interface IRfidObservationSink
{
    /// <summary>Feed an observation in. Deduplication and persistence happen downstream.</summary>
    void Submit(RfidObservation observation);

    /// <summary>Raised for every observation submitted, before deduplication.</summary>
    event Action<RfidObservation>? Observed;
}
