// SPDX-License-Identifier: MPL-2.0
// Adapted from photon1503/SkyFlats, including theatrus PR #4.
// Source and modifications: see THIRD-PARTY-NOTICES.md.
using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using Nina.FieldKit.Plugin.SkyFlats;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nina.FieldKit.Tests {
    public class NullPointSlewTests {
        private static TopocentricCoordinates Target() => new TopocentricCoordinates(
            Angle.ByDegree(260), Angle.ByDegree(75), Angle.ByDegree(31), Angle.ByDegree(-99));

        private static Mock<ITelescopeMediator> Mount(bool native = false, bool connected = true, bool parked = false) {
            var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo {
                Connected = connected, AtPark = parked, CanSlewAltAz = native
            });
            return mount;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UsesSupportedRouteAndEnablesTrackingAfterSlew(bool native) {
            var mount = Mount(native);
            var target = Target();
            using var cts = new CancellationTokenSource();
            var completion = new TaskCompletionSource<bool>();
            if (native) mount.Setup(m => m.SlewToTopocentricCoordinates(target, cts.Token)).Returns(completion.Task);
            else mount.Setup(m => m.SlewToCoordinatesAsync(target, cts.Token)).Returns(completion.Task);
            mount.Setup(m => m.SetTrackingEnabled(true)).Returns(true);

            var task = NullPointSlew.ExecuteAsync(mount.Object, target, cts.Token);
            Assert.False(task.IsCompleted);
            mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never);
            completion.SetResult(true);
            await task;
            mount.Verify(m => m.SetTrackingEnabled(true), Times.Once);
            if (native) mount.Verify(m => m.SlewToTopocentricCoordinates(target, cts.Token), Times.Once);
            else mount.Verify(m => m.SlewToCoordinatesAsync(target, cts.Token), Times.Once);
            // Strict mocks reject the other overload, route, or any extra device command.
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public async Task RejectsDisconnectedOrParkedMount(bool connected, bool parked) {
            var mount = Mount(connected: connected, parked: parked);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                NullPointSlew.ExecuteAsync(mount.Object, Target(), CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedSlewDoesNotEnableTracking(bool native) {
            var mount = Mount(native);
            var target = Target();
            if (native) mount.Setup(m => m.SlewToTopocentricCoordinates(target, CancellationToken.None)).ReturnsAsync(false);
            else mount.Setup(m => m.SlewToCoordinatesAsync(target, CancellationToken.None)).ReturnsAsync(false);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                NullPointSlew.ExecuteAsync(mount.Object, target, CancellationToken.None));
        }

        [Fact]
        public async Task AlreadyCanceledSendsNoDeviceCommands() {
            var mount = new Mock<ITelescopeMediator>(MockBehavior.Strict);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NullPointSlew.ExecuteAsync(mount.Object, Target(), cts.Token));
        }

        [Fact]
        public async Task CancellationAfterSlewDoesNotEnableTracking() {
            var mount = Mount();
            var target = Target();
            using var cts = new CancellationTokenSource();
            mount.Setup(m => m.SlewToCoordinatesAsync(target, cts.Token)).Returns(() => {
                cts.Cancel();
                return Task.FromResult(true);
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NullPointSlew.ExecuteAsync(mount.Object, target, cts.Token));
        }

        [Fact]
        public async Task TrackingFailureFailsInstruction() {
            var mount = Mount();
            var target = Target();
            mount.Setup(m => m.SlewToCoordinatesAsync(target, CancellationToken.None)).ReturnsAsync(true);
            mount.Setup(m => m.SetTrackingEnabled(true)).Returns(false);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() =>
                NullPointSlew.ExecuteAsync(mount.Object, target, CancellationToken.None));
        }
    }
}
