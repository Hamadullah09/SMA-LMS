using System.Globalization;
using Library_Management_system.Data;
using Library_Management_system.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Library_Management_system.Application.Policies;

/// <summary>
/// Reads configurable library rules (specification sections 22, 58).
///
/// Replaces the hardcoded <c>DefaultBorrowingDays = 14</c> and <c>FinePerLateDay = 1.00m</c>
/// constants found in ManageBorrowingBookController during the Phase 1 audit.
/// </summary>
public interface ILibraryPolicyService
{
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<decimal> GetDecimalAsync(string key, decimal fallback, CancellationToken ct = default);
    Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default);
    Task<LoanPolicySnapshot> GetLoanPolicyAsync(CancellationToken ct = default);
    void Invalidate();
}

/// <summary>
/// The loan rules resolved once, so a single circulation operation cannot see two different
/// versions of policy part-way through.
/// </summary>
public sealed record LoanPolicySnapshot(
    int MaximumLoanDays,
    int DefaultLoanDays,
    int MaximumBooksPerStudent,
    int MaximumOverdueBooks,
    int MaximumRenewals,
    int RenewalDays,
    decimal FinePerDay,
    string Currency,
    int GracePeriodDays,
    decimal MaximumOutstandingFine);

public sealed class LibraryPolicyService : ILibraryPolicyService
{
    private const string CacheKey = "sma:policies";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    public LibraryPolicyService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    /// <summary>
    /// Defaults used when a policy row is missing. These are a safety net for a partially seeded
    /// database, not the configuration surface - the seeder writes real rows an admin can edit.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [LibraryPolicy.Keys.MaximumLoanDays] = "30",
        [LibraryPolicy.Keys.DefaultLoanDays] = "14",
        [LibraryPolicy.Keys.MaximumBooksPerStudent] = "5",
        [LibraryPolicy.Keys.MaximumOverdueBooks] = "2",
        [LibraryPolicy.Keys.MaximumRenewals] = "2",
        [LibraryPolicy.Keys.RenewalDays] = "14",
        [LibraryPolicy.Keys.FinePerDay] = "20.00",
        [LibraryPolicy.Keys.FineCurrency] = "PKR",
        [LibraryPolicy.Keys.FineGracePeriodDays] = "0",
        [LibraryPolicy.Keys.MaximumOutstandingFine] = "500.00",
        [LibraryPolicy.Keys.LostBookCharge] = "2000.00",
        [LibraryPolicy.Keys.ReservationExpiryDays] = "3",
        [LibraryPolicy.Keys.MaximumReservations] = "3",
        [LibraryPolicy.Keys.ReminderDaysBeforeDue] = "3,1",
        [LibraryPolicy.Keys.OverdueEscalationDays] = "1,3,7,14",
        [LibraryPolicy.Keys.EmailRetryCount] = "5",
        [LibraryPolicy.Keys.RfidDuplicateWindowMs] = "1500",
        [LibraryPolicy.Keys.ReaderHeartbeatIntervalSeconds] = "30"
    };

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out Dictionary<string, string>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _db.LibraryPolicies
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase, ct);

        _cache.Set(CacheKey, rows, CacheLifetime);
        return rows;
    }

    public void Invalidate() => _cache.Remove(CacheKey);

    public async Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default)
    {
        var all = await LoadAsync(ct);
        if (all.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Defaults.TryGetValue(key, out var seeded) ? seeded : fallback;
    }

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
    {
        var raw = await GetStringAsync(key, fallback.ToString(CultureInfo.InvariantCulture), ct);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback, CancellationToken ct = default)
    {
        var raw = await GetStringAsync(key, fallback.ToString(CultureInfo.InvariantCulture), ct);
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public async Task<LoanPolicySnapshot> GetLoanPolicyAsync(CancellationToken ct = default)
    {
        return new LoanPolicySnapshot(
            MaximumLoanDays: await GetIntAsync(LibraryPolicy.Keys.MaximumLoanDays, 30, ct),
            DefaultLoanDays: await GetIntAsync(LibraryPolicy.Keys.DefaultLoanDays, 14, ct),
            MaximumBooksPerStudent: await GetIntAsync(LibraryPolicy.Keys.MaximumBooksPerStudent, 5, ct),
            MaximumOverdueBooks: await GetIntAsync(LibraryPolicy.Keys.MaximumOverdueBooks, 2, ct),
            MaximumRenewals: await GetIntAsync(LibraryPolicy.Keys.MaximumRenewals, 2, ct),
            RenewalDays: await GetIntAsync(LibraryPolicy.Keys.RenewalDays, 14, ct),
            FinePerDay: await GetDecimalAsync(LibraryPolicy.Keys.FinePerDay, 20.00m, ct),
            Currency: await GetStringAsync(LibraryPolicy.Keys.FineCurrency, "PKR", ct),
            GracePeriodDays: await GetIntAsync(LibraryPolicy.Keys.FineGracePeriodDays, 0, ct),
            MaximumOutstandingFine: await GetDecimalAsync(LibraryPolicy.Keys.MaximumOutstandingFine, 500m, ct));
    }
}
