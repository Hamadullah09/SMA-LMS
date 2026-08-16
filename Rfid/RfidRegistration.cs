using Library_Management_system.Rfid.Abstractions;
using Library_Management_system.Rfid.Pipeline;

namespace Library_Management_system.Rfid;

public sealed class RfidOptions
{
    public const string SectionName = "Rfid";

    /// <summary>"Simulator" or "D2184".</summary>
    public string Provider { get; set; } = "Simulator";

    /// <summary>Window within which repeated reads of one tag count as a single scan.</summary>
    public int DuplicateWindowMs { get; set; } = 1500;

    public bool IsSimulator =>
        string.Equals(Provider, "Simulator", StringComparison.OrdinalIgnoreCase);
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

        return services;
    }
}
