using Library_Management_system.Rfid.Abstractions;
using Library_Management_system.Rfid.D2184;
using Library_Management_system.Rfid.Hosting;
using Library_Management_system.Rfid.Pipeline;

namespace Library_Management_system.Rfid;

public sealed class RfidOptions
{
    public const string SectionName = "Rfid";

    /// <summary>"Simulator" or "D2184".</summary>
    public string Provider { get; set; } = "Simulator";

    /// <summary>Window within which repeated reads of one tag count as a single scan.</summary>
    public int DuplicateWindowMs { get; set; } = 1500;

    /// <summary>
    /// Fallback address for a reader row that has no host of its own. The reader row is the value an
    /// administrator edits and always wins; this exists so a stock D2184 works with no data entry.
    /// </summary>
    public string Host { get; set; } = D2184Defaults.IpAddress;

    public int Port { get; set; } = D2184Defaults.TcpPort;

    /// <summary>
    /// Reader address on the wire. Every D2184 leaves the factory as 1, and a multi-drop RS-485 bus
    /// is the only reason to change it.
    /// </summary>
    public int ReaderAddress { get; set; } = D2184Defaults.ReaderAddress;

    /// <summary>
    /// Set false to run the application with RFID hardware present but untouched — useful when two
    /// developers share one reader, since the D2184 accepts a single TCP client at a time.
    /// </summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Look for the reader on the local network when the configured address does not answer.
    /// </summary>
    /// <remarks>
    /// On by default, because a written-down IP address is the thing that breaks when the reader
    /// takes a new DHCP lease or the application is moved to another PC.
    ///
    /// Turned off for the hosted deployment. A cloud server has no route to the library LAN, so
    /// the scan could never find anything there, and what it would actually be probing is the
    /// hosting provider is own network.
    /// </remarks>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>Seconds between health checks on a connected reader.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>Base delay before redialling a reader; multiplied by the attempt count.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    public int MaxReconnectDelaySeconds { get; set; } = 60;


    public bool IsSimulator =>
        string.Equals(Provider, "Simulator", StringComparison.OrdinalIgnoreCase);

    public byte ReaderAddressByte => (byte)Math.Clamp(ReaderAddress, 0, 255);
}

public static class RfidRegistration
{
    /// <summary>
    /// Registers the RFID pipeline.
    ///
    /// The simulator is refused in Production. It is invaluable for development and automated
    /// testing (specification sections 4G, 82), and catastrophic in a live library, where it would
    /// silently accept any tag a caller invented.
    /// </summary>
    public static IServiceCollection AddSmaRfid(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var section = configuration.GetSection(RfidOptions.SectionName);
        services.Configure<RfidOptions>(section);

        var options = section.Get<RfidOptions>() ?? new RfidOptions();

        if (environment.IsProduction() && options.IsSimulator)
        {
            throw new InvalidOperationException(
                "Rfid:Provider is set to 'Simulator' in Production. The simulator accepts any tag "
                + "presented to it and must never run against a live library. Set Rfid:Provider to "
                + "'D2184', or disable RFID entirely and use the manual circulation workflow.");
        }

        // One processor for the whole application: duplicate suppression is stateful and must see
        // every observation from every reader.
        services.AddSingleton<IRfidScanProcessor, RfidScanProcessor>();

        // Holds the gap between a push pipeline (sockets) and pull consumers (browser polls).
        services.AddSingleton<IRfidLiveFeed, RfidLiveFeed>();

        // Reachability check for the reader admin screen. Separate from the host service because a
        // librarian needs to be able to test an address that is not connected, or not yet saved.
        services.AddSingleton<IRfidConnectionProbe, RfidConnectionProbe>();
        services.AddSingleton<IRfidReaderDiscovery, RfidReaderDiscovery>();

        // Exit-gate alarm (§28, §29). One instance serving two interfaces: the reader host attaches
        // live connections to it, and the gate policy sounds it. Singleton because an alarm already
        // sounding must be extended rather than restarted by the next violation.
        services.Configure<Application.Security.SecurityAlarmOptions>(
            configuration.GetSection(Application.Security.SecurityAlarmOptions.SectionName));

        services.AddSingleton<RfidBeeperAlarm>();
        services.AddSingleton<Application.Security.ISecurityAlarm>(
            sp => sp.GetRequiredService<RfidBeeperAlarm>());
        services.AddSingleton<Application.Security.IRfidAlarmTransport>(
            sp => sp.GetRequiredService<RfidBeeperAlarm>());

        services.AddScoped<Application.Security.IGateSecurityService,
                           Application.Security.GateSecurityService>();

        // Opens and maintains the actual hardware connections. It no-ops for the simulator, so the
        // registration is unconditional and the decision stays in one place.
        services.AddHostedService<RfidReaderHostService>();

        return services;
    }
}
