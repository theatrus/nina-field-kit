using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using Nina.FieldKit.Plugin.SkyFlats;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SkyFlatRotationsTests {
    internal sealed class History : INightRotationHistory {
        public FlatRotationScope Scope { get; set; } = new(Guid.NewGuid(), "camera-A", "rotator-A");
        public double[] Angles = [10, 190, 45];
        public FlatRotationPlan GetPlan(int nightOffset, double tolerance) => new(Scope, new DateTime(2026, 10, 18).AddDays(nightOffset), Angles);
    }
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class Probe(Action action) : SequenceItem {
        public int Started, Finished, Initialized, TornDown;
        public override object Clone() => new Probe(action) { Name = Name, Status = Status };
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        public override void SequenceBlockInitialize() => Initialized++;
        public override void SequenceBlockStarted() => Started++;
        public override void SequenceBlockFinished() => Finished++;
        public override void SequenceBlockTeardown() => TornDown++;
    }
    internal sealed class Rig {
        public readonly History History = new();
        public readonly Mock<IProfileService> Profiles = new() { DefaultValue = DefaultValue.Mock };
        public readonly Mock<IRotatorMediator> Rotator = new();
        public readonly Mock<ICameraMediator> Camera = new();
        public readonly Mock<ISafetyMonitorMediator> Safety = new();
        public readonly RotatorInfo Info = new() { Connected = true, DeviceId = "rotator-A", MechanicalPosition = 55 };
        public readonly CameraInfo CameraInfo = new() { Connected = true };
        public readonly SafetyMonitorInfo SafetyInfo = new() { Connected = true, IsSafe = true };
        public readonly List<string> Calls = new();
        public readonly SkyFlatRotationsContainer Container;
        public Rig() {
            Rotator.Setup(x => x.GetInfo()).Returns(Info);
            Rotator.Setup(x => x.GetTargetMechanicalPosition(It.IsAny<float>())).Returns((float angle) => angle);
            Rotator.Setup(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>())).Returns((float angle, CancellationToken token) => {
                token.ThrowIfCancellationRequested(); Calls.Add($"move:{angle}"); Info.MechanicalPosition = angle; return Task.FromResult(angle);
            });
            Camera.Setup(x => x.GetInfo()).Returns(CameraInfo); Safety.Setup(x => x.GetInfo()).Returns(SafetyInfo);
            Container = new(History, Profiles.Object, Rotator.Object, Camera.Object, Safety.Object) { Name = "Sky flats at captured rotations" };
        }
        public Probe AddProbe() {
            var probe = new Probe(() => Calls.Add($"flat:{Info.MechanicalPosition}")) { Name = "SkyFlats" };
            Container.Add(probe); return probe;
        }
    }
    [Fact] public async Task EachRotationMovesThenReplaysChildrenWithNativeBlockLifecycle() {
        var rig = new Rig(); var probe = rig.AddProbe();
        await rig.Container.Execute(null!, default);
        Assert.Equal(new[] { "move:10", "flat:10", "move:190", "flat:190", "move:45", "flat:45" }, rig.Calls);
        Assert.Equal(3, probe.Started); Assert.Equal(3, probe.Finished); Assert.Equal(3, probe.Initialized); Assert.Equal(3, probe.TornDown);
        Assert.Contains("3 captured rotations", rig.Container.LastResult);
        Assert.Equal(45, rig.Info.MechanicalPosition);
    }
    [Fact] public async Task NestedContainersRunAgainAndDisabledInstructionsStayDisabled() {
        var rig = new Rig(); var nested = new SequentialContainer();
        var probe = new Probe(() => rig.Calls.Add("nested")); nested.Add(probe); rig.Container.Add(nested);
        var disabled = rig.AddProbe(); disabled.Status = SequenceEntityStatus.DISABLED;
        await rig.Container.Execute(null!, default);
        Assert.Equal(3, rig.Calls.Count(x => x == "nested")); Assert.DoesNotContain(rig.Calls, call => call.StartsWith("flat:"));
        Assert.Equal(SequenceEntityStatus.DISABLED, disabled.Status);
    }
    [Theory] [InlineData("unsafe")] [InlineData("exposing")] [InlineData("moving")] [InlineData("disconnected")] [InlineData("wrong driver")]
    public async Task EquipmentGateBlocksBeforeAnyMoveOrFlat(string reason) {
        var rig = new Rig(); rig.AddProbe();
        if (reason == "unsafe") rig.SafetyInfo.IsSafe = false;
        if (reason == "exposing") rig.CameraInfo.IsExposing = true;
        if (reason == "moving") rig.Info.IsMoving = true;
        if (reason == "disconnected") rig.Info.Connected = false;
        if (reason == "wrong driver") rig.Info.DeviceId = "rotator-B";
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Empty(rig.Calls);
    }
    [Fact] public async Task AdjustedHalfTurnAndWrongReportedPositionCannotStartFlats() {
        var rig = new Rig(); rig.AddProbe();
        rig.Rotator.Setup(x => x.GetTargetMechanicalPosition(It.IsAny<float>())).Returns((float angle) => angle + 180);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default)); Assert.Empty(rig.Calls);
        rig = new Rig(); rig.AddProbe();
        rig.Rotator.Setup(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>())).ReturnsAsync(10);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.DoesNotContain(rig.Calls, x => x.StartsWith("flat:"));
    }
    [Fact] public async Task CancellationEvenIfNativeMoveReturnsNormallyDoesNotAdvanceOrRestore() {
        var rig = new Rig(); rig.AddProbe(); using var stop = new CancellationTokenSource();
        rig.Rotator.Setup(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>())).Returns((float angle, CancellationToken token) => {
            rig.Calls.Add("move"); stop.Cancel(); return Task.FromResult(float.NaN);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Container.Execute(null!, stop.Token));
        Assert.Equal(new[] { "move" }, rig.Calls);
    }
    [Fact] public async Task ProfileChangeDuringMoveCancelsBeforeChildrenAndNewAnglesWaitForAnotherRun() {
        var rig = new Rig(); rig.AddProbe();
        rig.Rotator.Setup(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>())).Returns((float angle, CancellationToken token) => {
            rig.Info.MechanicalPosition = angle; rig.Profiles.Raise(x => x.BeforeProfileChanging += null, EventArgs.Empty); return Task.FromResult(angle);
        });
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default)); Assert.Empty(rig.Calls);
        rig = new Rig(); var reference = rig; rig.Container.Add(new Probe(() => reference.History.Angles[1] = 300));
        await rig.Container.Execute(null!, default);
        Assert.Contains("move:190", rig.Calls); Assert.DoesNotContain("move:300", rig.Calls);
    }
    [Fact] public async Task AncestorConditionsStopTheNextRotationBeforeItMoves() {
        var rig = new Rig(); var allow = true;
        var parent = new Mock<ISequenceContainer>(); var conditionable = parent.As<IConditionable>();
        conditionable.Setup(x => x.GetConditionsSnapshot()).Returns(new[] { Mock.Of<ISequenceCondition>() });
        conditionable.Setup(x => x.CheckConditions(It.IsAny<ISequenceItem>(), It.IsAny<ISequenceItem>())).Returns(() => allow);
        rig.Container.AttachNewParent(parent.Object);
        rig.Container.Add(new Probe(() => { rig.Calls.Add("flat"); allow = false; }));
        await rig.Container.Execute(null!, default);
        Assert.Equal(new[] { "move:10", "flat" }, rig.Calls);
        Assert.Contains("condition", rig.Container.LastResult);
    }
    [Fact] public async Task EmptyNightOrDisabledBodyPerformsNoMoves() {
        var rig = new Rig(); rig.AddProbe(); rig.History.Angles = [];
        await rig.Container.Execute(null!, default); Assert.Empty(rig.Calls); Assert.Contains("skipped", rig.Container.LastResult);
        rig = new Rig(); var item = rig.AddProbe(); item.Status = SequenceEntityStatus.DISABLED;
        await rig.Container.Execute(null!, default); Assert.Empty(rig.Calls);
    }
    [Fact] public async Task AConfiguredDisconnectedSafetyMonitorBlocksMovement() {
        var rig = new Rig(); rig.AddProbe(); rig.SafetyInfo.Connected = false;
        rig.Profiles.SetupGet(x => x.ActiveProfile.SafetyMonitorSettings.Id).Returns("configured-monitor");
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Empty(rig.Calls);
    }
    [Fact] public async Task MoveTimeoutFailsBeforeChildrenAndDoesNotTryAnotherAngle() {
        var rig = new Rig(); rig.AddProbe(); rig.Container.MoveTimeoutSeconds = 5;
        rig.Rotator.Setup(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Returns(async (float angle, CancellationToken token) => { await Task.Delay(Timeout.Infinite, token); return angle; });
        var error = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Contains("timed out", error.Message);
        rig.Rotator.Verify(x => x.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(rig.Calls);
    }
    [Fact] public void CloneAndJsonPreserveNativeChildrenAndSettingsWithoutRuntimeState() {
        var rig = new Rig(); rig.Container.NightOffset = -1; rig.Container.ToleranceDegrees = .2; rig.Container.MoveTimeoutSeconds = 90;
        var original = rig.AddProbe(); rig.Container.PreviewCommand.Execute(null);
        var clone = Assert.IsType<SkyFlatRotationsContainer>(rig.Container.Clone());
        Assert.Equal(-1, clone.NightOffset); Assert.Equal(.2, clone.ToleranceDegrees); Assert.Equal(90, clone.MoveTimeoutSeconds);
        Assert.Equal(rig.Container.Name, clone.Name); Assert.Same(clone, Assert.Single(clone.Items).Parent); Assert.NotSame(original, clone.Items[0]);
        Assert.DoesNotContain("Night 2026", clone.LastResult);
        var factory = new Mock<NINA.Sequencer.ISequencerFactory>();
        factory.SetupGet(x => x.DateTimeProviders).Returns(new List<NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider>());
        factory.Setup(x => x.GetContainer<SkyFlatRotationsContainer>()).Returns(() => new SkyFlatRotationsContainer(rig.History, rig.Profiles.Object, rig.Rotator.Object, rig.Camera.Object, rig.Safety.Object));
        factory.Setup(x => x.GetItem<Probe>()).Returns(() => new Probe(() => { }));
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory.Object);
        var json = converter.Serialize(rig.Container);
        Assert.Contains("NightOffset", json); Assert.DoesNotContain("LastResult", json); Assert.DoesNotContain("PreviewCommand", json);
        var restored = Assert.IsType<SkyFlatRotationsContainer>(converter.Deserialize(json));
        Assert.Equal(-1, restored.NightOffset); Assert.Equal(.2, restored.ToleranceDegrees); Assert.Equal(90, restored.MoveTimeoutSeconds);
        Assert.Same(restored, Assert.Single(restored.Items).Parent);
        Assert.IsType<FlatRotationStrategy>(restored.Strategy);
    }
}
