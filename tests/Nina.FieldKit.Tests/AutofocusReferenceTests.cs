using System.IO;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model;
using NINA.WPF.Base.Utility.AutoFocus;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class AutofocusReferenceTests {
    private sealed class Rig {
        public Guid ProfileId = Guid.NewGuid();
        public string WheelId = "wheel-A";
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.Zero));
        public readonly FocuserInfo Info = new() { Connected = true, DeviceId = "focuser-A", Position = 10000, Temperature = 10 };
        public readonly FilterWheelInfo Wheel = new() { Connected = true, SelectedFilter = new FilterInfo { Name = "L", Position = 0 } };
        public readonly CameraInfo CameraInfo = new() { Connected = true };
        public readonly SafetyMonitorInfo SafetyInfo = new() { Connected = true, IsSafe = true };
        public readonly List<ImageHistoryPoint> Points = new();
        public readonly Dictionary<string, string> Data = new();
        public readonly Mock<IProfileService> Profiles = new() { DefaultValue = DefaultValue.Mock };
        public readonly Mock<IImageHistoryVM> History = new(MockBehavior.Strict);
        public readonly Mock<IFocuserMediator> Focuser = new(MockBehavior.Strict);
        public readonly Mock<IFilterWheelMediator> Filters = new(MockBehavior.Strict);
        public readonly Mock<ICameraMediator> Camera = new(MockBehavior.Strict);
        public readonly Mock<ISafetyMonitorMediator> Safety = new(MockBehavior.Strict);
        public readonly Mock<IFocuser> Device = new(MockBehavior.Strict);
        public readonly Mock<IPluginOptionsAccessor> Options = new(MockBehavior.Strict);
        public readonly NinaAutofocusReferenceService Service;
        public Rig() {
            Profiles.SetupGet(x => x.ActiveProfile.Id).Returns(() => ProfileId);
            Profiles.SetupGet(x => x.ActiveProfile.FilterWheelSettings.Id).Returns(() => WheelId);
            History.SetupGet(x => x.AutoFocusPoints).Returns(() => new AsyncObservableCollection<ImageHistoryPoint>(Points));
            Focuser.Setup(x => x.GetInfo()).Returns(Info);
            Focuser.Setup(x => x.GetDevice()).Returns(Device.Object);
            Device.SetupGet(x => x.MaxStep).Returns(200000);
            Focuser.Setup(x => x.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int position, CancellationToken token) => { token.ThrowIfCancellationRequested(); Info.Position = position; return Task.FromResult(position); });
            Filters.Setup(x => x.GetInfo()).Returns(Wheel);
            Camera.Setup(x => x.GetInfo()).Returns(CameraInfo);
            Safety.Setup(x => x.GetInfo()).Returns(SafetyInfo);
            Options.Setup(x => x.GetValueString(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string key, string fallback) => Data.GetValueOrDefault(key, fallback));
            Options.Setup(x => x.SetValueString(It.IsAny<string>(), It.IsAny<string>()))
                .Callback((string key, string value) => Data[key] = value);
            Service = NewService();
        }
        public NinaAutofocusReferenceService NewService() => new(Profiles.Object, History.Object, Focuser.Object,
            Filters.Object, Camera.Object, Safety.Object, Options.Object, Clock);
        public void Autofocus(double position = 10000, double temperature = 10, string filter = "L", DateTimeOffset? timestamp = null) {
            var point = new ImageHistoryPoint(0, "NONE");
            point.PopulateAFPoint(new AutoFocusReport {
                Timestamp = (timestamp ?? Clock.GetUtcNow()).UtcDateTime, Temperature = temperature, Filter = filter,
                CalculatedFocusPoint = new() { Position = position, Value = 1.7 }
            });
            Points.Add(point);
        }
        public AutofocusReference Save() {
            Autofocus();
            return Service.Save(null, null, CancellationToken.None);
        }
        public Task<AutofocusRestoreResult> Restore(double slope = -100, CancellationToken token = default) => Service.Restore(slope, null, token);
        public void NoMovement() => Focuser.Verify(x => x.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory] [InlineData(-100, 9, 10100)] [InlineData(-100, 20, 9000)] [InlineData(100, 9, 9900)] [InlineData(100, 20, 11000)]
    public async Task RestoresFromSavedAfTemperatureRatherThanEndOfNightPosition(double slope, double temperature, int expected) {
        var rig = new Rig();
        var saved = rig.Save();
        rig.Info.Position = 14000;
        rig.Info.Temperature = temperature;
        var result = await rig.Restore(slope);
        Assert.Equal(expected, result.Position);
        Assert.Equal(saved, result.Reference);
        rig.Focuser.Verify(x => x.MoveFocuser(expected, It.IsAny<CancellationToken>()), Times.Once);
        rig.Focuser.Verify(x => x.BroadcastSuccessfulAutoFocusRun(It.IsAny<AutoFocusInfo>()), Times.Never);
        rig.Focuser.Verify(x => x.BroadcastUserFocused(It.IsAny<FocuserInfo>()), Times.Never);
        Assert.Equal(1, rig.Data.Count);
    }
    [Fact] public async Task FirstNightSkipsWithoutMoving() {
        var rig = new Rig();
        var result = await rig.Restore();
        Assert.Null(result.Reference);
        Assert.Null(result.Position);
        rig.NoMovement();
    }
    [Fact] public async Task ReferencesSurviveRecreatingTheServiceAndDoNotLeakAcrossScopes() {
        var rig = new Rig();
        rig.Save();
        rig.Points.Clear();
        rig.Info.Temperature = 20;
        Assert.Equal(9000, (await rig.NewService().Restore(-100, null, default)).Position);
        rig.Wheel.SelectedFilter = new FilterInfo { Name = "R", Position = 1 };
        Assert.Null((await rig.Restore()).Reference);
        rig.Wheel.SelectedFilter = new FilterInfo { Name = "L", Position = 0 };
        rig.Info.DeviceId = "focuser-B";
        Assert.Null((await rig.Restore()).Reference);
        rig.Info.DeviceId = "focuser-A";
        rig.ProfileId = Guid.NewGuid();
        Assert.Null((await rig.Restore()).Reference);
    }
    [Fact] public async Task EachFilterRetainsItsOwnReference() {
        var rig = new Rig();
        rig.Save();
        rig.Wheel.SelectedFilter = new FilterInfo { Name = "R", Position = 1 };
        rig.Info.Position = 10500;
        rig.Autofocus(10500, 10, "R");
        rig.Service.Save(null, null, default);
        Assert.Equal(2, rig.Data.Count);
        rig.Info.Temperature = 20;
        Assert.Equal(9500, (await rig.Restore()).Position);
        rig.Wheel.SelectedFilter = new FilterInfo { Name = "L", Position = 0 };
        Assert.Equal(9000, (await rig.Restore()).Position);
    }
    [Fact] public async Task NoFilterWheelIsSupportedWhenNoWheelIsConfigured() {
        var rig = new Rig();
        rig.WheelId = "No_Device";
        rig.Wheel.Connected = false;
        rig.Autofocus(filter: "No Filter");
        var saved = rig.Service.Save(null, null, default);
        Assert.Equal(-1, saved.Scope.FilterPosition);
        Assert.Equal(string.Empty, saved.Scope.FilterName);
        Assert.Equal(10000, (await rig.Restore()).Position);
    }
    [Fact] public void PreflightDoesNotRequireAfOrASavedReference() {
        var rig = new Rig();
        Assert.True(new SaveAutofocusReference(rig.Service).Validate());
        Assert.True(new RestoreAutofocusReference(rig.Service) { StepsPerDegree = -100 }.Validate());
    }
    [Fact] public void MissingOrStaleAfCannotOverwriteThePreviousGoodReference() {
        var rig = new Rig();
        rig.Save();
        var saved = rig.Data.Single().Value;
        rig.Points.Clear();
        Assert.Throws<InvalidOperationException>(() => rig.Service.Save(null, null, default));
        rig.Autofocus(timestamp: rig.Clock.GetUtcNow() - TimeSpan.FromMinutes(31));
        Assert.Throws<InvalidOperationException>(() => rig.Service.Save(null, null, default));
        Assert.Equal(saved, rig.Data.Single().Value);
    }
    [Theory]
    [InlineData(double.NaN, 10)] [InlineData(double.PositiveInfinity, 10)] [InlineData(-1, 10)]
    [InlineData(10000, double.NaN)] [InlineData(10000, double.PositiveInfinity)] [InlineData(11000, 10)]
    public void InvalidAfPositionTemperatureOrMovedFocusIsNeverSaved(double position, double temperature) {
        var rig = new Rig();
        rig.Autofocus(position, temperature);
        Assert.Throws<InvalidOperationException>(() => rig.Service.Save(null, null, default));
        Assert.Empty(rig.Data);
    }
    [Fact] public void AfFilterMustMatchAndNewestResultIsNotReplacedByAnOlderGoodOne() {
        var rig = new Rig();
        rig.Autofocus();
        rig.Autofocus(filter: "R");
        Assert.Throws<InvalidOperationException>(() => rig.Service.Save(null, null, default));
        Assert.Empty(rig.Data);
    }
    [Fact] public async Task SaveActionRequiresAfFromThisSequenceInitialization() {
        var rig = new Rig();
        rig.Save();
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        var action = new SaveAutofocusReference(rig.Service);
        action.Initialize();
        var previous = rig.Data.Single().Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.Execute(null!, default));
        Assert.Equal(previous, rig.Data.Single().Value);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Autofocus();
        await action.Execute(null!, default);
        Assert.StartsWith("Saved 10000 steps", action.LastResult);
        Assert.NotEqual(previous, rig.Data.Single().Value);
    }
    [Fact] public async Task SaveRefusesAProfileChangeAfterInitialization() {
        var rig = new Rig();
        var action = new SaveAutofocusReference(rig.Service);
        action.Initialize();
        rig.ProfileId = Guid.NewGuid();
        rig.Autofocus();
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.Execute(null!, default));
        Assert.Empty(rig.Data);
    }
    [Theory] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task UnavailableCurrentTemperatureDoesNotFallBackToUnadjustedFocus(double temperature) {
        var rig = new Rig();
        rig.Save();
        rig.Info.Temperature = temperature;
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Theory] [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task InvalidSlopeNeverMoves(double slope) {
        var rig = new Rig();
        rig.Save();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Restore(slope));
        rig.NoMovement();
    }
    [Theory] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(false, false, true)]
    public async Task BusyFocuserAndHardwareCompensationPreventRestore(bool moving, bool settling, bool hardware) {
        var rig = new Rig();
        rig.Save();
        rig.Info.IsMoving = moving; rig.Info.IsSettling = settling; rig.Info.TempComp = hardware;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Fact] public async Task UnsafeMonitorOrAnExposurePreventsMovement() {
        var rig = new Rig();
        rig.Save();
        rig.SafetyInfo.IsSafe = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.SafetyInfo.IsSafe = true;
        rig.CameraInfo.IsExposing = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Fact] public async Task DisconnectedFocuserOrConfiguredWheelPreventsMovement() {
        var rig = new Rig();
        rig.Save();
        rig.Info.Connected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.Info.Connected = true;
        rig.Wheel.Connected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.Wheel.Connected = true;
        rig.Wheel.IsMoving = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Theory] [InlineData(100, -200)] [InlineData(100, 3000)] [InlineData(double.MaxValue, 20)]
    public async Task PredictionOutsideTravelOrIntegerRangeDoesNotMove(double slope, double temperature) {
        var rig = new Rig();
        rig.Save();
        rig.Info.Temperature = temperature;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Restore(slope));
        rig.NoMovement();
    }
    [Theory] [InlineData(-1)] [InlineData(9999)]
    public async Task FailedOrClampedMoveIsNotReportedAsRestored(int actual) {
        var rig = new Rig();
        rig.Save();
        var saved = rig.Data.Single().Value;
        rig.Focuser.Setup(x => x.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(actual);
        var action = new RestoreAutofocusReference(rig.Service) { StepsPerDegree = -100 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.Execute(null!, default));
        Assert.StartsWith("Failed:", action.LastResult);
        Assert.Equal(saved, rig.Data.Single().Value);
    }
    [Fact] public async Task CancellationAndLateSuccessLeaveSavedReferenceIntact() {
        var rig = new Rig();
        rig.Save();
        var saved = rig.Data.Single().Value;
        using var stop = new CancellationTokenSource();
        rig.Focuser.Setup(x => x.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int p, CancellationToken _) => { stop.Cancel(); return Task.FromResult(p); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Restore(token: stop.Token));
        Assert.Equal(saved, rig.Data.Single().Value);
    }
    [Fact] public async Task ProfileChangeCancelsAnInProgressNativeMove() {
        var rig = new Rig();
        rig.Save();
        rig.Focuser.Setup(x => x.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int p, CancellationToken token) => {
                rig.Profiles.Raise(x => x.BeforeProfileChanging += null, EventArgs.Empty);
                await Task.Delay(Timeout.Infinite, token);
                return p;
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Restore());
    }
    [Fact] public async Task CorruptReferenceFailsWithoutMovingOrOverwritingIt() {
        var rig = new Rig();
        rig.Save();
        var key = rig.Data.Single().Key;
        rig.Data[key] = "{bad json";
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Restore());
        Assert.Equal("{bad json", rig.Data[key]);
        rig.NoMovement();
    }
    [Fact] public async Task MismatchedStoredIdentityCannotBeUsedEvenUnderTheExpectedKey() {
        var rig = new Rig();
        var saved = rig.Save();
        var key = rig.Data.Single().Key;
        rig.Data[key] = JsonConvert.SerializeObject(saved with { Scope = saved.Scope with { ProfileId = Guid.NewGuid() } });
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Theory] [InlineData("Temperature")] [InlineData("Position")] [InlineData("CapturedAt")]
    public async Task MissingStoredFieldsCannotSilentlyDefaultToZero(string field) {
        var rig = new Rig();
        rig.Save();
        var key = rig.Data.Single().Key;
        var json = Newtonsoft.Json.Linq.JObject.Parse(rig.Data[key]);
        json.Remove(field);
        rig.Data[key] = json.ToString();
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.Restore());
        rig.NoMovement();
    }
    [Fact] public async Task RestoreWaitsForNativeMovementAndKeepsTheSavedBaseline() {
        var rig = new Rig();
        var saved = rig.Save();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Focuser.Setup(x => x.MoveFocuser(10000, It.IsAny<CancellationToken>())).Returns(completion.Task);
        var action = new RestoreAutofocusReference(rig.Service) { StepsPerDegree = -100 };
        var run = action.Execute(null!, default);
        Assert.False(run.IsCompleted);
        completion.SetResult(10000);
        await run;
        Assert.StartsWith("Restored 10000 steps", action.LastResult);
        Assert.Equal(saved, JsonConvert.DeserializeObject<AutofocusReference>(rig.Data.Single().Value));
    }
    [Fact] public async Task NewRestoreRequiresExplicitSlopeAndCloneJsonDoNotContainSavedPosition() {
        var rig = new Rig();
        rig.Save();
        var action = new RestoreAutofocusReference(rig.Service);
        Assert.Null(action.StepsPerDegree);
        Assert.False(action.Validate());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => action.Execute(null!, default));
        rig.NoMovement();
        action.StepsPerDegree = -42.5;
        action.Attempts = 2;
        var clone = Assert.IsType<RestoreAutofocusReference>(action.Clone());
        Assert.Equal(-42.5, clone.StepsPerDegree);
        Assert.Equal(2, clone.Attempts);
        Assert.Equal(InstructionErrorBehavior.AbortOnError, clone.ErrorBehavior);
        var json = JsonConvert.SerializeObject(action);
        Assert.DoesNotContain("LastResult", json);
        Assert.DoesNotContain("CapturedAt", json);
        Assert.DoesNotContain("10000", json);
        var restored = new RestoreAutofocusReference(rig.Service);
        JsonConvert.PopulateObject(json, restored);
        Assert.Equal(-42.5, restored.StepsPerDegree);
    }
    [Fact] public async Task RestoreActionExplainsFirstNightAndDoesNotCreateAReference() {
        var rig = new Rig();
        var action = new RestoreAutofocusReference(rig.Service) { StepsPerDegree = -100 };
        await action.Execute(null!, default);
        Assert.Equal("No saved reference — run autofocus", action.LastResult);
        Assert.Empty(rig.Data);
        rig.NoMovement();
    }
    [Fact] public async Task SaveActionCancellationDoesNotWrite() {
        var rig = new Rig();
        rig.Autofocus();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SaveAutofocusReference(rig.Service).Execute(null!, stop.Token));
        Assert.Empty(rig.Data);
    }
    [Fact] public void PositionPredictionRoundsFractionalStepsDeterministically() {
        var reference = new AutofocusReference(new(Guid.NewGuid(), "A", -1, ""), 10000, 10, DateTimeOffset.UtcNow);
        Assert.Equal(10001, reference.PositionAt(9.995, -100));
        Assert.Equal(9999, reference.PositionAt(10.01, -100));
    }
}
