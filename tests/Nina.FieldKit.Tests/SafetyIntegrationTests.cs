using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Plugin.Safety;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile;
using NINA.Profile.Interfaces;
using Xunit;

namespace Nina.FieldKit.Tests;

[Collection("Windows UI")]
public sealed class SafetyIntegrationTests {
    private static void CheckConnectedDraftControls() {
        var server = new LocalAlpacaServer();
        try {
                using var device = new FieldKitSafetyMonitor(Profiles().Object);
                var a = new SafetyEndpointOptions { Label = "A", BaseUrl = server.BaseUrl };
                var b = a with { Id = Guid.NewGuid(), Label = "B", DeviceNumber = 1 };
                device.SaveConfiguration(new() { Endpoints = [a, b] }, device.ProfileIdentity);
                Assert.True(device.Connect(default).GetAwaiter().GetResult());
                var window = new SafetySetupWindow(device);
                IEnumerable<DependencyObject> Descendants(DependencyObject root) {
                    yield return root;
                    foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                        foreach (var descendant in Descendants(child)) yield return descendant;
                }
                var controls = Descendants(window).ToArray();
                Button Button(string text) => controls.OfType<Button>().Single(b => b.Content is TextBlock t && t.Text == text);
                var grid = controls.OfType<DataGrid>().Single();
                Assert.True(Button("Add source").IsEnabled);
                Assert.True(Button("Edit").IsEnabled);
                Assert.True(Button("Save to profile").IsEnabled);
                Assert.False(Button("Test source").IsEnabled);
                Button("Enable / disable").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.False(((SafetyEndpointOptions)grid.SelectedItem).Enabled);
                Assert.True(device.LoadConfiguration().Endpoints[0].Enabled);
                Assert.True(device.Connected);
                Button("Remove").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Single(grid.Items.Cast<object>());
                Assert.NotNull(grid.SelectedItem);
                Assert.Equal(2, device.LoadConfiguration().Endpoints.Count);
                Button("Save to profile").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal(b.Id, Assert.Single(device.LoadConfiguration().Endpoints).Id);
                Assert.True(device.Connected);
                window.Close();
        } finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    internal static Mock<IProfileService> Profiles() {
        var profile = new Mock<IProfile>();
        profile.SetupGet(p => p.PluginSettings).Returns(new PluginSettings());
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        return profiles;
    }

    [Fact] public async Task PersistentTcpSocketIsRenewedAfterConfiguredLifetime() {
        await using var server = new LocalAlpacaServer();
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options with { BaseUrl = server.BaseUrl, ConnectionLifetimeSeconds = 1 });
        Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.Equal(1, server.Connections);
        await Task.Delay(1150);
        Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.Equal(2, server.Connections);
    }

    [Fact] public async Task RealHttp502RetriesInBackgroundAndExplicitUnsafeWithdrawsSafety() {
        await using var server = new LocalAlpacaServer();
        server.SafetyResponse = call => call switch { 1 or 2 => (502, false, null), 3 => (200, true, null), _ => (200, false, null) };
        var options = SafetyStateTests.Options with { BaseUrl = server.BaseUrl, PollSeconds = .1, InitialBackoffSeconds = .05 };
        using var service = new SafetyMonitorService(new() { Endpoints = [options] });
        service.Start();
        await SafetyServiceTests.Until(() => server.SafetyRequests >= 4 && service.Snapshot().Endpoints[0].RawIsSafe == false);
        Assert.False(service.Snapshot().IsSafe);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        Assert.Equal(EndpointPhase.Unsafe, service.Snapshot().Endpoints[0].Phase);
        Assert.Equal(1, server.Connections);
    }

    [Fact] public async Task ProfileSettingsRoundTripApplyLiveAndDisconnectOnProfileChange() {
        await using var server = new LocalAlpacaServer();
        var profiles = Profiles();
        using var device = new FieldKitSafetyMonitor(profiles.Object);
        var config = new SafetyConfiguration { Endpoints = [SafetyStateTests.Options with { BaseUrl = server.BaseUrl, MaximumSafeAgeSeconds = 45 }] };
        device.SaveConfiguration(config, device.ProfileIdentity);
        var restored = device.LoadConfiguration();
        Assert.Equal(45, restored.Endpoints[0].MaximumSafeAgeSeconds);
        Assert.NotEqual(config.Revision, restored.Revision);
        Assert.True(await device.Connect(default));
        await SafetyServiceTests.Until(() => device.IsSafe);
        device.SaveConfiguration(config, device.ProfileIdentity);
        Assert.True(device.Connected);
        Assert.True(device.IsSafe);
        profiles.Raise(p => p.BeforeProfileChanging += null, EventArgs.Empty);
        Assert.False(device.Connected);
        Assert.False(device.IsSafe);
        Assert.Null(device.GetSnapshot());
        Assert.Throws<InvalidOperationException>(() => device.SaveConfiguration(config, new object()));
    }

    [Fact] public void ProviderIsDiscoverableByNinasNonGenericExport() {
        using var catalog = new TypeCatalog(typeof(SafetyMonitorProvider));
        using var container = new CompositionContainer(catalog);
        container.ComposeExportedValue(Profiles().Object);
        var provider = Assert.IsType<SafetyMonitorProvider>(Assert.Single(container.GetExportedValues<IEquipmentProvider>()));
        Assert.Same(provider, container.GetExportedValue<SafetyMonitorProvider>());
        Assert.Same(provider.GetEquipment()[0], provider.GetEquipment()[0]);
        Assert.IsAssignableFrom<ISafetyMonitor>(provider.GetEquipment()[0]);
    }

    [Fact] public async Task EmptyConfigurationFailsConnectUnsafe() {
        using var device = new FieldKitSafetyMonitor(Profiles().Object);
        Assert.False(await device.Connect(default));
        Assert.False(device.Connected);
        Assert.False(device.IsSafe);
    }

    [Fact] public void SetupAndEndpointEditorRenderOnStaThread() {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var manifestIcon = BitmapFrame.Create(new Uri(new Nina.FieldKit.Plugin.FieldKitPlugin().Descriptions.FeaturedImageURL));
                Assert.Equal(512, manifestIcon.PixelWidth);
                using var device = new FieldKitSafetyMonitor(Profiles().Object);
                var example = new SafetyEndpointOptions { Label = "Observatory safety", BaseUrl = "http://192.0.2.1:11111", DeviceNumber = 0 };
                device.SaveConfiguration(new SafetyConfiguration { Endpoints = [example] }, device.ProfileIdentity);
                // Render detached visual trees without opening a user-visible window.
                foreach (var theme in new[] { "Dark", "Persian Faint", "Light" })
                foreach (var (window, name) in new[] { ((Window)new SafetySetupWindow(device), "safety-setup"), ((Window)new SafetyEndpointEditor(example), "safety-endpoint") }) {
                    var content = (FrameworkElement)window.Content;
                    var settings = new ColorSchemaSettings();
                    settings.ColorSchema = settings.ColorSchemas.Items.First(s => s.Name == theme);
                    var profile = new Mock<IProfile>();
                    profile.SetupGet(p => p.ColorSchemaSettings).Returns(settings);
                    var profiles = new Mock<IProfileService>();
                    profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
                    var resources = new ResourceDictionary { ["ProfileService"] = profiles.Object };
                    app.Resources = resources;
                    content.Resources = resources;
                    foreach (var resource in new[] { "StaticResources/Brushes", "StaticResources/Converters", "StaticResources/SVGDictionary",
                        "Styles/Button", "Styles/TextBlock", "Styles/TextBox", "Styles/TabControl", "Styles/CheckBox", "Styles/DataGrid",
                        "Styles/RepeatButton", "Styles/ToggleButton", "Styles/ScrollViewer", "Styles/ComboBox", "Styles/Expander" })
                        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/NINA.WPF.Base;component/Resources/{resource}.xaml", UriKind.Relative) });
                    content.SetValue(Control.FontSizeProperty, 14d);
                    window.Content = null;
                    var tabs = ((DockPanel)content).Children.OfType<TabControl>().Single();
                    for (var tabIndex = 0; tabIndex < tabs.Items.Count; tabIndex++) {
                    tabs.SelectedIndex = tabIndex;
                    content.Measure(new Size(window.Width, window.Height));
                    content.Arrange(new Rect(0, 0, window.Width, window.Height));
                    content.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    content.UpdateLayout();
                    Assert.Equal(settings.ColorSchema.BackgroundColor, Assert.IsType<SolidColorBrush>(((DockPanel)content).Background).Color);
                    Assert.Equal(settings.ColorSchema.PrimaryColor, Assert.IsType<SolidColorBrush>(System.Windows.Documents.TextElement.GetForeground(content)).Color);
                    var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    Assert.True(content.ActualWidth > 500);
                    var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"{name}-{theme.Replace(' ', '-')}-{tabIndex}.png"));
                    encoder.Save(file);
                    }
                    window.Close();
                }
                CheckConnectedDraftControls();
                app.Shutdown();
            } catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }
}
