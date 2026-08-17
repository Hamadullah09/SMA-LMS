using System.Collections.Concurrent;

namespace Library_Management_system.Application.Kiosk;

/// <summary>
/// Mutable state of one physical self-service station.
///
/// Keyed by reader rather than by browser session on purpose. A kiosk is a piece of furniture with
/// one antenna: the books on that pad and the card tapped against it belong to whoever is standing
/// there, and that fact has to survive a page reload, a browser crash, or a librarian opening the
/// same URL to see what the student is stuck on. A cookie session would lose it.
/// </summary>
public sealed class KioskStation
{
    public KioskStation(int readerId)
    {
        ReaderId = readerId;
        LastActivityUtc = DateTime.UtcNow;

        // Seeded from the clock rather than starting at zero, because the browser caches the last
        // version it rendered and skips redrawing when the number is unchanged. A counter that
        // restarts at zero on every deploy or app-pool recycle collides with what an open kiosk page
        // already holds, and the station then appears frozen until somebody reloads it by hand.
        //
        // Milliseconds, NOT DateTime.Ticks. This value is read by JavaScript, which holds integers
        // exactly only to 2^53 (about 9.0e15). Ticks are ~6.4e17, so a tick-seeded version is
        // rounded on parse and an increment of 1 becomes invisible — every poll then looks
        // unchanged and the screen never updates again. Epoch milliseconds are ~1.8e12, far inside
        // the safe range, so +1 stays exact.
        Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public int ReaderId { get; }

    /// <summary>
    /// Serialises access to this station. A semaphore rather than a lock because applying scans
    /// needs the database, and a lock cannot be held across an await.
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public KioskMode Mode { get; set; } = KioskMode.Borrow;

    public int? StudentId { get; set; }
    public string? StudentName { get; set; }
    public string? RollNumber { get; set; }
    public string? Department { get; set; }
    public List<string> StudentWarnings { get; } = [];

    /// <summary>Keyed by copy so a tag re-read while sitting on the pad cannot duplicate a line.</summary>
    public Dictionary<int, KioskItem> Items { get; } = [];

    /// <summary>Transient messages: unknown tags, wrong-mode taps. Cleared as the student acts.</summary>
    public List<string> Notices { get; } = [];

    public KioskReceipt? Receipt { get; set; }
    public DateTime? ReceiptShownUtc { get; set; }

    public DateTime LastActivityUtc { get; set; }

    /// <summary>Position in the live scan feed. Owned by the server, never by the browser.</summary>
    public long Cursor { get; set; }

    /// <summary>
    /// Bumped on every change so the browser can tell a real update from an unchanged poll and skip
    /// re-rendering — which matters because re-rendering steals focus and restarts CSS animations.
    /// Never restarts from a fixed value; see the constructor for why.
    /// </summary>
    public long Version { get; private set; }

    public void Touch()
    {
        LastActivityUtc = DateTime.UtcNow;
        Version++;
    }

    /// <summary>
    /// Returns the station to idle. Keeps the feed cursor: books left on the pad from the previous
    /// student must not be re-collected the moment the next one walks up.
    /// </summary>
    public void Clear()
    {
        StudentId = null;
        StudentName = null;
        RollNumber = null;
        Department = null;
        StudentWarnings.Clear();
        Items.Clear();
        Notices.Clear();
        Receipt = null;
        ReceiptShownUtc = null;
        Mode = KioskMode.Borrow;
        Touch();
    }

    public void AddNotice(string message)
    {
        // Repeated presentations of the same unknown tag should not stack up the same line.
        if (!Notices.Contains(message))
        {
            Notices.Add(message);
        }

        // Only the last few are readable on a kiosk screen at arm's length.
        while (Notices.Count > 3)
        {
            Notices.RemoveAt(0);
        }
    }
}

/// <summary>Holds one <see cref="KioskStation"/> per reader for the lifetime of the application.</summary>
public sealed class KioskStationStore
{
    private readonly ConcurrentDictionary<int, KioskStation> _stations = new();

    public KioskStation Get(int readerId) =>
        _stations.GetOrAdd(readerId, id => new KioskStation(id));
}
