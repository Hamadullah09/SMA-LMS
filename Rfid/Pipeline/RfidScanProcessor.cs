using System.Collections.Concurrent;
using Library_Management_system.Rfid.Abstractions;

namespace Library_Management_system.Rfid.Pipeline;

/// <summary>
/// Duplicate-scan suppression (specification sections 17, 4D).
///
/// A UHF reader re-observes a tag many times per second while it sits in the field. Each of those
/// is a real RF event, but only the first is a real *library* event: a student does not want to
/// borrow the same book forty times because they held it near the antenna for two seconds.
///
/// Behaviour:
///   * first sighting of an EPC on a reader  -> a new RfidScan, with a fresh correlation id
///   * repeats inside the duplicate window   -> suppressed, read count accumulates
///   * a sighting after the window has passed -> a NEW scan, because the tag was represented
///
/// The window is per (reader, EPC): two different books on one reader are independent, and the
/// same book on two readers is two events - which matters for gate readers.
///
/// Deduplication alone is not sufficient protection against double-issue. It reduces load and
/// noise; the database's unique active-loan index is the actual guarantee (section 42).
/// </summary>
public sealed class RfidScanProcessor : IRfidScanProcessor
{
    private sealed class TagState
    {
        public required DateTime FirstObservedUtc { get; init; }
        public DateTime LastObservedUtc { get; set; }
        public int ReadCount { get; set; }
        public required string CorrelationId { get; init; }
        public int? BestRssi { get; set; }
        public int? Antenna { get; set; }

        /// <summary>The persisted row this burst belongs to, once the recorder reports it.</summary>
        public long? ScanEventId { get; set; }
    }

    private readonly ConcurrentDictionary<(int ReaderId, string Epc), TagState> _state = new();

    public RfidScan? Process(RfidObservation observation, TimeSpan duplicateWindow)
    {
        var key = (observation.ReaderId, observation.Epc);

        if (_state.TryGetValue(key, out var existing))
        {
            var sinceLast = observation.ObservedUtc - existing.LastObservedUtc;

            if (sinceLast < duplicateWindow && sinceLast >= TimeSpan.Zero)
            {
                // Same tag still in the field. Accumulate, emit nothing.
                existing.LastObservedUtc = observation.ObservedUtc;
                existing.ReadCount++;

                // Keep the strongest signal seen - useful for choosing between two tags both
                // in range of a desk antenna.
                if (observation.Rssi is { } rssi && (existing.BestRssi is null || rssi > existing.BestRssi))
                {
                    existing.BestRssi = rssi;
                    existing.Antenna = observation.Antenna;
                }

                return null;
            }
        }

        // Either never seen, or the window lapsed and this is a genuine re-presentation.
        var fresh = new TagState
        {
            FirstObservedUtc = observation.ObservedUtc,
            LastObservedUtc = observation.ObservedUtc,
            ReadCount = 1,
            CorrelationId = Guid.NewGuid().ToString("N"),
            BestRssi = observation.Rssi,
            Antenna = observation.Antenna
        };

        _state[key] = fresh;

        return new RfidScan(
            observation.ReaderId,
            observation.Epc,
            fresh.FirstObservedUtc,
            fresh.LastObservedUtc,
            fresh.ReadCount,
            fresh.BestRssi,
            fresh.Antenna,
            fresh.CorrelationId);
    }

    public void AttachScanEvent(int readerId, string epc, long scanEventId)
    {
        if (_state.TryGetValue((readerId, epc), out var state))
        {
            state.ScanEventId = scanEventId;
        }
    }

    public IReadOnlyList<RfidBurstCompletion> CollectCompleted(TimeSpan duplicateWindow, DateTime asOfUtc)
    {
        var completed = new List<RfidBurstCompletion>();

        foreach (var entry in _state.ToArray())
        {
            var state = entry.Value;

            // Still in the field - the student has not taken the book away yet.
            if (asOfUtc - state.LastObservedUtc < duplicateWindow)
            {
                continue;
            }

            if (!_state.TryRemove(entry.Key, out _))
            {
                continue;
            }

            // A single-read burst was already stored correctly, and a burst whose row was never
            // persisted has nothing to correct.
            if (state.ScanEventId is { } id && state.ReadCount > 1)
            {
                completed.Add(new RfidBurstCompletion(id, state.ReadCount, state.LastObservedUtc));
            }
        }

        return completed;
    }

    public void Reset(int readerId)
    {
        foreach (var key in _state.Keys.Where(k => k.ReaderId == readerId).ToList())
        {
            _state.TryRemove(key, out _);
        }
    }

    /// <summary>Read count accumulated for a tag still inside its window. Diagnostics only.</summary>
    public int GetReadCount(int readerId, string epc) =>
        _state.TryGetValue((readerId, epc), out var s) ? s.ReadCount : 0;
}
