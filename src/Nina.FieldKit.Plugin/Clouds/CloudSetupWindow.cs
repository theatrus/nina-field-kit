using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using Nina.FieldKit.Core.Clouds;
using Nina.FieldKit.Plugin.Safety;

namespace Nina.FieldKit.Plugin.Clouds;

public sealed class CloudSetupWindow : Window {
    public CloudSetupWindow(CloudGuardService service) {
        Title = "Field Kit Cloud Guard"; Width = 780; Height = 720; MinWidth = 680; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var identity = service.ProfileIdentity;
        var options = service.LoadOptions();
        var root = SafetyDialogLayout.Root(this); Content = root;
        var title = new StackPanel(); title.Children.Add(FieldKitIcon.Heading("Cloud guard"));
        title.Children.Add(SafetyDialogLayout.Note("Detect clouds from comparable light frames, guiding loss and failed sequence plate solves. Settings apply to this NINA profile."));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10), FontSize = 15 };
        title.Children.Add(status); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var footer = new StackPanel(); var error = SafetyDialogLayout.Note(""); footer.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var close = SafetyDialogLayout.Button("Close"); close.Click += (_, _) => Close(); buttons.Children.Add(close);
        var save = SafetyDialogLayout.Button("Save to profile"); buttons.Children.Add(save); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var panel = new StackPanel { Margin = new Thickness(0, 8, 12, 8) };
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var fields = new Dictionary<string, TextBox>();
        void Field(Panel parent, string name, string label, string hint) {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            var text = new StackPanel(); text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            text.Children.Add(SafetyDialogLayout.Note(hint, 3)); row.Children.Add(text);
            var box = new TextBox { Text = Convert.ToString(typeof(CloudOptions).GetProperty(name)!.GetValue(options), CultureInfo.InvariantCulture),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1); row.Children.Add(box); fields[name] = box; parent.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "RETRY AND END OF NIGHT", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        Field(panel, nameof(options.RetrySeconds), "Allow a retry after (seconds)", "Default 300 s. The timer allows another attempt; it does not confirm clear skies.");
        Field(panel, nameof(options.MaximumEpisodeSeconds), "Stop trying after (seconds)", "Default 3600 s across the whole episode, including unsuccessful trial resumptions.");
        panel.Children.Add(new TextBlock { Text = "Morning cutoff", FontWeight = FontWeights.SemiBold });
        var morning = new ComboBox { ItemsSource = Enum.GetValues<MorningBoundary>(), SelectedItem = options.Morning, Margin = new Thickness(0, 5, 0, 5) };
        panel.Children.Add(morning);
        panel.Children.Add(SafetyDialogLayout.Note("Uses your NINA profile location. Morning exits the imaging target area so normal end-of-sequence instructions can run. Default: nautical dawn."));
        var detection = new StackPanel(); panel.Children.Add(new Expander { Header = "Detection thresholds", Content = detection, Margin = new Thickness(0, 12, 0, 0) });
        Field(detection, nameof(options.StarFraction), "Low star-count fraction", "0.5 means fewer than half the reference stars for this target/filter/exposure setup.");
        Field(detection, nameof(options.RecoveryStarFraction), "Clear confirmation fraction", "Default 0.8 of the retained reference. Baselines never adapt downward during clouds.");
        Field(detection, nameof(options.BaselineFrames), "Reference light frames", "Default 3. Separate references for exposure, binning, gain, offset and image size; flats/darks are ignored.");
        Field(detection, nameof(options.BadFrames), "Low-star frames before a cloud event", "Default 3. A low frame during a trial retry immediately holds again.");
        Field(detection, nameof(options.ClearFrames), "Clear light frames to confirm recovery", "Default 2. Until then, status says Retry allowed.");
        var guiding = new CheckBox { IsChecked = options.UseGuiding, VerticalAlignment = VerticalAlignment.Center };
        var guideRow = new DockPanel(); DockPanel.SetDock(guiding, Dock.Right); guideRow.Children.Add(guiding);
        guideRow.Children.Add(new Label { Content = "Use guiding loss and guide-star flapping", Target = guiding }); detection.Children.Add(guideRow);
        Field(detection, nameof(options.GuideLossSeconds), "Guiding lost for (seconds)", "Default 30 s while a light exposure is active. Unguided sessions and intentional guider states are excluded.");
        Field(detection, nameof(options.GuideLosses), "Guide-star loss transitions", "Default 3 loss/recovery transitions in the window below.");
        Field(detection, nameof(options.FlapWindowSeconds), "Guiding instability window (seconds)", "Default 300 s.");
        Field(detection, nameof(options.SolveFailures), "Failed sequence solve attempts", "Default 2. Uses NINA sequence failure events; unrelated background solvers are not globally intercepted.");
        Field(detection, nameof(options.OperationTimeoutSeconds), "Mount/recovery operation timeout (seconds)", "Default 180 s. Passed as cancellation to NINA's normal park, center and guide operations.");
        Field(detection, nameof(options.MinimumAltitudeDegrees), "Minimum recovery altitude (degrees)", "Default 20°. The NINA custom horizon is also respected. Closed dome shutters and unsafe sources block recovery.");
        save.Click += (_, _) => {
            try {
                var json = JObject.FromObject(options);
                foreach (var (name, box) in fields) json[name] = JToken.FromObject(Convert.ChangeType(box.Text, typeof(CloudOptions).GetProperty(name)!.PropertyType, CultureInfo.InvariantCulture));
                json[nameof(options.Morning)] = (int)(MorningBoundary)morning.SelectedItem;
                json[nameof(options.UseGuiding)] = guiding.IsChecked == true;
                var updated = json.ToObject<CloudOptions>()!.Validate();
                service.SaveOptions(updated, identity); options = updated; error.Text = "Cloud settings saved to this profile.";
            } catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or InvalidOperationException) { error.Text = e.Message; }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Refresh() { status.Text = service.Status; save.IsEnabled = ReferenceEquals(identity, service.ProfileIdentity); }
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh(); Closed += (_, _) => timer.Stop();
    }
}
