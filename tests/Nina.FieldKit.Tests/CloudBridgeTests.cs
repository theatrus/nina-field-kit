using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Core.Clouds;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Plugin.Clouds;
using Nina.FieldKit.Plugin.Safety;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class CloudBridgeTests {
    internal sealed class Rig : IDisposable {
        public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 18, 20, 0, 0, TimeSpan.Zero));
        public readonly Mock<NINA.Profile.Interfaces.IProfileService> Profiles = SafetyIntegrationTests.Profiles();
        public readonly Mock<IImageSaveMediator> Images = new();
        public readonly Mock<IGuiderMediator> Guider = new();
        public readonly Mock<IGuider> GuideDevice = new();
        public readonly Mock<ICameraMediator> Camera = new();
        public readonly Mock<IFilterWheelMediator> Filters = new();
        public readonly Mock<ITelescopeMediator> Telescope = new();
        public readonly Mock<IImagingMediator> Imaging = new();
        public readonly Mock<IDomeMediator> Dome = new();
        public readonly Mock<IDomeFollower> Follower = new();
        public readonly Mock<ISafetyMonitorMediator> Safety = new();
        public readonly Mock<ISequenceRootContainer> Root = new();
        public readonly Mock<IDeepSkyObjectContainer> Context = new();
        public readonly Mock<ISequenceContainer> TargetArea = new();
        public readonly Coordinates Coordinates = new(5, 30, Epoch.J2000, Coordinates.RAType.Hours);
        public string GuideState = "Guiding";
        public readonly CameraInfo CameraInfo = new() { Connected = true };
        public readonly CloudGuardService Service;
        public readonly FieldKitSafetyMonitor Monitor;
        public readonly CloudGuardTrigger Trigger;
        public Rig(double morningSeconds = 28800) {
            Mock.Get(Profiles.Object.ActiveProfile).SetupGet(x => x.Id).Returns(Guid.NewGuid());
            Camera.Setup(x => x.GetInfo()).Returns(CameraInfo);
            Guider.Setup(x => x.GetInfo()).Returns(new GuiderInfo { Connected = true });
            Guider.Setup(x => x.GetDevice()).Returns(GuideDevice.Object);
            GuideDevice.SetupGet(x => x.State).Returns(() => GuideState);
            Filters.Setup(x => x.GetInfo()).Returns(new FilterWheelInfo { Connected = true, SelectedFilter = new FilterInfo { Name = "L", Position = 0 } });
            Telescope.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true });
            var input = new InputTarget(Angle.Zero, Angle.Zero, null) { TargetName = "Test target" };
            input.DeepSkyObject = Mock.Of<NINA.Astrometry.Interfaces.IDeepSkyObject>();
            input.InputCoordinates.Coordinates = Coordinates;
            Context.SetupGet(x => x.Target).Returns(input);
            Context.SetupGet(x => x.Parent).Returns(Root.Object);
            Root.SetupGet(x => x.Items).Returns(new List<ISequenceItem> { Mock.Of<ISequenceContainer>(), TargetArea.Object, Mock.Of<ISequenceContainer>() });
            Service = new(Profiles.Object, Images.Object, Guider.Object, Camera.Object, Filters.Object, Telescope.Object,
                Imaging.Object, Dome.Object, Follower.Object, Safety.Object, Time, morningCalculator: _ => Time.GetUtcNow().AddSeconds(morningSeconds));
            Service.SaveOptions(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 100 }, Profiles.Object.ActiveProfile);
            Monitor = new(Profiles.Object, Service);
            Monitor.SaveConfiguration(new() { IncludeCloudGuard = true }, Monitor.ProfileIdentity);
            Monitor.Connect(default).GetAwaiter().GetResult();
            Safety.Setup(x => x.GetDevice()).Returns(Monitor);
            Safety.Setup(x => x.GetInfo()).Returns(() => new SafetyMonitorInfo { Connected = true, IsSafe = Monitor.IsSafe });
            Trigger = new(Service) { Response = CloudResponse.ReportUnsafe, Parent = Context.Object, Name = "Cloud guard" };
        }
        public ISequenceItem Exposure() {
            var item = new Mock<ISequenceItem>(); item.As<IExposureItem>().SetupGet(x => x.ImageType).Returns("LIGHT");
            item.SetupGet(x => x.Parent).Returns(Context.Object); return item.Object;
        }
        public async Task Arm() {
            Trigger.Initialize();
            await Service.MonitoringStarted.WaitAsync(TimeSpan.FromSeconds(3));
            Trigger.ShouldTrigger(null!, Exposure());
        }
        public void Frame(int stars, string type = "LIGHT", string filter = "L", DateTimeOffset? started = null) {
            var meta = new ImageMetaData(); meta.Image.ImageType = type; meta.Image.ExposureTime = 60;
            meta.Image.ExposureStart = (started ?? Time.GetUtcNow()).UtcDateTime; meta.Target.Coordinates = Coordinates;
            var analysis = new Mock<IStarDetectionAnalysis>(); analysis.SetupGet(x => x.DetectedStars).Returns(stars);
            var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Gray16, null, new byte[2048], 64); bitmap.Freeze();
            Images.Raise(x => x.ImageSaved += null, new ImageSavedEventArgs { MetaData = meta, Filter = filter, StarDetectionAnalysis = analysis.Object, Image = bitmap });
        }
        public void Clouds() { for (var i = 0; i < 3; i++) Frame(100); for (var i = 0; i < 3; i++) Frame(10); }
        public void Dispose() { Trigger.Teardown(); Monitor.Dispose(); Service.Dispose(); }
    }
    [Fact] public async Task ReportUnsafeUsesActualImageEventsAndNeverCommandsEquipment() {
        using var rig = new Rig();
        Assert.False(rig.Monitor.IsSafe);
        await rig.Arm();
        Assert.True(rig.Trigger.Validate()); Assert.True(rig.Monitor.IsSafe);
        rig.Clouds(); Assert.False(rig.Monitor.IsSafe);
        Assert.False(rig.Trigger.ShouldTrigger(null!, rig.Exposure()));
        Assert.Contains("stars 10/100", rig.Trigger.LastResult);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(rig.Monitor.IsSafe); Assert.Equal(CloudPhase.RetryAllowed, rig.Service.Snapshot.Phase);
        rig.Frame(100); rig.Frame(100);
        Assert.Equal(CloudPhase.Monitoring, rig.Service.Snapshot.Phase);
        rig.Telescope.Verify(x => x.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Telescope.Verify(x => x.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Guider.Verify(x => x.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact] public async Task CalibrationDifferentFilterAndLateFramesCannotClearClouds() {
        using var rig = new Rig(); await rig.Arm();
        for (var i = 0; i < 5; i++) { rig.Frame(0, "DARK"); rig.Frame(0, filter: "Ha"); }
        Assert.True(rig.Monitor.IsSafe);
        rig.Clouds(); var beforeHold = rig.Time.GetUtcNow(); rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Frame(100, started: beforeHold); rig.Frame(100, started: beforeHold);
        Assert.Equal(CloudPhase.RetryAllowed, rig.Service.Snapshot.Phase);
        rig.Frame(10); Assert.False(rig.Monitor.IsSafe);
    }
    [Fact] public async Task FailureEventsAndGuidingLossFeedTheSameCloudState() {
        using var rig = new Rig(); await rig.Arm();
        var center = new Center(rig.Profiles.Object, rig.Telescope.Object, rig.Imaging.Object, rig.Filters.Object,
            rig.Guider.Object, rig.Dome.Object, rig.Follower.Object, new NINA.PlateSolving.PlateSolverFactoryProxy(), new NINA.Core.Utility.WindowService.WindowServiceFactory());
        center.AttachNewParent(rig.Context.Object);
        rig.Root.Raise(x => x.FailureEvent += null, rig.Root.Object, new SequenceEntityFailureEventArgs(center, new Exception("first")));
        Assert.True(rig.Monitor.IsSafe);
        rig.Root.Raise(x => x.FailureEvent += null, rig.Root.Object, new SequenceEntityFailureEventArgs(center, new Exception("second")));
        Assert.False(rig.Monitor.IsSafe);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.GuideState = "LostLock";
        rig.Service.CheckBoundary(rig.Context.Object);
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        rig.Service.CheckBoundary(rig.Context.Object);
        Assert.False(rig.Monitor.IsSafe);
        Assert.Contains("Guiding", rig.Service.Snapshot.Reason);
    }
    [Fact] public async Task MorningExitSkipsTheImagingAreaWithoutAwaitingItself() {
        using var rig = new Rig(morningSeconds: 5); await rig.Arm(); rig.Clouds();
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        for (var i = 0; i < 100 && !rig.TargetArea.Invocations.Any(x => x.Method.Name == nameof(ISequenceItem.Skip)); i++) await Task.Delay(10);
        Assert.Equal(CloudPhase.Bailed, rig.Service.Snapshot.Phase);
        rig.TargetArea.Verify(x => x.Skip(), Times.Once);
        Assert.False(rig.Monitor.IsSafe);
    }
    [Fact] public async Task LifecycleProfileChangeAndCloneDoNotLeaveAFalseSafeInput() {
        using var rig = new Rig(); await rig.Arm();
        var clone = Assert.IsType<CloudGuardTrigger>(rig.Trigger.Clone());
        Assert.Equal(CloudResponse.ReportUnsafe, clone.Response);
        Assert.Equal("Cloud guard", clone.Name);
        Assert.DoesNotContain("LastResult", JsonConvert.SerializeObject(clone));
        Assert.Throws<InvalidOperationException>(() => rig.Trigger.Response = CloudResponse.ParkAndRetry);
        rig.Profiles.Raise(x => x.BeforeProfileChanging += null, EventArgs.Empty);
        Assert.False(rig.Monitor.IsSafe);
        Assert.Equal(CloudPhase.Disarmed, rig.Service.Snapshot.Phase);
    }
    [Fact] public async Task ParkModeRetainsPendingHoldAcrossALongExposure() {
        using var rig = new Rig();
        rig.Trigger.Response = CloudResponse.ParkAndRetry;
        await rig.Arm(); rig.Clouds();
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CloudPhase.RetryAllowed, rig.Service.Snapshot.Phase);
        Assert.True(rig.Trigger.ShouldTrigger(null!, rig.Exposure()));
    }
    [Fact] public async Task RetiredWatcherFailureCannotFaultANewlyArmedSequence() {
        using var rig = new Rig(); await rig.Arm();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        rig.Camera.Setup(x => x.GetInfo()).Returns(() => {
            if (Interlocked.Increment(ref first) == 1) {
                entered.TrySetResult(); release.Wait(); throw new InvalidOperationException("Retired watcher");
            }
            return rig.CameraInfo;
        });
        var advance = Task.Run(() => rig.Time.Advance(TimeSpan.FromSeconds(1)));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            rig.Trigger.Teardown(); await rig.Arm();
        } finally { release.Set(); }
        await advance.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(50);
        Assert.True(rig.Monitor.IsSafe);
        Assert.Equal(CloudPhase.Learning, rig.Service.Snapshot.Phase);
    }
    [Fact] public async Task BudgetExitSkipsTheEnclosingSchedulerRatherThanItsInnerInstructions() {
        using var rig = new Rig(); await rig.Arm();
        var scheduler = new NINA.Plugin.TargetScheduler.Sequencer.TargetSchedulerContainer();
        scheduler.AttachNewParent(rig.Root.Object);
        rig.Context.SetupGet(x => x.Parent).Returns(scheduler);
        Assert.Same(scheduler, CloudGuardService.FindScheduler(rig.Context.Object));
        rig.Service.Exit(rig.Context.Object, false);
        Assert.Equal(NINA.Core.Enum.SequenceEntityStatus.SKIPPED, scheduler.Status);
        rig.Context.Verify(x => x.Skip(), Times.Never);
        rig.TargetArea.Verify(x => x.Skip(), Times.Never);
    }
}
