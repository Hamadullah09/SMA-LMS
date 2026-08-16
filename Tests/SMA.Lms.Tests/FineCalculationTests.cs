using Library_Management_system.Application.Circulation;
using Library_Management_system.Application.Policies;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Fine and due-date rules (specification sections 22, 23, 63).
///
/// These replace the inherited hardcoded FinePerLateDay = 1.00m constant, so the arithmetic is
/// worth pinning down precisely - a wrong fine is charged to a real student.
/// </summary>
public class FineCalculationTests
{
    private static LoanPolicySnapshot Policy(
        decimal finePerDay = 20.00m,
        int graceDays = 0,
        int maxLoanDays = 30,
        int defaultLoanDays = 14) =>
        new(
            MaximumLoanDays: maxLoanDays,
            DefaultLoanDays: defaultLoanDays,
            MaximumBooksPerStudent: 5,
            MaximumOverdueBooks: 2,
            MaximumRenewals: 2,
            RenewalDays: 14,
            FinePerDay: finePerDay,
            Currency: "PKR",
            GracePeriodDays: graceDays,
            MaximumOutstandingFine: 500m);

    [Fact]
    public void Returned_before_due_date_incurs_no_fine()
    {
        var (days, fine) = CirculationService.CalculateFine(
            dueUtc: new DateTime(2026, 8, 15),
            returnedUtc: new DateTime(2026, 8, 10),
            Policy());

        Assert.Equal(0, days);
        Assert.Equal(0m, fine);
    }

    [Fact]
    public void Returned_on_the_due_date_is_not_late()
    {
        // A book due Monday and handed back Monday evening is on time.
        var (days, fine) = CirculationService.CalculateFine(
            dueUtc: new DateTime(2026, 8, 15, 9, 0, 0),
            returnedUtc: new DateTime(2026, 8, 15, 23, 30, 0),
            Policy());

        Assert.Equal(0, days);
        Assert.Equal(0m, fine);
    }

    [Fact]
    public void Five_days_late_charges_five_days()
    {
        // The worked example from specification section 23: due 10 Aug, returned 15 Aug.
        var (days, fine) = CirculationService.CalculateFine(
            dueUtc: new DateTime(2026, 8, 10),
            returnedUtc: new DateTime(2026, 8, 15),
            Policy(finePerDay: 20.00m));

        Assert.Equal(5, days);
        Assert.Equal(100.00m, fine);
    }

    [Fact]
    public void Fine_uses_configured_rate_not_a_hardcoded_one()
    {
        var (_, cheap) = CirculationService.CalculateFine(
            new DateTime(2026, 8, 10), new DateTime(2026, 8, 13), Policy(finePerDay: 5m));
        var (_, dear) = CirculationService.CalculateFine(
            new DateTime(2026, 8, 10), new DateTime(2026, 8, 13), Policy(finePerDay: 50m));

        Assert.Equal(15m, cheap);
        Assert.Equal(150m, dear);
    }

    [Fact]
    public void Grace_period_suppresses_the_fine_but_still_reports_lateness()
    {
        // Three days late with a three-day grace: recorded as late, charged nothing.
        var (days, fine) = CirculationService.CalculateFine(
            new DateTime(2026, 8, 10), new DateTime(2026, 8, 13), Policy(graceDays: 3));

        Assert.Equal(3, days);
        Assert.Equal(0m, fine);
    }

    [Fact]
    public void Only_days_beyond_the_grace_period_are_charged()
    {
        var (days, fine) = CirculationService.CalculateFine(
            new DateTime(2026, 8, 10), new DateTime(2026, 8, 15), Policy(finePerDay: 20m, graceDays: 3));

        Assert.Equal(5, days);
        Assert.Equal(40m, fine);   // 5 late, 2 chargeable
    }

    [Fact]
    public void Fine_grows_by_exactly_one_day_rate_per_extra_day()
    {
        var policy = Policy(finePerDay: 20m);
        var due = new DateTime(2026, 8, 10);

        var (_, day1) = CirculationService.CalculateFine(due, due.AddDays(1), policy);
        var (_, day2) = CirculationService.CalculateFine(due, due.AddDays(2), policy);

        Assert.Equal(20m, day1);
        Assert.Equal(day1 + 20m, day2);
    }

    [Fact]
    public void Transaction_number_matches_the_documented_format()
    {
        // Specification section 41: SMA-LIB-2026-000123
        Assert.Equal("SMA-LIB-2026-000123",
            CirculationService.BuildTransactionNumber(new DateTime(2026, 8, 15), 123));
    }

    [Fact]
    public void Transaction_number_uses_the_issue_year()
    {
        Assert.StartsWith("SMA-LIB-2027-",
            CirculationService.BuildTransactionNumber(new DateTime(2027, 1, 1), 1));
    }
}
