using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Clouds;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Plugin.Clouds;
using Nina.FieldKit.Plugin.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class CloudSafetyIntegrationTests {
    private sealed class CloudInput : ICloudSafetyInput {
        private readonly CloudDetector detector;
        public readonly FakeTimeProvider Time = new();
        public CloudInput() { detector = new(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 100 }); detector.Arm(); }
        private double Now => (double)Time.GetTimestamp() / Time.TimestampFrequency;
        public CloudSnapshot Snapshot => detector.Snapshot(Now);
        public bool IsSafe => Snapshot.IsSafe;
        public string Status => Snapshot.Reason;
        public event Action? Changed;
        public void Trip() { detector.Trip("Cloud", Now); Changed?.Invoke(); }
        public void Disarm() { detector.Disarm(); Changed?.Invoke(); }
    }
    private static async Task WaitFor(Func<bool> condition) {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
    [Fact] public async Task CloudOnlyDeviceAllowsTimedTrialButDisarmIsUnsafe() {
        var clouds = new CloudInput();
        using var device = new FieldKitSafetyMonitor(SafetyIntegrationTests.Profiles().Object, clouds);
        device.SaveConfiguration(new() { IncludeCloudGuard = true }, device.ProfileIdentity);
        Assert.True(await device.Connect(default));
        Assert.True(device.IsSafe);
        clouds.Trip(); Assert.False(device.IsSafe);
        Assert.Contains("Cloud", device.Status);
        clouds.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(device.IsSafe);
        Assert.Contains("not yet confirmed", device.Status);
        clouds.Disarm(); Assert.False(device.IsSafe);
        device.Disconnect(); Assert.False(device.IsSafe);
    }
    [Fact] public async Task TimerNeverOverridesAnUnsafeExternalSource() {
        await using var server = new LocalAlpacaServer();
        server.SafetyResponse = _ => (200, false, null);
        var clouds = new CloudInput();
        using var device = new FieldKitSafetyMonitor(SafetyIntegrationTests.Profiles().Object, clouds);
        device.SaveConfiguration(new() { IncludeCloudGuard = true, Endpoints = [SafetyStateTests.Options with { BaseUrl = server.BaseUrl }] }, device.ProfileIdentity);
        Assert.True(await device.Connect(default));
        await WaitFor(() => device.GetSnapshot()?.Endpoints.Single().Phase == EndpointPhase.Unsafe);
        clouds.Trip(); clouds.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(clouds.IsSafe); Assert.False(device.IsSafe);
    }
    [Fact] public async Task MissingCloudModuleFailsClosedAndExistingOverrideStillWorksExplicitly() {
        using var device = new FieldKitSafetyMonitor(SafetyIntegrationTests.Profiles().Object);
        device.SaveConfiguration(new() { IncludeCloudGuard = true }, device.ProfileIdentity);
        Assert.True(await device.Connect(default));
        Assert.False(device.IsSafe);
        device.SetOutputOverride(SafetyOutputOverride.Safe, device.ProfileIdentity);
        Assert.True(device.IsSafe);
        device.SetOutputOverride(SafetyOutputOverride.Passthrough, device.ProfileIdentity);
        Assert.False(device.IsSafe);
    }
}
