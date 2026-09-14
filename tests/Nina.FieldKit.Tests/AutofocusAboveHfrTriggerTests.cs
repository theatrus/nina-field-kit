using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using NINA.Core.Model;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class AutofocusAboveHfrTriggerTests {
    private sealed class Service : IHfrAutofocusService {
        public HfrReading? Reading = new(3, "Image");
        public double Result = 1.6;
        public int Runs, Reads;
        public HfrReading? ReadLatest() { Reads++; return Reading; }
        public IList<string> Validate() => new List<string>();
        public Task<HfrReading?> RunAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Runs++;
            Reading = new(Result, "Autofocus fit");
            return Task.FromResult(Reading);
        }
    }
    private static ISequenceItem Exposure(string type) {
        var mock = new Mock<ISequenceItem>();
        mock.As<IExposureItem>().SetupGet(x => x.ImageType).Returns(type);
        return mock.Object;
    }
    [Theory]
    [InlineData(3, true)] [InlineData(1.7, false)] [InlineData(1.2, false)]
    [InlineData(0, false)] [InlineData(double.NaN, false)] [InlineData(double.PositiveInfinity, false)]
    public void FiresOnlyAboveFixedLimit(double value, bool expected) {
        var trigger = new AutofocusAboveHfrTrigger(new Service { Reading = new(value, "Image") });
        Assert.Equal(expected, trigger.ShouldTrigger(null!, Exposure("LIGHT")));
    }
    [Fact] public void MissingHistoryDoesNotTrigger() {
        Assert.False(new AutofocusAboveHfrTrigger(new Service { Reading = null }).ShouldTrigger(null!, Exposure("LIGHT")));
    }
    [Theory] [InlineData("DARK")] [InlineData("FLAT")] [InlineData("BIAS")]
    public void CalibrationExposuresDoNotTrigger(string type) {
        var service = new Service();
        Assert.False(new AutofocusAboveHfrTrigger(service).ShouldTrigger(null!, Exposure(type)));
        Assert.Equal(0, service.Reads);
    }
    [Fact] public void EndOfSequenceAndNonExposureDoNotTrigger() {
        var trigger = new AutofocusAboveHfrTrigger(new Service());
        Assert.False(trigger.ShouldTrigger(null!, null!));
        Assert.False(trigger.ShouldTrigger(null!, Mock.Of<ISequenceItem>()));
    }
    [Fact] public async Task UnsafeMonitorBlocksDecisionAndExecution() {
        var service = new Service();
        var trigger = new AutofocusAboveHfrTrigger(service, () => true);
        Assert.False(trigger.ShouldTrigger(null!, Exposure("LIGHT")));
        await trigger.Execute(null!, null!, CancellationToken.None);
        Assert.Equal(0, service.Runs);
    }
    [Fact] public async Task RunsOnceAndGoodAutofocusResultStopsFurtherTriggers() {
        var service = new Service();
        var trigger = new AutofocusAboveHfrTrigger(service);
        Assert.True(trigger.ShouldTrigger(null!, Exposure("LIGHT")));
        await trigger.Execute(null!, null!, CancellationToken.None);
        Assert.Equal(1, service.Runs);
        Assert.Contains("accepted", trigger.LastResult);
        Assert.False(trigger.ShouldTrigger(null!, Exposure("LIGHT")));
    }
    [Fact] public async Task HighAutofocusResultFailsWithoutInternalLoop() {
        var service = new Service { Result = 3 };
        var trigger = new AutofocusAboveHfrTrigger(service);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => trigger.Execute(null!, null!, CancellationToken.None));
        Assert.Equal(1, service.Runs);
        Assert.StartsWith("Failed:", trigger.LastResult);
    }
    [Fact] public async Task CancellationDoesNotRunAutofocus() {
        var service = new Service();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AutofocusAboveHfrTrigger(service).Execute(null!, null!, stop.Token));
        Assert.Equal(0, service.Runs);
    }
    [Fact] public void CloneAndJsonPreserveLimit() {
        var trigger = new AutofocusAboveHfrTrigger(new Service()) { MaximumHfr = 2.2, Name = "Autofocus Above HFR" };
        var clone = Assert.IsType<AutofocusAboveHfrTrigger>(trigger.Clone());
        Assert.Equal(2.2, clone.MaximumHfr);
        Assert.Equal(trigger.Name, clone.Name);
        var json = JsonConvert.SerializeObject(trigger);
        Assert.DoesNotContain("LastResult", json);
        var restored = new AutofocusAboveHfrTrigger(new Service());
        JsonConvert.PopulateObject(json, restored);
        Assert.Equal(2.2, restored.MaximumHfr);
        Assert.True(restored.ShouldTrigger(null!, Exposure("LIGHT")));
    }
}
