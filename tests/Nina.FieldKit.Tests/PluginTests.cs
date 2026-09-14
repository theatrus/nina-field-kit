using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Windows;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using Nina.FieldKit.Plugin;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using Xunit;

namespace Nina.FieldKit.Tests;

[Collection("Windows UI")]
public sealed class PluginTests {
    private static Mock<ITelescopeMediator> Telescope(TelescopeInfo? info = null) {
        var mock = new Mock<ITelescopeMediator>(MockBehavior.Strict);
        mock.Setup(m => m.GetInfo()).Returns(info ?? new TelescopeInfo { Connected = true, TrackingEnabled = true });
        return mock;
    }

    [Fact] public void SnapshotIsDetachedFromMutableMediatorState() {
        var info = new TelescopeInfo { Connected = true, TrackingEnabled = true, RightAscension = 5 };
        var snapshot = new MountSnapshotReader(Telescope(info).Object).Capture();
        info.TrackingEnabled = false;
        info.RightAscension = 20;
        Assert.True(snapshot.TrackingEnabled);
        Assert.Equal(5d, snapshot.RightAscensionHours);
        Assert.Equal(ObservationSource.CachedMediator, snapshot.Source);
    }

    [Fact] public void DisconnectedSnapshotDoesNotUseDefaultFlagsAsEvidence() {
        var snapshot = new MountSnapshotReader(Telescope(new TelescopeInfo()).Object).Capture();
        Assert.False(snapshot.Connected);
        Assert.Null(snapshot.TrackingEnabled);
        Assert.Null(snapshot.AtHome);
        Assert.Null(snapshot.RightAscensionHours);
    }

    [Fact] public void SnapshotRetainsOriginalExceptionDetails() {
        var telescope = Telescope();
        telescope.Setup(m => m.GetInfo()).Throws(new InvalidOperationException("Synthetic failure"));
        var snapshot = new MountSnapshotReader(telescope.Object).Capture();
        Assert.Contains("InvalidOperationException", snapshot.ReadError);
        Assert.Contains("Synthetic failure", snapshot.ReadError);
        Assert.Null(snapshot.Connected);
    }

    [Fact] public void CloneAndJsonPreserveSettingsButNotResults() {
        var original = new MountHealthCheck(Telescope().Object) {
            SampleCount = 9, RequireTracking = true, RequireUnparked = true,
            RequireStationary = true, RequireConfirmedResponse = true, Attempts = 2
        };
        var clone = Assert.IsType<MountHealthCheck>(original.Clone());
        Assert.Equal(9, clone.SampleCount);
        Assert.True(clone.RequireConfirmedResponse);
        Assert.True(clone.RequireStationary);
        Assert.True(clone.RequireUnparked);
        Assert.True(clone.RequireTracking);
        Assert.Equal(2, clone.Attempts);
        Assert.Equal(InstructionErrorBehavior.AbortOnError, clone.ErrorBehavior);
        var json = JsonConvert.SerializeObject(original);
        Assert.DoesNotContain("LastResult", json);
        var restored = new MountHealthCheck(Telescope().Object);
        JsonConvert.PopulateObject(json, restored);
        Assert.Equal(clone.SampleCount, restored.SampleCount);
        Assert.True(restored.RequireTracking && restored.RequireUnparked && restored.RequireStationary && restored.RequireConfirmedResponse);
        Assert.Equal(clone.ErrorBehavior, restored.ErrorBehavior);
    }

    [Fact] public async Task UnknownFailsSequenceActionWithoutCommands() {
        var telescope = Telescope();
        var item = new MountHealthCheck(telescope.Object) { SampleCount = 1, RequireConfirmedResponse = true };
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(null!, CancellationToken.None));
        Assert.StartsWith("Unknown:", item.LastResult);
        telescope.Verify(m => m.GetInfo(), Times.Once);
        telescope.VerifyNoOtherCalls();
    }

    [Fact] public async Task ReportedHealthPassesWithoutCommands() {
        var telescope = Telescope();
        var item = new MountHealthCheck(telescope.Object) { SampleCount = 1, RequireTracking = true };
        await item.Execute(null!, CancellationToken.None);
        Assert.StartsWith("Healthy:", item.LastResult);
        telescope.Verify(m => m.GetInfo(), Times.Once);
        telescope.VerifyNoOtherCalls();
    }

    [Fact] public async Task InvalidSettingsFailAtExecutionWithoutReading() {
        var telescope = Telescope();
        var item = new MountHealthCheck(telescope.Object) { SampleCount = 0 };
        Assert.False(item.Validate());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(null!, CancellationToken.None));
        telescope.VerifyNoOtherCalls();
    }

    [Fact] public async Task SnapshotAllowsDisconnectedDiagnostics() {
        var telescope = Telescope(new TelescopeInfo());
        var item = new CaptureEquipmentSnapshot(telescope.Object);
        await item.Execute(null!, CancellationToken.None);
        Assert.Contains("snapshot logged", item.LastResult);
        telescope.Verify(m => m.GetInfo(), Times.Once);
        telescope.VerifyNoOtherCalls();
    }

    [Fact] public void MefDiscoversManifestAndBothInstructions() {
        using var catalog = new TypeCatalog(typeof(FieldKitPlugin), typeof(MountHealthCheck), typeof(CaptureEquipmentSnapshot));
        using var container = new CompositionContainer(catalog);
        container.ComposeExportedValue(Telescope().Object);
        Assert.Single(container.GetExportedValues<IPluginManifest>());
        Assert.Equal(2, container.GetExportedValues<ISequenceItem>().Count());
        Assert.Equal("3.2.0.9001", new FieldKitPlugin().MinimumApplicationVersion.ToString());
        Assert.Equal("Apache-2.0", new FieldKitPlugin().License);
        Assert.Equal("https://www.apache.org/licenses/LICENSE-2.0", new FieldKitPlugin().LicenseURL);
    }

    [Fact] public void WpfTemplatesLoadOnStaThread() {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                var resources = new SequenceTemplates();
                Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(MountHealthCheck))]);
                Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(CaptureEquipmentSnapshot))]);
            } catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF resource load timed out");
        Assert.Null(failure);
    }
}
