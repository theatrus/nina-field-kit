using Moq;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Nina.FieldKit.Plugin;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SlewToSkyFlatPointTests {
    private static IProfileService Profiles(double latitude = 31) {
        var profiles = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        profiles.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(latitude);
        profiles.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(-99);
        profiles.SetupGet(p => p.ActiveProfile.AstrometrySettings.Elevation).Returns(450);
        return profiles.Object;
    }

    [Theory]
    [InlineData(80, 260)]
    [InlineData(270, 90)]
    [InlineData(180, 0)]
    public async Task ItemComputesNullPointAndUsesFallback(double sunAzimuth, double expectedAzimuth) {
        var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
        mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true });
        TopocentricCoordinates? target = null;
        mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<TopocentricCoordinates>(), default))
            .Callback<TopocentricCoordinates, CancellationToken>((c, _) => target = c).ReturnsAsync(true);
        mount.Setup(m => m.SetTrackingEnabled(true)).Returns(true);
        var item = new SlewToSkyFlatPoint(mount.Object, Profiles(), () => sunAzimuth);
        await item.Execute(null!, default);
        Assert.NotNull(target);
        Assert.Equal(75, target.Altitude.Degree, 8);
        Assert.Equal(expectedAzimuth, target.Azimuth.Degree, 8);
        Assert.Equal("Slew complete; tracking enabled", item.LastResult);
        var clone = Assert.IsType<SlewToSkyFlatPoint>(item.Clone());
        Assert.NotEqual(item.LastResult, clone.LastResult);
        Assert.DoesNotContain("LastResult", JsonConvert.SerializeObject(item));
    }

    [Theory]
    [InlineData(false, false, 31)]
    [InlineData(true, true, 31)]
    [InlineData(true, false, 91)]
    [InlineData(true, false, double.NaN)]
    public async Task InvalidPreconditionsNeverSlew(bool connected, bool parked, double latitude) {
        var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
        mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = connected, AtPark = parked });
        var item = new SlewToSkyFlatPoint(mount.Object, Profiles(latitude));
        Assert.False(item.Validate());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(null!, default));
    }

    [Fact] public async Task CancelledItemDoesNotReadProfileOrMount() {
        var item = new SlewToSkyFlatPoint(new Mock<ITelescopeMediator>(MockBehavior.Strict).Object,
            new Mock<IProfileService>(MockBehavior.Strict).Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => item.Execute(null!, new CancellationToken(true)));
    }

    [Fact] public async Task InvalidSolarCalculationNeverCommandsMount() {
        var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
        mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true });
        var item = new SlewToSkyFlatPoint(mount.Object, Profiles(), () => double.NaN);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(null!, default));
    }
}
