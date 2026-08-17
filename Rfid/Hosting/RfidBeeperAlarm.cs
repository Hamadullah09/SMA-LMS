using System.Collections.Concurrent;
using Library_Management_system.Application.Security;
using Library_Management_system.Rfid.D2184;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Rfid.Hosting;

/// <summary>
/// Sounds the D2184's own beeper when a gate violation is detected.
///
/// How, and why it looks like this: the protocol's only beeper command is
/// <c>set_beeper_mode</c> (0x7A), which sets a persistent mode — quiet, beep per inventory round, or
/// beep per tag. There is no "beep once" command. So an alarm is a *pulse* of that mode: switch the
/// beeper to per-tag, leave it armed for a few seconds while the book is still passing the antenna,
/// then switch it back to quiet.
///
/// The consequence is worth being explicit about: during those few seconds the reader beeps for any
/// tag in its field, not only the offending one. For an exit gate that is acceptable and arguably
/// correct — the alarm means "something at this doorway needs attention", not "this specific book" —
/// but it is a property of the hardware, not a choice.
/// </summary>
public sealed class RfidBeeperAlarm : ISecurityAlarm, IRfidAlarmTransport
{
    /// <summary>Beeper modes from the V3.1 protocol's set_beeper_mode parameter.</summary>
    private const byte BeeperQuiet = 0x00;
    private const byte BeeperPerTag = 0x02;

    private readonly ConcurrentDictionary<int, Func<byte[], CancellationToken, Task<bool>>> _senders = new();

    /// <summary>When each reader's current alarm should fall silent. Re-arming extends it.</summary>
    private readonly ConcurrentDictionary<int, DateTime> _silenceAt = new();

    private readonly SecurityAlarmOptions _options;
    private readonly RfidOptions _rfid;
    private readonly ILogger<RfidBeeperAlarm> _logger;

    public RfidBeeperAlarm(
        IOptions<SecurityAlarmOptions> options,
        IOptions<RfidOptions> rfid,
        ILogger<RfidBeeperAlarm> logger)
    {
        _options = options.Value;
        _rfid = rfid.Value;
        _logger = logger;
    }

    public void Attach(int readerId, Func<byte[], CancellationToken, Task<bool>> sender) =>
        _senders[readerId] = sender;

    public void Detach(int readerId)
    {
        _senders.TryRemove(readerId, out _);
        _silenceAt.TryRemove(readerId, out _);
    }

    public Task SoundAsync(int readerId, string reason, CancellationToken ct = default)
    {
        // The event is always recorded by the caller; the noise is optional.
        if (!_options.BuzzerEnabled)
        {
            _logger.LogWarning(
                "GATE ALARM (buzzer disabled) at reader {ReaderId}: {Reason}", readerId, reason);
            return Task.CompletedTask;
        }

        if (!_senders.TryGetValue(readerId, out var send))
        {
            // A gate whose reader is offline cannot sound. Still worth a loud log line, because the
            // violation happened whether or not anything could beep.
            _logger.LogWarning(
                "GATE ALARM at reader {ReaderId} could not sound — no live connection: {Reason}",
                readerId, reason);
            return Task.CompletedTask;
        }

        _logger.LogWarning("GATE ALARM at reader {ReaderId}: {Reason}", readerId, reason);

        var duration = TimeSpan.FromSeconds(Math.Clamp(_options.BuzzerSeconds, 1, 30));
        var until = DateTime.UtcNow + duration;

        // Extend an alarm already sounding rather than starting a second overlapping pulse.
        var wasSounding = _silenceAt.TryGetValue(readerId, out var existing) && existing > DateTime.UtcNow;
        _silenceAt[readerId] = wasSounding && existing > until ? existing : until;

        if (wasSounding)
        {
            return Task.CompletedTask;
        }

        // Deliberately not awaited: this runs on the scan pipeline, and a gate must keep reading
        // while it is making noise.
        _ = PulseAsync(readerId, send);

        return Task.CompletedTask;
    }

    private async Task PulseAsync(int readerId, Func<byte[], CancellationToken, Task<bool>> send)
    {
        try
        {
            await SetModeAsync(send, BeeperPerTag);

            // Re-check rather than sleeping once: a second violation during the pulse pushes the
            // silence time out, and the beeper should stay on until the latest one expires.
            while (_silenceAt.TryGetValue(readerId, out var until) && until > DateTime.UtcNow)
            {
                var remaining = until - DateTime.UtcNow;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250)
                    ? TimeSpan.FromMilliseconds(250)
                    : remaining);
            }

            await SetModeAsync(send, BeeperQuiet);
            _silenceAt.TryRemove(readerId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alarm pulse failed at reader {ReaderId}.", readerId);

            // Never leave the beeper armed because of an error — a gate stuck beeping is worse than
            // one that missed a beep.
            try { await SetModeAsync(send, BeeperQuiet); } catch { /* nothing further to try */ }
            _silenceAt.TryRemove(readerId, out _);
        }
    }

    private Task<bool> SetModeAsync(Func<byte[], CancellationToken, Task<bool>> send, byte mode)
    {
        var frame = new D2184Frame(_rfid.ReaderAddressByte, D2184Command.SetBeeperMode, [mode]);
        return send(frame.ToBytes(), CancellationToken.None);
    }

    /// <summary>
    /// Silences a reader immediately, used when a gate reader is shut down or its purpose changes.
    /// </summary>
    public async Task SilenceAsync(int readerId)
    {
        if (_senders.TryGetValue(readerId, out var send))
        {
            _silenceAt.TryRemove(readerId, out _);
            try { await SetModeAsync(send, BeeperQuiet); } catch { /* reader may already be gone */ }
        }
    }
}
