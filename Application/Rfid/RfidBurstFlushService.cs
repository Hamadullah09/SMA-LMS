using Library_Management_system.Rfid;
using Library_Management_system.Rfid.Abstractions;
using Microsoft.Extensions.Options;

namespace Library_Management_system.Application.Rfid;

/// <summary>
/// Writes back the final read count of bursts that have ended (specification section 4D).
///
/// Why a background sweep rather than updating on each read: a UHF reader re-observes a tag many
/// times per second, and section 4D is explicit that repeated observations must not each become a
/// database transaction. So counts accumulate in memory and are persisted once, after the tag
/// leaves the field.
///
/// The consequence is that a read count is briefly stale — correct within roughly one flush
/// interval of the burst ending. That is the right trade: the count is a statistic, while the scan
/// itself, which circulation depends on, is written immediately and is never delayed by this.
/// </summary>
public sealed class RfidBurstFlushService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRfidScanProcessor _processor;
    private readonly RfidOptions _options;
    private readonly ILogger<RfidBurstFlushService> _logger;

    public RfidBurstFlushService(
        IServiceScopeFactory scopeFactory,
        IRfidScanProcessor processor,
        IOptions<RfidOptions> options,
        ILogger<RfidBurstFlushService> logger)
    {
        _scopeFactory = scopeFactory;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed flush loses a statistic, never a scan. Keep going.
                _logger.LogWarning(ex, "RFID burst flush failed; will retry.");
            }
        }

        // Shutdown: bursts still in memory would otherwise keep their initial count of 1.
        try
        {
            await FlushAsync(CancellationToken.None, force: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final RFID burst flush failed during shutdown.");
        }
    }

    internal async Task<int> FlushAsync(CancellationToken ct, bool force = false)
    {
        var window = TimeSpan.FromMilliseconds(_options.DuplicateWindowMs);

        // On shutdown every burst counts as ended, however recently it was seen.
        var asOf = force ? DateTime.UtcNow.Add(window) : DateTime.UtcNow;

        var completed = _processor.CollectCompleted(window, asOf);
        if (completed.Count == 0)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IRfidScanRecorder>();

        return await recorder.ApplyBurstCompletionsAsync(completed, ct);
    }
}
