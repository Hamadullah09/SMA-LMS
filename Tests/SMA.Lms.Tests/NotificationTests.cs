using Library_Management_system.Application.Notifications;
using Library_Management_system.Rfid;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>Notification scheduling (specification sections 24, 52, 53).</summary>
public class NotificationTests
{
    [Theory]
    [InlineData("3,1", new[] { 1, 3 })]
    [InlineData("1,3,7,14", new[] { 1, 3, 7, 14 })]
    [InlineData(" 3 , 1 ", new[] { 1, 3 })]
    public void Reminder_day_lists_parse_and_sort(string raw, int[] expected)
    {
        Assert.Equal(expected, OverdueBackgroundService.ParseDayList(raw));
    }

    [Fact]
    public void Malformed_day_lists_degrade_instead_of_throwing()
    {
        // A bad policy value must not take the background service down.
        Assert.Equal([3], OverdueBackgroundService.ParseDayList("abc,3,,-1,0"));
        Assert.Empty(OverdueBackgroundService.ParseDayList("nonsense"));
    }

    [Fact]
    public void Duplicate_offsets_collapse_so_no_student_is_emailed_twice()
    {
        Assert.Equal([1, 3], OverdueBackgroundService.ParseDayList("3,1,3,1"));
    }
}

/// <summary>
/// The production guard on the RFID simulator (specification section 4G).
/// A simulator in a live library would accept any tag a caller invented.
/// </summary>
public class RfidRegistrationTests
{
    private static IConfiguration Config(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rfid:Provider"] = provider
            })
            .Build();

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SMA.Lms";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Simulator_is_refused_in_production()
    {
        var services = new ServiceCollection();
        var env = new FakeEnvironment { EnvironmentName = "Production" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddSmaRfid(Config("Simulator"), env));

        Assert.Contains("Simulator", ex.Message);
        Assert.Contains("must never run against a live library", ex.Message);
    }

    [Fact]
    public void Real_provider_is_accepted_in_production()
    {
        var services = new ServiceCollection();
        var env = new FakeEnvironment { EnvironmentName = "Production" };

        services.AddSmaRfid(Config("D2184"), env);   // must not throw
    }

    [Fact]
    public void Simulator_is_allowed_in_development()
    {
        var services = new ServiceCollection();
        var env = new FakeEnvironment { EnvironmentName = "Development" };

        services.AddSmaRfid(Config("Simulator"), env);   // must not throw
    }
}
