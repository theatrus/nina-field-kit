using System.IO;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nina.FieldKit.Core;
using Nina.FieldKit.Plugin.SkyFlats;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class NightRotationHistoryTests {
    internal sealed class Rig : IDisposable {
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
        public readonly Mock<IProfileService> Profiles = new() { DefaultValue = DefaultValue.Mock };
        public readonly Mock<IImageSaveMediator> Images = new();
        public readonly Mock<IPluginOptionsAccessor> Options = new();
        public readonly Dictionary<string, string> Data = new();
        public readonly NightRotationHistory History;
        public Guid ProfileId = Guid.NewGuid();
        public string RotatorId = "rotator-A", CameraId = "camera-A";
        public int Writes;
        public Rig() {
            Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
            Profiles.SetupGet(x => x.ActiveProfile.Id).Returns(() => ProfileId);
            Profiles.SetupGet(x => x.ActiveProfile.RotatorSettings.Id).Returns(() => RotatorId);
            Profiles.SetupGet(x => x.ActiveProfile.CameraSettings.Id).Returns(() => CameraId);
            Options.Setup(x => x.GetValueString(It.IsAny<string>(), It.IsAny<string>())).Returns((string key, string fallback) => Data.GetValueOrDefault(key, fallback));
            Options.Setup(x => x.SetValueString(It.IsAny<string>(), It.IsAny<string>())).Callback((string key, string value) => { Writes++; Data[key] = value; });
            History = NewHistory();
        }
        public NightRotationHistory NewHistory() => new(Profiles.Object, Images.Object, Options.Object, Clock);
        public ImageMetaData Before(double mechanical, string type = "LIGHT", DateTimeOffset? started = null) {
            var meta = new ImageMetaData(); meta.Image.ImageType = type; meta.Image.ExposureStart = (started ?? Clock.GetUtcNow()).UtcDateTime;
            meta.Rotator.MechanicalPosition = mechanical; meta.Rotator.Position = 123; // A synced sky position is deliberately different.
            var image = new Mock<IImageData>(); image.SetupGet(x => x.MetaData).Returns(meta);
            Images.Raise(x => x.BeforeImageSaved += null, this, new BeforeImageSavedEventArgs(image.Object, Task.FromResult<IRenderedImage>(null!)));
            return meta;
        }
        public void Save(ImageMetaData meta) => Images.Raise(x => x.ImageSaved += null, new ImageSavedEventArgs { MetaData = meta });
        public void Frame(double mechanical, string type = "LIGHT", DateTimeOffset? started = null) => Save(Before(mechanical, type, started));
        public void Dispose() => History.Dispose();
    }
    [Fact] public void WrapDuplicatesDoNotCollapseOppositeMechanicalPositions() {
        Assert.Equal(new[] { 359.97, 180d, .2 }, FlatRotations.Distinct([359.97, .01, 180, 180.04, .2, 360], .1));
        Assert.Equal(new[] { 0d, 180 }, FlatRotations.Distinct([0, 360, 180], .1));
        Assert.Throws<ArgumentException>(() => FlatRotations.Distinct([double.NaN], .1));
        Assert.Throws<ArgumentException>(() => FlatRotations.Distinct([0], 0));
    }
    [Fact] public void ObservingNightStartsAtLocalNoonIncludingMidnightAndDstDates() {
        Assert.Equal(new DateTime(2026, 10, 18), FlatRotations.ObservingNight(new(2026, 10, 19, 0, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 18), FlatRotations.ObservingNight(new(2026, 10, 19, 11, 59, 59)));
        Assert.Equal(new DateTime(2026, 10, 19), FlatRotations.ObservingNight(new(2026, 10, 19, 12, 0, 0)));
        using var rig = new Rig(); rig.Clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        rig.Frame(25);
        Assert.Equal(new DateTime(2026, 10, 31), rig.History.GetPlan(0, .1).Night);
        Assert.Equal(25, Assert.Single(rig.History.GetPlan(0, .1).Angles));
    }
    [Fact] public void SavedLightsPersistMechanicalAnglesAndSeparateNightProfileAndEquipment() {
        using var rig = new Rig(); rig.Frame(12); rig.Frame(12.005); rig.Frame(192);
        Assert.Equal(2, rig.Writes);
        using var restored = rig.NewHistory();
        Assert.Equal(new[] { 12d, 192 }, restored.GetPlan(0, .1).Angles);
        Assert.DoesNotContain(123d, restored.GetPlan(0, .1).Angles);
        var original = rig.ProfileId; rig.ProfileId = Guid.NewGuid(); Assert.Empty(restored.GetPlan(0, .1).Angles); rig.ProfileId = original;
        rig.RotatorId = "rotator-B"; Assert.Empty(restored.GetPlan(0, .1).Angles); rig.RotatorId = "rotator-A";
        rig.CameraId = "camera-B"; Assert.Empty(restored.GetPlan(0, .1).Angles); rig.CameraId = "camera-A";
        rig.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Empty(restored.GetPlan(0, .1).Angles);
        Assert.Equal(new[] { 12d, 192 }, restored.GetPlan(-1, .1).Angles);
    }
    [Fact] public void CalibrationUnknownAnglesLateProfileImagesAndMissingCaptureScopeAreIgnored() {
        using var rig = new Rig();
        rig.Frame(0, "FLAT"); rig.Frame(10, "DARK"); rig.Frame(double.NaN); rig.Frame(-1); rig.Frame(361);
        rig.Frame(40, started: rig.Clock.GetUtcNow().AddMinutes(1));
        var late = rig.Before(55); rig.RotatorId = "rotator-B"; rig.Save(late); rig.RotatorId = "rotator-A";
        var unscoped = new ImageMetaData(); unscoped.Image.ImageType = "LIGHT"; unscoped.Image.ExposureStart = rig.Clock.GetUtcNow().UtcDateTime;
        unscoped.Rotator.MechanicalPosition = 90; rig.Save(unscoped);
        Assert.Empty(rig.History.GetPlan(0, .1).Angles);
        Assert.Equal(0, rig.Writes);
        rig.Frame(0); Assert.Equal(0, Assert.Single(rig.History.GetPlan(0, .1).Angles));
    }
    [Fact] public void CorruptHistoryAndRecordingFailuresBlockAnIncompletePlan() {
        using var rig = new Rig(); rig.Frame(15);
        var key = Assert.Single(rig.Data.Keys); rig.Data[key] = "{broken";
        Assert.Throws<InvalidDataException>(() => rig.History.GetPlan(0, .1));
        rig.Frame(20); // Observer errors must not fail NINA's image save.
        Assert.Throws<InvalidDataException>(() => rig.History.GetPlan(0, .1));
    }
    [Fact] public void DisposedObserverCannotRecordMoreFrames() {
        using var rig = new Rig(); rig.Frame(10); rig.History.Dispose(); rig.Frame(20);
        using var restored = rig.NewHistory(); Assert.Equal(10, Assert.Single(restored.GetPlan(0, .1).Angles));
    }
    [Fact] public void SaveQueueFromThePreviousProfileCannotRecordIntoTheNewProfile() {
        using var rig = new Rig(); var oldStart = rig.Clock.GetUtcNow();
        var inFlight = rig.Before(50);
        rig.Profiles.Raise(x => x.BeforeProfileChanging += null, EventArgs.Empty);
        rig.Clock.Advance(TimeSpan.FromSeconds(2)); rig.ProfileId = Guid.NewGuid();
        rig.Profiles.Raise(x => x.ProfileChanged += null, EventArgs.Empty);
        rig.Save(inFlight); rig.Frame(90, started: oldStart);
        Assert.Empty(rig.History.GetPlan(0, .1).Angles);
        rig.Frame(120); Assert.Equal(120, Assert.Single(rig.History.GetPlan(0, .1).Angles));
    }
}
