namespace Library_Management_system.Application.Kiosk;

/// <summary>What the student is doing at the station.</summary>
public enum KioskMode
{
    Borrow = 0,
    Return = 1
}

/// <summary>Where the station is in its cycle. Drives which screen the kiosk renders.</summary>
public enum KioskStage
{
    /// <summary>Nobody at the station.</summary>
    Idle = 0,

    /// <summary>Books have been placed on the pad but no card has been tapped yet.</summary>
    WaitingForCard = 1,

    /// <summary>A student is identified and the basket is being filled.</summary>
    Collecting = 2,

    /// <summary>A transaction has completed and the receipt is showing.</summary>
    Finished = 3,

    /// <summary>The station is unusable — reader offline, or self-service switched off.</summary>
    Unavailable = 4
}

/// <summary>One book on the pad, with the verdict for this student already worked out.</summary>
public sealed record KioskItem(
    int BookCopyId,
    string Epc,
    string Title,
    string? Author,
    string CopyNumber,
    string? AccessionNumber,
    bool Allowed,
    string? Message,
    DateTime AddedUtc)
{
    /// <summary>Return mode only: what the student owes on this item if handed back now.</summary>
    public int OverdueDays { get; init; }
    public decimal Fine { get; init; }
    public DateTime? DueUtc { get; init; }
    public string? BorrowerName { get; init; }

    /// <summary>
    /// Cover image, so the student can match what is on screen against what is in their hand
    /// without reading a title. Null is normal — not every record has artwork.
    /// </summary>
    public string? CoverUrl { get; init; }
}

/// <summary>Result for one book after the transaction ran.</summary>
public sealed record KioskReceiptLine(
    string Title,
    string CopyNumber,
    bool Succeeded,
    string Message,
    DateTime? DueUtc,
    string? TransactionNumber,
    decimal Fine)
{
    /// <summary>Carried for the printed document, where a title alone is not enough to identify a
    /// book at the desk.</summary>
    public string? Author { get; init; }
    public string? AccessionNumber { get; init; }
}

/// <summary>The printable outcome of a completed transaction.</summary>
public sealed record KioskReceipt(
    KioskMode Mode,
    string StudentName,
    string RollNumber,
    DateTime CompletedUtc,
    IReadOnlyList<KioskReceiptLine> Lines,
    string Currency)
{
    /// <summary>Station the transaction happened at, printed so a query can be traced to a machine.</summary>
    public string? StationName { get; init; }

    public string? Department { get; init; }

    /// <summary>Loan period and fine rate, so the printed terms state the actual policy in force
    /// rather than a number hardcoded into a template.</summary>
    public int LoanDays { get; init; }
    public decimal FinePerDay { get; init; }

    /// <summary>
    /// Document reference for the printed receipt. Derived from the completion time and station
    /// rather than stored: the authoritative references are the per-line transaction numbers, and
    /// inventing a second persisted identity for a piece of paper would be one more thing to keep
    /// consistent for no gain.
    /// </summary>
    public string DocumentNumber =>
        $"SMA-{(Mode == KioskMode.Borrow ? "LN" : "RT")}-{CompletedUtc:yyyyMMdd}-{CompletedUtc:HHmmss}";

    public int SucceededCount => Lines.Count(l => l.Succeeded);
    public int FailedCount => Lines.Count(l => !l.Succeeded);
    public decimal TotalFine => Lines.Sum(l => l.Fine);

    public string Headline => Mode == KioskMode.Borrow
        ? SucceededCount switch
        {
            0 => "Nothing was issued",
            1 => "1 book issued",
            _ => $"{SucceededCount} books issued"
        }
        : SucceededCount switch
        {
            0 => "Nothing was returned",
            1 => "1 book returned",
            _ => $"{SucceededCount} books returned"
        };
}

/// <summary>
/// Everything the kiosk screen needs, in one object.
///
/// The client is deliberately dumb: it polls, renders this, and posts button presses. Keeping the
/// state machine on the server means the physical station has one authoritative state no matter how
/// many times the page is reloaded or how flaky the browser is — which matters when the state was
/// established by someone physically tapping a card.
/// </summary>
public sealed record KioskState(
    int ReaderId,
    string ReaderName,
    bool ReaderOnline,

    /// <summary>
    /// Whether this instance drives reader hardware at all.
    /// </summary>
    /// <remarks>
    /// False on the hosted deployment, where Rfid:AutoConnect is off because a cloud server has
    /// no route to the library LAN. Without this the kiosk showed the same "Reader offline" there
    /// as it would for a genuinely broken pad, which reads as a fault and sends people looking
    /// for one that does not exist.
    /// </remarks>
    bool ReaderSupportedHere,

    KioskMode Mode,
    KioskStage Stage,
    string? StudentName,
    string? RollNumber,
    string? Department,
    IReadOnlyList<string> StudentWarnings,
    IReadOnlyList<KioskItem> Items,
    IReadOnlyList<string> Notices,
    KioskReceipt? Receipt,
    int LoanDays,
    string Currency,
    int IdleSecondsRemaining,
    long Version)
{
    public int AllowedCount => Items.Count(i => i.Allowed);
    public bool CanCommit => Stage == KioskStage.Collecting && AllowedCount > 0;
    public decimal TotalFine => Items.Sum(i => i.Fine);
}
