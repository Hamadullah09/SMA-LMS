using Library_Management_system.Domain.Enums;

namespace Library_Management_system.Rfid.Hosting;

/// <summary>
/// A scan that has been deduplicated, persisted and resolved, ready for a screen to react to.
///
/// <see cref="Sequence"/> is a monotonic cursor rather than a timestamp: a kiosk polling this feed
/// needs to ask "what happened since I last looked" and get an answer that cannot skip or repeat
/// an entry, which clock comparison cannot promise once two scans land in the same millisecond.
/// </summary>
public sealed record LiveScan(
    long Sequence,
    long ScanEventId,
    int ReaderId,
    string Epc,
    DateTime ObservedUtc,
    int? Rssi,
    int? Antenna,
    RfidTagKind? Kind,
    int? StudentId,
    int? BookCopyId,
    string Description)
{
    public bool IsUnknown => Kind is null;
}

/// <summary>
/// The bridge between the reader host and any screen that wants to see scans as they happen.
///
/// This exists because the scan pipeline is push (a socket read loop) and the browser is pull (an
/// HTTP poll), so something has to hold the gap. Keeping it in memory is deliberate: the durable
/// record is already in RfidScanEvents, and this only has to answer "what arrived in the last few
/// seconds" for a kiosk standing in front of the reader. Nothing here is a source of truth.
/// </summary>
public interface IRfidLiveFeed
{
    /// <summary>The cursor a caller should start from to see only future scans.</summary>
    long CurrentSequence { get; }

    void Publish(
        long scanEventId,
        int readerId,
        string epc,
        DateTime observedUtc,
        int? rssi,
        int? antenna,
        RfidTagKind? kind,
        int? studentId,
        int? bookCopyId,
        string description);

    /// <summary>
    /// Scans newer than <paramref name="cursor"/>, oldest first. A caller that has fallen further
    /// behind than the buffer holds simply misses the overflow — for a live kiosk display that is
    /// correct, because a scan from a minute ago is no longer actionable.
    /// </summary>
    IReadOnlyList<LiveScan> Since(long cursor, int? readerId = null, int max = 64);

    /// <summary>
    /// Most recent scan whose EPC matched nothing in the database. This is what makes card
    /// enrollment possible: an unassigned tag has no holder to look up, so the only way to learn
    /// its EPC is to present it to a reader and read it back off the feed.
    /// </summary>
    LiveScan? LastUnknown(int? readerId = null);
}

public sealed class RfidLiveFeed : IRfidLiveFeed
{
    /// <summary>
    /// Enough for a busy desk to poll at a comfortable interval without losing anything. A basket
    /// of books being scanned produces one entry per book, not per RF observation, so this is a
    /// generous window rather than a tight one.
    /// </summary>
    private const int Capacity = 512;

    private readonly LinkedList<LiveScan> _scans = new();
    private readonly Lock _gate = new();

    private long _sequence;

    public long CurrentSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public void Publish(
        long scanEventId,
        int readerId,
        string epc,
        DateTime observedUtc,
        int? rssi,
        int? antenna,
        RfidTagKind? kind,
        int? studentId,
        int? bookCopyId,
        string description)
    {
        lock (_gate)
        {
            var scan = new LiveScan(
                ++_sequence, scanEventId, readerId, epc, observedUtc,
                rssi, antenna, kind, studentId, bookCopyId, description);

            _scans.AddLast(scan);

            while (_scans.Count > Capacity)
            {
                _scans.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<LiveScan> Since(long cursor, int? readerId = null, int max = 64)
    {
        lock (_gate)
        {
            var results = new List<LiveScan>();

            // Walk from the newest backwards and stop at the cursor, so a caller that is up to
            // date does no work at all. The common poll returns nothing.
            for (var node = _scans.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Sequence <= cursor)
                {
                    break;
                }

                if (readerId is { } id && node.Value.ReaderId != id)
                {
                    continue;
                }

                results.Add(node.Value);

                if (results.Count >= max)
                {
                    break;
                }
            }

            results.Reverse();
            return results;
        }
    }

    public LiveScan? LastUnknown(int? readerId = null)
    {
        lock (_gate)
        {
            for (var node = _scans.Last; node is not null; node = node.Previous)
            {
                if (!node.Value.IsUnknown)
                {
                    continue;
                }

                if (readerId is { } id && node.Value.ReaderId != id)
                {
                    continue;
                }

                return node.Value;
            }

            return null;
        }
    }
}
