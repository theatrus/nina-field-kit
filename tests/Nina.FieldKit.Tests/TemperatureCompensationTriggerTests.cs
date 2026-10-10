using System.IO;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class TemperatureCompensationTriggerTests {
    private sealed class Rig {
        public readonly FocuserInfo Info = new() { Connected = true, Temperature = 20, Position = 10000 };
        public readonly SafetyMonitorInfo SafetyInfo = new() { Connected = true, IsSafe = true };
        public readonly Mock<IFocuserMediator> Focuser = new(MockBehavior.Strict);
        public readonly Mock<ISafetyMonitorMediator> Safety = new(MockBehavior.Strict);
        public readonly TemperatureCompensationTrigger Trigger;
        public IFocuserConsumer? Consumer;
        public Rig(double? slope = 40) {
            Focuser.Setup(x => x.GetInfo()).Returns(Info);
            Focuser.Setup(x => x.RegisterConsumer(It.IsAny<IFocuserConsumer>())).Callback<IFocuserConsumer>(c => Consumer = c);
            Focuser.Setup(x => x.RemoveConsumer(It.IsAny<IFocuserConsumer>()));
            Safety.Setup(x => x.GetInfo()).Returns(SafetyInfo);
            Trigger = new(Focuser.Object, Safety.Object) { StepsPerDegree = slope };
        }
        public void MoveTo(int position) => Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(
            It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).ReturnsAsync(position);
        public Task Execute(CancellationToken token = default) => Trigger.Execute(null!, null!, token);
        public bool AfterFrame() => Trigger.ShouldTriggerAfter(Exposure(), null!);
    }
    private static ISequenceItem Exposure(string type = "LIGHT", SequenceEntityStatus status = SequenceEntityStatus.FINISHED) {
        var exposure = new Mock<ISequenceItem>();
        exposure.As<IExposureItem>().SetupGet(x => x.ImageType).Returns(type);
        exposure.SetupGet(x => x.Status).Returns(status);
        return exposure.Object;
    }
    [Fact] public async Task NewTriggerNeedsAnExplicitSlope() {
        var rig = new Rig(null);
        Assert.Null(rig.Trigger.StepsPerDegree);
        Assert.False(rig.Trigger.Validate());
        Assert.False(rig.AfterFrame());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Execute());
        rig.Focuser.Verify(x => x.MoveFocuserByTemperatureRelative(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidSlopePreventsMovement(double slope) {
        var rig = new Rig(slope);
        Assert.False(rig.Trigger.Validate());
        Assert.False(rig.AfterFrame());
        Assert.Contains("slope", Assert.Single(rig.Trigger.Issues));
    }
    [Theory]
    [InlineData("DARK")] [InlineData("FLAT")] [InlineData("BIAS")]
    public void CalibrationFramesNeverTrigger(string type) {
        var rig = new Rig();
        Assert.False(rig.Trigger.ShouldTriggerAfter(Exposure(type), Exposure()));
        rig.Focuser.VerifyNoOtherCalls();
        rig.Safety.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(SequenceEntityStatus.CREATED)] [InlineData(SequenceEntityStatus.RUNNING)]
    [InlineData(SequenceEntityStatus.FAILED)] [InlineData(SequenceEntityStatus.SKIPPED)]
    public void IncompleteOrFailedFramesNeverTrigger(SequenceEntityStatus status) {
        var rig = new Rig();
        Assert.False(rig.Trigger.ShouldTriggerAfter(Exposure(status: status), null!));
        rig.Focuser.VerifyNoOtherCalls();
    }
    [Fact] public void OnlyRunsAfterCompletedLightFramesIncludingTheLastFrame() {
        var rig = new Rig();
        Assert.False(rig.Trigger.ShouldTrigger(null!, Exposure()));
        Assert.False(rig.Trigger.ShouldTriggerAfter(null!, Exposure()));
        Assert.False(rig.Trigger.ShouldTriggerAfter(Mock.Of<ISequenceItem>(), Exposure()));
        Assert.True(rig.AfterFrame());
    }
    [Theory] [InlineData(40)] [InlineData(-40)]
    public async Task PassesSignedSlopeAndLatestFocuserTemperatureToNina(double slope) {
        var rig = new Rig(slope);
        Assert.True(rig.AfterFrame());
        rig.Info.Temperature = 19.75;
        using var stop = new CancellationTokenSource();
        rig.Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(19.75, slope, stop.Token)).ReturnsAsync(9990);
        await rig.Execute(stop.Token);
        rig.Focuser.Verify(x => x.MoveFocuserByTemperatureRelative(19.75, slope, stop.Token), Times.Once);
        Assert.Contains("-10 steps", rig.Trigger.LastResult);
        Assert.False(rig.AfterFrame());
        rig.Info.Temperature = 19.5;
        Assert.True(rig.AfterFrame());
    }
    [Fact] public async Task UnsafeMonitorBlocksDecisionAndExecution() {
        var rig = new Rig();
        Assert.True(rig.AfterFrame());
        rig.SafetyInfo.IsSafe = false;
        Assert.False(rig.AfterFrame());
        await rig.Execute();
        Assert.Equal("Deferred: safety monitor unsafe", rig.Trigger.LastResult);
    }
    [Fact] public void DisconnectedSafetyMonitorDoesNotBlockCompensation() {
        var rig = new Rig();
        rig.SafetyInfo.Connected = false;
        rig.SafetyInfo.IsSafe = false;
        Assert.True(rig.AfterFrame());
    }
    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public async Task MovingOrSettlingFocuserIsDeferred(bool moving, bool settling) {
        var rig = new Rig();
        rig.Info.IsMoving = moving;
        rig.Info.IsSettling = settling;
        Assert.False(rig.AfterFrame());
        await rig.Execute();
        Assert.Equal("Deferred: focuser busy", rig.Trigger.LastResult);
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public async Task UnavailableTemperatureNeverMoves(double temperature) {
        var rig = new Rig();
        rig.Info.Temperature = temperature;
        Assert.False(rig.Trigger.Validate());
        Assert.False(rig.AfterFrame());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Execute());
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task HardwareCompensationOrDisconnectionPreventsMovement(bool hardwareCompensation) {
        var rig = new Rig();
        rig.Info.TempComp = hardwareCompensation;
        rig.Info.Connected = hardwareCompensation;
        Assert.False(rig.Trigger.Validate());
        Assert.False(rig.AfterFrame());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Execute());
    }
    [Fact] public async Task RechecksEquipmentBeforeExecuting() {
        var rig = new Rig();
        Assert.True(rig.AfterFrame());
        rig.Info.TempComp = true;
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Execute());
    }
    [Fact] public async Task FocusEventsInvalidateDuplicateTemperatureSuppression() {
        var rig = new Rig();
        rig.Trigger.Initialize();
        rig.MoveTo(10000);
        await rig.Execute();
        Assert.False(rig.AfterFrame());
        rig.Consumer!.UpdateEndAutoFocusRun(new AutoFocusInfo(21, 10040, "L", DateTime.UtcNow));
        Assert.True(rig.AfterFrame());
        await rig.Execute();
        Assert.False(rig.AfterFrame());
        rig.Consumer.UpdateUserFocused(new FocuserInfo { Temperature = 21 });
        Assert.True(rig.AfterFrame());
        await rig.Execute();
        rig.Consumer.UpdateDeviceInfo(new FocuserInfo { Connected = false });
        Assert.True(rig.AfterFrame());
    }
    [Fact] public async Task ChangingSlopeDuringAMoveDoesNotMarkNewSettingsAsApplied() {
        var rig = new Rig();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(20, 40, It.IsAny<CancellationToken>())).Returns(completion.Task);
        var move = rig.Execute();
        rig.Trigger.StepsPerDegree = -50;
        completion.SetResult(10000);
        await move;
        Assert.True(rig.AfterFrame());
    }
    [Fact] public async Task FailedMovementSurfacesToSequenceAndCanRetry() {
        var rig = new Rig();
        rig.MoveTo(-1);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Execute());
        Assert.StartsWith("Failed:", rig.Trigger.LastResult);
        Assert.True(rig.AfterFrame());
        rig.Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic focuser failure"));
        await Assert.ThrowsAsync<IOException>(() => rig.Execute());
        Assert.Contains("Synthetic focuser failure", rig.Trigger.LastResult);
    }
    [Fact] public async Task CancellationBeforeExecutionDoesNotMove() {
        var rig = new Rig();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Execute(stop.Token));
        Assert.Equal("Cancelled", rig.Trigger.LastResult);
        rig.Focuser.VerifyNoOtherCalls();
        rig.Safety.VerifyNoOtherCalls();
    }
    [Fact] public async Task CancelledMovementDoesNotConsumeTemperatureChange() {
        var rig = new Rig();
        rig.Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Execute());
        Assert.True(rig.AfterFrame());
    }
    [Fact] public async Task InitializeAndTeardownManageConsumerWithoutLeakingClones() {
        var rig = new Rig();
        rig.Trigger.Clone();
        rig.Focuser.Verify(x => x.RegisterConsumer(It.IsAny<IFocuserConsumer>()), Times.Never);
        rig.Trigger.Initialize();
        rig.Trigger.Initialize();
        rig.Focuser.Verify(x => x.RegisterConsumer(rig.Trigger), Times.Once);
        rig.MoveTo(10000);
        await rig.Execute();
        rig.Trigger.Teardown();
        rig.Trigger.Dispose();
        rig.Focuser.Verify(x => x.RemoveConsumer(rig.Trigger), Times.Once);
        rig.Trigger.Initialize();
        Assert.True(rig.AfterFrame());
        rig.Trigger.Teardown();
    }
    [Fact] public async Task CloneAndJsonRetainOnlyConfiguredSlope() {
        var rig = new Rig(-42.5);
        rig.Trigger.Name = "Temperature compensation after frame";
        rig.MoveTo(10000);
        await rig.Execute();
        var clone = Assert.IsType<TemperatureCompensationTrigger>(rig.Trigger.Clone());
        Assert.Equal(-42.5, clone.StepsPerDegree);
        Assert.Equal(rig.Trigger.Name, clone.Name);
        Assert.True(clone.ShouldTriggerAfter(Exposure(), null!));
        var json = JsonConvert.SerializeObject(rig.Trigger);
        Assert.DoesNotContain("LastResult", json);
        Assert.DoesNotContain("lastAppliedTemperature", json);
        var restored = new TemperatureCompensationTrigger(rig.Focuser.Object, rig.Safety.Object);
        JsonConvert.PopulateObject(json, restored);
        Assert.Equal(-42.5, restored.StepsPerDegree);
        Assert.True(restored.ShouldTriggerAfter(Exposure(), null!));
    }
    [Fact] public async Task NativeSequencerWaitsForCompensationBetweenFrames() {
        var rig = new Rig();
        var order = new List<string>();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Focuser.Setup(x => x.MoveFocuserByTemperatureRelative(20, 40, It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("compensate"); started.SetResult(); }).Returns(completion.Task);
        var first = new Mock<ISequenceItem>();
        first.As<IExposureItem>().SetupGet(x => x.ImageType).Returns("LIGHT");
        first.SetupProperty(x => x.Status, SequenceEntityStatus.CREATED);
        first.Setup(x => x.Run(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("first frame finished"); first.Object.Status = SequenceEntityStatus.FINISHED; }).Returns(Task.CompletedTask);
        var next = new Mock<ISequenceItem>();
        next.SetupProperty(x => x.Status, SequenceEntityStatus.CREATED);
        next.Setup(x => x.Run(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("next instruction"); next.Object.Status = SequenceEntityStatus.FINISHED; }).Returns(Task.CompletedTask);
        var parent = new SequentialContainer();
        var container = new SequentialContainer();
        container.AttachNewParent(parent);
        container.Items.Add(first.Object);
        container.Items.Add(next.Object);
        parent.Triggers.Add(rig.Trigger);
        rig.Trigger.AttachNewParent(parent);
        rig.Trigger.Initialize();
        var run = container.Execute(null!, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first frame finished", "compensate" }, order);
        completion.SetResult(10000);
        await run;
        Assert.Equal(new[] { "first frame finished", "compensate", "next instruction" }, order);
        rig.Trigger.Teardown();
    }
}
