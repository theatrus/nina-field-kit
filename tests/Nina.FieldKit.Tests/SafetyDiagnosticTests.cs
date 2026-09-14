using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Plugin.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SafetyDiagnosticTests {
    [Fact] public void TraceExpiresUsingElapsedTimeAndNormalEventsRemainAvailable() {
        var clock = new FakeTimeProvider();
        var journal = new SafetyDiagnosticJournal(clock);
        Assert.Null(journal.Add("Poll", "hidden", traceOnly: true));
        journal.SetTrace(true);
        Assert.NotNull(journal.Add("Poll", "visible", traceOnly: true));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, journal.TraceRemaining);
        Assert.Null(journal.Add("Poll", "expired", traceOnly: true));
        Assert.NotNull(journal.Add("Unsafe", "transition"));
        Assert.Equal(2, journal.Snapshot().Length);
        journal.SetTrace(true);
        journal.SetTrace(false);
        Assert.Null(journal.Add("Poll", "stopped", traceOnly: true));
    }

    [Fact] public void JournalBoundsHistoryAndCannotInjectAdditionalLogLines() {
        var journal = new SafetyDiagnosticJournal();
        Parallel.For(0, 1000, i => journal.Add("Event", i.ToString()));
        Assert.Equal(SafetyDiagnosticJournal.Capacity, journal.Snapshot().Length);
        journal.Add("Event", "a\r\nb" + new string('x', 20000));
        var latest = journal.Snapshot()[^1];
        Assert.DoesNotContain('\n', latest.Details);
        Assert.DoesNotContain('\r', latest.Details);
        Assert.Equal(16000, latest.Details.Length);
    }

    [Fact] public void RetryActivityRemainsVisibleWithoutTrace() {
        using var device = new FieldKitSafetyMonitor(SafetyIntegrationTests.Profiles().Object);
        foreach (var name in new[] { "RetryScheduled", "CheckMissed", "CheckRecovered" }) {
            device.RecordDiagnostic(name, new { attempt = 2 });
            Assert.Contains(device.DiagnosticEvents, entry => entry.Event == name);
        }
        Assert.Equal(TimeSpan.Zero, device.TraceRemaining);
    }

    [Fact] public void CheckSummaryDistinguishesRetryFromExhaustedCheck() {
        var source = new EndpointSafetyState(SafetyStateTests.Options).Snapshot(0) with {
            CheckInProgress = true, Attempt = 1, NextRequestInSeconds = .5, LastPoll = SafetyStateTests.Failure
        };
        Assert.Contains("RETRY in 0.5 s", SafetySetupWindow.DescribeCheck(source, 3));
        Assert.Contains("not yet missed", SafetySetupWindow.DescribeCheck(source, 3));
        Assert.Contains("RETRYING", SafetySetupWindow.DescribeCheck(source with { Attempt = 2, NextRequestInSeconds = null }, 3));
        var exhausted = source with { CheckInProgress = false, Attempt = 3, FailedCycles = 1 };
        Assert.Contains("CHECK MISSED", SafetySetupWindow.DescribeCheck(exhausted, 3));
        Assert.Contains("Missed checks: 1", SafetySetupWindow.DescribeCheck(exhausted, 3));
        Assert.Contains("recovered on attempt 2", SafetySetupWindow.DescribeCheck(source with {
            CheckInProgress = false, Attempt = 2, LastPoll = SafetyStateTests.Safe(0)
        }, 3));
    }

    [Fact] public void MainStatusShowsRetryActivityAndClearsAfterNormalCheck() {
        var options = SafetyStateTests.Options;
        var source = new EndpointSafetyState(options).Snapshot(0) with {
            CheckInProgress = true, Attempt = 1, NextRequestInSeconds = .5
        };
        Assert.Contains("retry in 0.5 s (attempt 2 of 3)", SafetySetupWindow.DescribeRetryActivity([source], [options]));
        Assert.Contains("retrying (attempt 2 of 3)", SafetySetupWindow.DescribeRetryActivity(
            [source with { Attempt = 2, NextRequestInSeconds = null }], [options]));
        Assert.Equal("", SafetySetupWindow.DescribeRetryActivity(
            [source with { CheckInProgress = false, LastPoll = SafetyStateTests.Safe(0) }], [options]));
    }

    [Fact] public void SavedConfigurationReportOmitsEndpointUrlAndRetainsIdentity() {
        using var device = new FieldKitSafetyMonitor(SafetyIntegrationTests.Profiles().Object);
        device.SaveConfiguration(new() { Endpoints = [new() { Label = "Weather", BaseUrl = "https://private.example/secret-path" }] }, device.ProfileIdentity);
        device.RecordDiagnostic("PollCompleted", new { reason = "quiet poll" });
        var report = device.ExportDiagnosticReport();
        Assert.Contains("ConfigurationSaved", report);
        Assert.Contains("Weather", report);
        Assert.DoesNotContain("private.example", report);
        Assert.DoesNotContain("secret-path", report);
        Assert.DoesNotContain("quiet poll", report);
        device.SetDiagnosticTrace(true);
        device.RecordDiagnostic("PollCompleted", new { reason = "traced poll" });
        Assert.Contains("traced poll", device.ExportDiagnosticReport());
    }
}
