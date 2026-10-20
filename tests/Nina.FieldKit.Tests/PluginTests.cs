using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Windows;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using Nina.FieldKit.Plugin;
using Nina.FieldKit.Plugin.SkyFlats;
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
        using var catalog = new TypeCatalog(typeof(FieldKitPlugin), typeof(SlewToSkyFlatPoint), typeof(MountHealthCheck), typeof(CaptureEquipmentSnapshot), typeof(AutofocusAboveHfrTrigger), typeof(TemperatureCompensationTrigger), typeof(SaveAutofocusReference), typeof(RestoreAutofocusReference), typeof(NightRotationHistory), typeof(SkyFlatRotationsContainer));
        using var container = new CompositionContainer(catalog);
        container.ComposeExportedValue(Telescope().Object);
        container.ComposeExportedValue(new Mock<NINA.Profile.Interfaces.IProfileService> { DefaultValue = DefaultValue.Mock }.Object);
        container.ComposeExportedValue(Mock.Of<NINA.WPF.Base.Interfaces.ViewModel.IImageHistoryVM>());
        container.ComposeExportedValue(Mock.Of<ICameraMediator>());
        container.ComposeExportedValue(Mock.Of<IFilterWheelMediator>());
        container.ComposeExportedValue(Mock.Of<IFocuserMediator>());
        container.ComposeExportedValue(Mock.Of<IRotatorMediator>());
        container.ComposeExportedValue(Mock.Of<NINA.WPF.Base.Interfaces.Mediator.IImageSaveMediator>());
        container.ComposeExportedValue(Mock.Of<ISafetyMonitorMediator>());
        container.ComposeExportedValue(Mock.Of<NINA.WPF.Base.Interfaces.IAutoFocusVMFactory>());
        Assert.Single(container.GetExportedValues<IPluginManifest>());
        Assert.Equal(6, container.GetExportedValues<ISequenceItem>().Count());
        Assert.IsType<SkyFlatRotationsContainer>(Assert.Single(container.GetExportedValues<NINA.Sequencer.Container.ISequenceContainer>()));
        Assert.Same(container.GetExportedValue<NightRotationHistory>(), container.GetExportedValue<INightRotationHistory>());
        var triggers = container.GetExportedValues<NINA.Sequencer.Trigger.ISequenceTrigger>().ToArray();
        Assert.Equal(2, triggers.Length);
        Assert.Contains(triggers, trigger => trigger is AutofocusAboveHfrTrigger);
        Assert.Contains(triggers, trigger => trigger is TemperatureCompensationTrigger);
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
            foreach (var type in new[] { typeof(SlewToSkyFlatPoint), typeof(SaveAutofocusReference), typeof(RestoreAutofocusReference), typeof(TemperatureCompensationTrigger), typeof(AutofocusAboveHfrTrigger), typeof(AutofocusAboveHfr), typeof(MountHealthCheck), typeof(CaptureEquipmentSnapshot) }) {
                var template = (DataTemplate)resources[new DataTemplateKey(type)];
                var block = Assert.IsType<NINA.View.Sequencer.SequenceBlockView>(template.LoadContent());
                Assert.IsType<System.Windows.Controls.StackPanel>(block.SequenceItemContent);
                Assert.Contains(Microsoft.Xaml.Behaviors.Interaction.GetBehaviors((DependencyObject)block.Content),
                    behavior => behavior is NINA.Sequencer.Behaviors.DragDropBehavior);
                Assert.NotNull(block.FindName("ShowMenuButton"));
                Assert.NotNull(block.FindName("MoveUpButton"));
                Assert.NotNull(block.FindName("MoveDownButton"));
                if (type == typeof(AutofocusAboveHfrTrigger) || type == typeof(TemperatureCompensationTrigger) ||
                    type == typeof(SaveAutofocusReference) || type == typeof(RestoreAutofocusReference)) {
                    block.DataContext = type == typeof(AutofocusAboveHfrTrigger)
                        ? new AutofocusAboveHfrTrigger(Mock.Of<IHfrAutofocusService>()) { Name = "AF above HFR" }
                        : type == typeof(TemperatureCompensationTrigger)
                        ? new TemperatureCompensationTrigger(Mock.Of<IFocuserMediator>(), Mock.Of<ISafetyMonitorMediator>()) { Name = "Temperature compensation after frame" }
                        : type == typeof(SaveAutofocusReference)
                        ? new SaveAutofocusReference(Mock.Of<IAutofocusReferenceService>()) { Name = "Save autofocus reference" }
                        : new RestoreAutofocusReference(Mock.Of<IAutofocusReferenceService>()) { Name = "Restore autofocus reference" };
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
                    var filename = type == typeof(AutofocusAboveHfrTrigger) ? "autofocus-sequence-item.png"
                        : type == typeof(TemperatureCompensationTrigger) ? "temperature-compensation-trigger.png"
                        : type == typeof(SaveAutofocusReference) ? "focus-reference-save.png" : "focus-reference-restore.png";
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, filename));
                    encoder.Save(file);
                }
            }
            CheckFlatRotationContainerChrome(resources, profiles.Object);
            CheckFlatRotationInstructionDrops(resources);
            CheckEmbeddedFlatRotationChildren(resources);
            GC.KeepAlive(profileResources);
        }
        finally {
            Application.Current.MainWindow = previousWindow;
            shell.Close();
            cache.Clear();
            foreach (var item in previous) cache[item.Key] = item.Value;
        }
    }
    private static void CheckFlatRotationContainerChrome(SequenceTemplates resources, NINA.Profile.Interfaces.IProfileService profiles) {
        var rig = new SkyFlatRotationsTests.Rig();
        rig.Container.Add(new SlewToSkyFlatPoint(Telescope().Object, rig.Profiles.Object) { Name = "Slew to sky-flat point" });
        rig.Container.PreviewCommand.Execute(null);
        var template = Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(SkyFlatRotationsContainer))]);
        var view = Assert.IsType<NINA.View.Sequencer.SequenceContainerView>(template.LoadContent());
        Assert.IsType<System.Windows.Controls.StackPanel>(view.SequenceContainerContent);
        var previousResources = Application.Current.Resources;
        try {
        foreach (var theme in new[] { "Dark", "Persian Faint", "Light" }) {
        profiles.ActiveProfile.ColorSchemaSettings.ColorSchema = profiles.ActiveProfile.ColorSchemaSettings.ColorSchemas.Items.First(schema => schema.Name == theme);
        var themeResources = new ResourceDictionary { ["ProfileService"] = profiles };
        Application.Current.Resources = themeResources;
        var shared = NINA.WPF.Base.Utility.SharedResourceDictionary.SharedDictionaries;
        shared.Clear();
        shared[new Uri("/NINA.WPF.Base;component/Resources/StaticResources/ProfileService.xaml", UriKind.Relative)] = new WeakReference(themeResources);
        foreach (var resource in new[] { "StaticResources/Brushes", "StaticResources/Converters", "StaticResources/SVGDictionary",
            "Styles/Button", "Styles/TextBlock", "Styles/TextBox", "Styles/ComboBox", "Styles/ToggleButton", "Styles/Expander" })
            themeResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/NINA.WPF.Base;component/Resources/{resource}.xaml", UriKind.Relative) });
        var tree = new System.Windows.Controls.TreeView { ItemsSource = new[] { rig.Container }, BorderThickness = new Thickness(0) };
        tree.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BackgroundBrush");
        tree.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "PrimaryBrush");
        tree.Resources.MergedDictionaries.Add(resources);
        var itemStyle = new Style(typeof(System.Windows.Controls.TreeViewItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.TreeViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        tree.ItemContainerStyle = itemStyle;
        var host = new System.Windows.Controls.Border { Child = tree, Padding = new Thickness(10) };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BackgroundBrush");
        host.Measure(new Size(1050, 550)); host.Arrange(new Rect(0, 0, 1050, 550)); host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Assert.Equal(profiles.ActiveProfile.ColorSchemaSettings.ColorSchema.BackgroundColor, Assert.IsType<System.Windows.Media.SolidColorBrush>(host.Background).Color);
        IEnumerable<DependencyObject> Visuals(DependencyObject root) {
            yield return root;
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, index))) yield return child;
        }
        var controls = Visuals(host).ToArray();
        Assert.Contains(controls, control => Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(control).Any(behavior => behavior is NINA.Sequencer.Behaviors.DragDropBehavior));
        Assert.Contains(controls.OfType<System.Windows.Controls.TextBox>(), text => text.Text == rig.Container.Name);
        Assert.Contains(controls.OfType<System.Windows.Controls.Button>(), button => button.Content is System.Windows.Controls.TextBlock { Text: "Show rotations" });
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1050, 550, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        using var file = System.IO.File.Create(System.IO.Path.Combine(directory, $"flat-rotations-container-{theme.Replace(' ', '-')}.png")); encoder.Save(file);
        }
        } finally { Application.Current.Resources = previousResources; }
    }
    private static void CheckFlatRotationInstructionDrops(SequenceTemplates resources) {
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/NINA.WPF.Base;component/Resources/Styles/ListView.xaml", UriKind.Relative)
        });
        var rig = new SkyFlatRotationsTests.Rig();
        var root = new NINA.Sequencer.Container.SequenceRootContainer();
        var startup = new NINA.Sequencer.Container.StartAreaContainer();
        var targets = new NINA.Sequencer.Container.TargetAreaContainer();
        root.Add(startup); root.Add(targets); root.Add(new NINA.Sequencer.Container.EndAreaContainer());
        targets.Add(rig.Container);
        var native = new NINA.View.Sequencer.SequenceView {
            DataContext = new { Sequencer = root, IsLocked = false, CanDragAndDrop = true }
        };
        native.Resources.MergedDictionaries.Add(new NINA.Sequencer.Container.Datatemplates());
        native.Resources.MergedDictionaries.Add(resources);
        var host = new System.Windows.Controls.Border { Child = native };
        host.Measure(new Size(1100, 900)); host.Arrange(new Rect(0, 0, 1100, 900)); host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        IEnumerable<DependencyObject> Visuals(DependencyObject element) {
            yield return element;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                foreach (var child in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(element, i))) yield return child;
        }
        var containerView = Assert.Single(Visuals(host).OfType<NINA.View.Sequencer.SequenceContainerView>(), v => ReferenceEquals(v.DataContext, rig.Container));
        var dropTargets = Visuals(containerView).OfType<FrameworkElement>().Where(element =>
            Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(element).OfType<NINA.Sequencer.Behaviors.DropIntoBehavior>()
                .Any(b => b.OnDropCommand == "DropIntoCommand")).ToArray();
        var target = Assert.Single(dropTargets);
        Assert.Same(rig.Container, target.DataContext);
        Assert.True(target.ActualWidth > 200 && target.ActualHeight >= 40);
        // Hit-test the empty target as the native drag behavior does, then use its actual drop command.
        var center = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight - 8), host);
        var drag = new NINA.Sequencer.Behaviors.DragDropBehavior(new System.Windows.Controls.Grid());
        System.Windows.Media.VisualTreeHelper.HitTest(host, null, drag.GetFirstDropIntoBehaviorBelowMyself,
            new System.Windows.Media.PointHitTestParameters(center));
        var drop = Assert.IsType<NINA.Sequencer.Behaviors.DropIntoBehavior>(typeof(NINA.Sequencer.Behaviors.DragDropBehavior)
            .GetField("hitTestDropIntoBehavior", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(drag));
        Assert.Contains(drop, Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(target));
        System.Windows.Media.VisualTreeHelper.HitTest(host, null, drag.GetFirstDraggedOverBehaviorElementBelowMyself,
            new System.Windows.Media.PointHitTestParameters(center));
        Assert.Same(target, typeof(NINA.Sequencer.Behaviors.DragDropBehavior)
            .GetField("draggedOverElement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(drag));
        var over = Assert.Single(Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(target).OfType<NINA.Sequencer.Behaviors.DragOverBehavior>());
        Assert.True(over.AllowDragCenter); Assert.False(over.AllowDragAbove); Assert.False(over.AllowDragBelow);
        var available = new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 1 };
        Assert.True(drop.CanDropInto(available.GetType()));
        drop.ExecuteDropInto(new NINA.Sequencer.DragDrop.DropIntoParameters(available));
        var added = Assert.IsType<NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan>(Assert.Single(rig.Container.Items));
        Assert.NotSame(available, added); Assert.Same(rig.Container, added.Parent);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Assert.Contains(Visuals(host).OfType<NINA.View.Sequencer.SequenceBlockView>(), view => ReferenceEquals(view.DataContext, added) && view.ActualHeight > 0);
        Assert.False(over.AllowDragCenter);
        var existing = new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 2 };
        host.UpdateLayout();
        // A populated loop still accepts native insertions through its container drop command.
        Assert.True(drop.CanDropInto(existing.GetType()));
        startup.Add(existing);
        drop.ExecuteDropInto(new NINA.Sequencer.DragDrop.DropIntoParameters(existing));
        Assert.Empty(startup.Items); Assert.Same(existing, rig.Container.Items[1]); Assert.Same(rig.Container, existing.Parent);
        var nested = new NINA.Sequencer.Container.SequentialContainer();
        nested.Add(new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 3 });
        drop.ExecuteDropInto(new NINA.Sequencer.DragDrop.DropIntoParameters(nested));
        var nestedCopy = Assert.IsType<NINA.Sequencer.Container.SequentialContainer>(rig.Container.Items[2]);
        Assert.Single(nestedCopy.Items); Assert.Same(rig.Container, nestedCopy.Parent); Assert.Same(nestedCopy, nestedCopy.Items[0].Parent);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        var addedView = Assert.Single(Visuals(containerView).OfType<NINA.View.Sequencer.SequenceBlockView>(), v => ReferenceEquals(v.DataContext, added));
        Assert.True(addedView.ActualHeight > 0);
        Assert.Single(Visuals(containerView).OfType<NINA.View.Sequencer.SequenceBlockView>(), v => ReferenceEquals(v.DataContext, nestedCopy.Items[0]));
        var parallel = new NINA.Sequencer.Container.ParallelContainer();
        parallel.Add(new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 4 });
        rig.Container.Add(parallel);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Assert.Single(Visuals(containerView).OfType<NINA.View.Sequencer.SequenceBlockView>(), v => ReferenceEquals(v.DataContext, parallel.Items[0]));
        drop.ExecuteDropInto(new NINA.Sequencer.DragDrop.DropIntoParameters(existing, added, NINA.Core.Enum.DropTargetEnum.Top));
        Assert.Same(existing, rig.Container.Items[0]); Assert.Same(added, rig.Container.Items[1]);
        existing.DetachCommand.Execute(null);
        Assert.DoesNotContain(existing, rig.Container.Items);
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        Assert.DoesNotContain(Visuals(containerView).OfType<NINA.View.Sequencer.SequenceBlockView>(), v => ReferenceEquals(v.DataContext, existing));
        Assert.False(drop.CanDropInto(typeof(NINA.Sequencer.Conditions.LoopCondition)));
        Assert.False(drop.CanDropInto(typeof(TemperatureCompensationTrigger)));
        target.IsEnabled = false;
        var count = rig.Container.Items.Count;
        drop.ExecuteDropInto(new NINA.Sequencer.DragDrop.DropIntoParameters(available));
        Assert.Equal(count, rig.Container.Items.Count);
    }
    private static void CheckEmbeddedFlatRotationChildren(SequenceTemplates resources) {
        var rig = new SkyFlatRotationsTests.Rig();
        var child = new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan { Time = 2 };
        rig.Container.Add(child);
        var embedded = new System.Windows.Controls.ItemsControl { ItemsSource = new[] { rig.Container } };
        embedded.Resources.MergedDictionaries.Add(resources);
        var embeddedHost = new System.Windows.Controls.Border { Child = embedded };
        embeddedHost.Measure(new Size(1000, 800)); embeddedHost.Arrange(new Rect(0, 0, 1000, 800)); embeddedHost.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        embedded.UpdateLayout();
        IEnumerable<DependencyObject> Visuals(DependencyObject element) {
            yield return element;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                foreach (var descendant in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(element, i))) yield return descendant;
        }
        Assert.Contains(Visuals(embedded).OfType<NINA.View.Sequencer.SequenceBlockView>(), view => ReferenceEquals(view.DataContext, child) && view.ActualHeight > 0);
        var expander = Assert.Single(Visuals(embedded).OfType<NINA.CustomControlLibrary.DetachingExpander>(), e => ReferenceEquals(e.DataContext, rig.Container));
        Assert.True(expander.IsExpanded);
        expander.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, false);
        embedded.UpdateLayout();
        Assert.False(rig.Container.IsExpanded);
        Assert.DoesNotContain(Visuals(embedded).OfType<NINA.View.Sequencer.SequenceBlockView>(), view => ReferenceEquals(view.DataContext, child));
        expander.SetCurrentValue(System.Windows.Controls.Expander.IsExpandedProperty, true);
        embedded.UpdateLayout();
        Assert.True(rig.Container.IsExpanded);
        Assert.Contains(Visuals(embedded).OfType<NINA.View.Sequencer.SequenceBlockView>(), view => ReferenceEquals(view.DataContext, child) && view.ActualHeight > 0);
        rig.Container.IsExpanded = false;
        embedded.UpdateLayout();
        Assert.False(expander.IsExpanded);
        rig.Container.IsExpanded = true;
        embedded.UpdateLayout();
        Assert.True(expander.IsExpanded);
        Assert.Single(Visuals(embedded).OfType<NINA.View.Sequencer.SequenceBlockView>(), view => ReferenceEquals(view.DataContext, child));
        var factory = new Mock<NINA.Sequencer.ISequencerFactory>();
        factory.SetupGet(f => f.DateTimeProviders).Returns(new List<NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider>());
        factory.Setup(f => f.GetContainer<SkyFlatRotationsContainer>()).Returns(() => new SkyFlatRotationsContainer(rig.History, rig.Profiles.Object, rig.Rotator.Object, rig.Camera.Object, rig.Safety.Object));
        factory.Setup(f => f.GetItem<NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan>()).Returns(() => new NINA.Sequencer.SequenceItem.Utility.WaitForTimeSpan());
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory.Object);
        var restored = Assert.IsType<SkyFlatRotationsContainer>(converter.Deserialize(converter.Serialize(rig.Container)));
        var restoredChild = Assert.Single(restored.Items);
        embedded.ItemsSource = new[] { restored };
        embeddedHost.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        embeddedHost.UpdateLayout();
        Assert.Same(restored, restoredChild.Parent);
        Assert.Single(Visuals(embedded).OfType<NINA.View.Sequencer.SequenceBlockView>(), v => ReferenceEquals(v.DataContext, restoredChild));
        Assert.True(Assert.Single(Visuals(embedded).OfType<NINA.CustomControlLibrary.DetachingExpander>(), e => ReferenceEquals(e.DataContext, restored)).IsExpanded);
    }
}
