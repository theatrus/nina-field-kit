using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Plugin.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class AscomSafetyClientTests {
    private sealed class Driver : ILocalSafetyDriver {
        public bool ConnectedValue;
        public bool Safe = true;
        public bool Disposed;
        public int Reads;
        public readonly List<(int Thread, ApartmentState Apartment, string Call)> Calls = new();
        public ManualResetEventSlim? Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Call(string name) => Calls.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), name));
        public bool Connected { get { Call("connected?"); return ConnectedValue; } set { Call("connect=" + value); ConnectedValue = value; } }
        public bool IsSafe { get { Call("safe?"); Interlocked.Increment(ref Reads); Entered.TrySetResult(); Block?.Wait(); return Safe; } }
        public int InterfaceVersion { get { Call("version"); return 1; } }
        public void Dispose() { Call("dispose"); Disposed = true; }
    }
    private static SafetyEndpointOptions Options => new() { Kind = SafetySourceKind.Ascom,
        AscomDriverId = "ASCOM.Test." + Guid.NewGuid().ToString("N"), ConnectionPolicy = ConnectionPolicy.Managed, RequestTimeoutSeconds = 1 };
    [Fact] public async Task AllDriverCallsAndCleanupStayOnOnePumpedSta() {
        var driver = new Driver(); var activations = new List<(int, ApartmentState)>();
        var client = new AscomSafetyClient(Options, factory: _ => { activations.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState())); return driver; });
        try {
            var result = await client.PollAsync(default);
            Assert.Equal(PollOutcome.Observation, result.Outcome);
            Assert.True(result.IsSafe);
            driver.Safe = false;
            Assert.False((await client.PollAsync(default)).IsSafe);
        } finally { client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(driver.Disposed);
        Assert.False(driver.ConnectedValue);
        Assert.All(driver.Calls, c => { Assert.Equal(activations.Single().Item1, c.Thread); Assert.Equal(ApartmentState.STA, c.Apartment); });
    }
    [Fact] public async Task ReadOnlyModeDoesNotConnectOrDisconnectAnExistingClient() {
        var driver = new Driver { ConnectedValue = true };
        var client = new AscomSafetyClient(Options with { ConnectionPolicy = ConnectionPolicy.ExternallyManaged }, factory: _ => driver);
        await client.PollAsync(default); client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(driver.Calls, c => c.Call.StartsWith("connect="));
        driver = new Driver();
        client = new AscomSafetyClient(Options with { ConnectionPolicy = ConnectionPolicy.ExternallyManaged }, factory: _ => driver);
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(0, driver.Reads);
        client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact] public async Task HungComDoesNotBlockCallerQueueMoreReadsOrAllowAReplacementConnection() {
        using var blocked = new ManualResetEventSlim();
        var driver = new Driver();
        var options = Options with { RequestTimeoutSeconds = 1 };
        var client = new AscomSafetyClient(options, factory: _ => driver);
        Assert.Equal(PollOutcome.Observation, (await client.PollAsync(default)).Outcome);
        driver.Block = blocked; driver.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousReads = driver.Reads;
        var first = client.PollAsync(default);
        await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PollOutcome.TransientFailure, (await first).Outcome);
        Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
        Assert.Equal(previousReads + 1, driver.Reads);
        client.Dispose();
        var activated = 0;
        var replacement = new AscomSafetyClient(options, factory: _ => { Interlocked.Increment(ref activated); return new Driver(); });
        try {
            Assert.Equal(PollOutcome.TransientFailure, (await replacement.PollAsync(default)).Outcome);
            Assert.Equal(0, activated);
            blocked.Set();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(PollOutcome.Observation, (await replacement.PollAsync(default)).Outcome);
            Assert.Equal(1, activated);
        } finally { blocked.Set(); client.Dispose(); replacement.Dispose(); await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    [Fact] public async Task CancelledAndLateReadsAreNeverReturnedAsFreshEvidence() {
        using var blocked = new ManualResetEventSlim();
        var driver = new Driver { Block = blocked };
        var client = new AscomSafetyClient(Options, factory: _ => driver);
        using var stop = new CancellationTokenSource();
        try {
            var read = client.PollAsync(stop.Token);
            await driver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            driver.Safe = false; blocked.Set();
            for (var i = 0; i < 100; i++) {
                var result = await client.PollAsync(default);
                if (result.Outcome == PollOutcome.Observation) { Assert.False(result.IsSafe); Assert.True(driver.Reads >= 2); return; }
                await Task.Delay(10);
            }
            Assert.Fail("Driver did not complete a fresh read after cancellation");
        } finally { blocked.Set(); client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    [Fact] public async Task DriverExceptionsNeverBecomeSafeAndDoNotExposeDriverText() {
        var client = new AscomSafetyClient(Options, factory: _ => throw new InvalidOperationException("private connection detail"));
        try {
            var result = await client.PollAsync(default);
            Assert.Equal(PollOutcome.TransientFailure, result.Outcome);
            Assert.Null(result.IsSafe);
            Assert.DoesNotContain("private", result.Reason);
        } finally { client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    [Fact] public void SourceValidationAndDuplicateIdentitySupportBothTransports() {
        var local = Options;
        local.Validate();
        Assert.Throws<ArgumentException>(() => (local with { AscomDriverId = "http://server/" }).Validate());
        Assert.Throws<ArgumentException>(() => new SafetyConfiguration { Endpoints = [local, local with { Id = Guid.NewGuid(), AscomDriverId = local.AscomDriverId.ToUpperInvariant() }] }.Freeze());
        Assert.Equal(2, new SafetyConfiguration { Endpoints = [local, new() { BaseUrl = "http://example.test" }] }.Freeze().Endpoints.Count);
        Assert.Empty(new SafetyConfiguration { IncludeCloudGuard = true }.Freeze().Endpoints);
        Assert.Throws<ArgumentException>(() => new SafetyConfiguration().Freeze());
    }
}
