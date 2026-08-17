using System.Net;

namespace Library_Management_system.Application.Kiosk;

/// <summary>
/// Settings for the unattended self-service station.
///
/// The station is deliberately unauthenticated: a student identifies themselves by tapping a card
/// against the antenna, not by logging in, and asking them to type a password at a shared terminal
/// would be worse than the problem it solved. That places a real limit on what the kiosk endpoints
/// are allowed to be, and it is worth being explicit about it:
///
///   * every identity the kiosk acts on comes from a physical scan — there is no endpoint that
///     accepts a student id, so a caller cannot choose a victim;
///   * every book it acts on is one physically present on that reader's antenna;
///   * the station clears itself on an idle timer, so an abandoned session is short-lived.
///
/// What remains is that anyone who can reach the URL could press "confirm" for a session a real
/// person has already established at the pad. On a library LAN that is the same exposure as the
/// physical button on the kiosk itself. <see cref="AllowedClients"/> exists to close even that, by
/// restricting the endpoints to the kiosk machines.
/// </summary>
public sealed class KioskOptions
{
    public const string SectionName = "Kiosk";

    /// <summary>Set false to switch self-service off entirely and leave circulation to the desk.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds of inactivity after which the station forgets the student and the basket.</summary>
    public int IdleTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Lets a signed-in student be identified by their login instead of a card tap, so arriving at
    /// the kiosk from their own cart does not require presenting a card.
    ///
    /// Worth understanding before leaving this on. It only ever applies when the kiosk browser
    /// carries an authenticated cookie, so a genuine shared station where nobody signs in is
    /// unaffected. Where it does apply, a student who walks away leaves their identity on the
    /// station until the idle timer clears it — a card tap has no such window, because the card
    /// leaves with its owner. Set false to require the card everywhere.
    /// </summary>
    public bool AllowSignedInIdentity { get; set; } = true;

    /// <summary>
    /// Which machines may use the kiosk endpoints. Empty means any, which is the right default for a
    /// closed library network and the wrong one for anything reachable from outside it.
    ///
    /// Entries are either an exact address ("192.168.0.42") or a dotted prefix ("192.168.0."), which
    /// matches everything beneath it. Loopback is always permitted so the station can be tested on
    /// the machine running the application.
    /// </summary>
    public List<string> AllowedClients { get; set; } = [];

    public bool IsClientAllowed(IPAddress? address)
    {
        if (AllowedClients.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var text = address.ToString();

        return AllowedClients.Any(entry =>
        {
            var allowed = entry.Trim();

            if (allowed.Length == 0)
            {
                return false;
            }

            return allowed.EndsWith('.')
                ? text.StartsWith(allowed, StringComparison.Ordinal)
                : string.Equals(text, allowed, StringComparison.Ordinal);
        });
    }
}
