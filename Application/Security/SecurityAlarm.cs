namespace Library_Management_system.Application.Security;

/// <summary>
/// Sounds the audible alarm at a reader (specification sections 28, 29).
///
/// Kept as an interface because the alarm is the one part of the gate whose hardware was unknown when
/// the RFID layer was designed. It is known now — the D2184 carries its own beeper — but a library
/// that wires a louder external sounder should be able to swap this without touching gate policy.
/// </summary>
public interface ISecurityAlarm
{
    /// <summary>
    /// Sounds the alarm at <paramref name="readerId"/>. Must not throw and must not block the scan
    /// pipeline: a gate whose alarm fails still has to record the event and keep reading.
    /// </summary>
    Task SoundAsync(int readerId, string reason, CancellationToken ct = default);
}

/// <summary>
/// How a live reader connection makes itself reachable to the alarm.
///
/// The alarm has to send a command to a specific device, but only the reader host owns sockets
/// (specification section 87 forbids the application depending on raw socket code). So the host
/// attaches a send delegate for each reader it brings up, and detaches it on disconnect. The alarm
/// therefore never sees a socket — only "a way to send bytes to reader 3, while it lasts".
/// </summary>
public interface IRfidAlarmTransport
{
    void Attach(int readerId, Func<byte[], CancellationToken, Task<bool>> sender);
    void Detach(int readerId);
}

public sealed class SecurityAlarmOptions
{
    public const string SectionName = "Security";

    /// <summary>Set false to record gate violations silently, without sounding anything.</summary>
    public bool BuzzerEnabled { get; set; } = true;

    /// <summary>
    /// How long the beeper stays armed after a violation. Long enough for a person to still be in
    /// the doorway, short enough that it stops on its own if nobody attends.
    /// </summary>
    public int BuzzerSeconds { get; set; } = 4;

    /// <summary>
    /// Treats every reader as an exit gate, whatever its recorded purpose.
    ///
    /// For a single-reader bench setup only. On a real installation the checkout pad must NOT be a
    /// gate: a book sitting on the pad waiting to be issued has no loan yet, so the gate rule would
    /// fire on every normal borrow.
    /// </summary>
    public bool TreatAllReadersAsGate { get; set; }
}
