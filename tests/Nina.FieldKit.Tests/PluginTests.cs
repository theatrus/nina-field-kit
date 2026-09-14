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

    [Fact] public void MefDiscoversManifestAndAllInstructions() {
        using var catalog = new TypeCatalog(typeof(FieldKitPlugin), typeof(MountHealthCheck), typeof(CaptureEquipmentSnapshot), typeof(AutofocusAboveHfr));
        using var container = new CompositionContainer(catalog);
        container.ComposeExportedValue(Telescope().Object);
        container.ComposeExportedValue(Mock.Of<NINA.Profile.Interfaces.IProfileService>());
        container.ComposeExportedValue(Mock.Of<NINA.WPF.Base.Interfaces.ViewModel.IImageHistoryVM>());
        container.ComposeExportedValue(Mock.Of<ICameraMediator>());
        container.ComposeExportedValue(Mock.Of<IFilterWheelMediator>());
        container.ComposeExportedValue(Mock.Of<IFocuserMediator>());
        container.ComposeExportedValue(Mock.Of<NINA.WPF.Base.Interfaces.IAutoFocusVMFactory>());
        Assert.Single(container.GetExportedValues<IPluginManifest>());
        Assert.Equal(3, container.GetExportedValues<ISequenceItem>().Count());
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
                Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(AutofocusAboveHfr))]);
            } catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF resource load timed out");
        Assert.Null(failure);
    }
    internal static void CheckSequenceItemChrome() {
        var cache = NINA.WPF.Base.Utility.SharedResourceDictionary.SharedDictionaries;
        var previous = cache.ToArray();
        var previousWindow = Application.Current.MainWindow;
        var shell = new Window();
        NameScope.SetNameScope(shell, new NameScope());
        shell.RegisterName("RootGrid", new System.Windows.Controls.Grid());
        Application.Current.MainWindow = shell;
        try {
            // Native sequence chrome needs a profile resource. Keep this test detached from user profiles.
            var profiles = new Mock<NINA.Profile.Interfaces.IProfileService> { DefaultValue = DefaultValue.Mock };
            profiles.SetupGet(p => p.ActiveProfile.ColorSchemaSettings).Returns(new NINA.Profile.ColorSchemaSettings());
            var profileResources = new ResourceDictionary { ["ProfileService"] = profiles.Object };
            cache[new Uri("/NINA.WPF.Base;component/Resources/StaticResources/ProfileService.xaml", UriKind.Relative)] = new WeakReference(profileResources);
            var resources = new SequenceTemplates();
            Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(MountHealthCheck))]);
            Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(CaptureEquipmentSnapshot))]);
            Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(AutofocusAboveHfr))]);
            foreach (var type in new[] { typeof(AutofocusAboveHfr), typeof(MountHealthCheck), typeof(CaptureEquipmentSnapshot) }) {
                var template = (DataTemplate)resources[new DataTemplateKey(type)];
                var block = Assert.IsType<NINA.View.Sequencer.SequenceBlockView>(template.LoadContent());
                Assert.IsType<System.Windows.Controls.StackPanel>(block.SequenceItemContent);
                Assert.Contains(Microsoft.Xaml.Behaviors.Interaction.GetBehaviors((DependencyObject)block.Content),
                    behavior => behavior is NINA.Sequencer.Behaviors.DragDropBehavior);
                Assert.NotNull(block.FindName("ShowMenuButton"));
                Assert.NotNull(block.FindName("MoveUpButton"));
                Assert.NotNull(block.FindName("MoveDownButton"));
                if (type == typeof(AutofocusAboveHfr)) {
                    block.DataContext = new AutofocusAboveHfr(Mock.Of<IHfrAutofocusService>()) {
                        Name = "Autofocus Above HFR"
                    };
                    var host = new System.Windows.Controls.Border {
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
                        Child = block, Padding = new Thickness(8)
                    };
                    host.Measure(new Size(760, 240));
                    host.Arrange(new Rect(0, 0, 760, 240));
                    host.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(760, 240, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
                    System.IO.Directory.CreateDirectory(directory);
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, "autofocus-sequence-item.png"));
                    encoder.Save(file);
                }
            }
            GC.KeepAlive(profileResources);
        }
        finally {
            Application.Current.MainWindow = previousWindow;
            shell.Close();
            cache.Clear();
            foreach (var item in previous) cache[item.Key] = item.Value;
        }
    }
}
