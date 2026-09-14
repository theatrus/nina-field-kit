using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nina.FieldKit.Core.Safety;

namespace Nina.FieldKit.Plugin.Safety;

// Setup edits detached records. Only Apply commits a new profile revision.
public sealed class SafetySetupWindow : Window {
    private readonly FieldKitSafetyMonitor device;
    private readonly object profileIdentity;
    private readonly ObservableCollection<SafetyEndpointOptions> endpoints = new();
    private readonly DataGrid grid = new();
    private readonly TextBlock live = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox diagnostics = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 110 };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBox eventHistory = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button traceButton = SafetyDialogLayout.Button("Trace polls for 5 minutes");
    private readonly TextBlock traceStatus = SafetyDialogLayout.Note("");
    private bool testing;
    private readonly TextBlock editStatus = SafetyDialogLayout.Note("");
    private readonly List<Button> draftButtons = new();
    private readonly List<Button> selectedButtons = new();
    private readonly Button saveButton;
    private readonly Button testButton;


    public SafetySetupWindow(FieldKitSafetyMonitor device) {
        this.device = device;
        profileIdentity = device.ProfileIdentity;
        Title = "Field Kit Alpaca Safety Monitor";
        Width = 960; Height = 740; MinWidth = 800; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = SafetyDialogLayout.Root(this);
        Content = panel;
        var heading = new StackPanel();
        heading.Children.Add(FieldKitIcon.Heading("Safety sources"));
        heading.Children.Add(new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "Every enabled source must confirm safe. Add a source, review its behavior, then save to this NINA profile." });
        var statusContent = new StackPanel();
        statusContent.Children.Add(new TextBlock { Text = "MONITOR STATUS", FontSize = 12, FontWeight = FontWeights.SemiBold });
        live.FontSize = 16;
        statusContent.Children.Add(new ScrollViewer { Content = live, MaxHeight = 95, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var statusCard = new Border { Child = statusContent, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 16) };
        statusCard.SetResourceReference(Border.BackgroundProperty, "SecondaryBackgroundBrush");
        statusCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        statusCard.BorderThickness = new Thickness(1);
        heading.Children.Add(statusCard);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var footer = new StackPanel();
        footer.Children.Add(new TextBlock { Text = "CONFIGURATION", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        footer.Children.Add(editStatus);
        footer.Children.Add(notice);
        var saveButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        AddButton(saveButtons, "Close", Close);
        saveButton = AddButton(saveButtons, "Save to profile", Apply);
        footer.Children.Add(saveButtons);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var tabs = new TabControl();
        var sources = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        draftButtons.Add(AddButton(buttons, "Add source", () => EditEndpoint(null)));
        selectedButtons.Add(AddButton(buttons, "Edit", () => { if (Selected is { } endpoint) EditEndpoint(endpoint); }));
        selectedButtons.Add(AddButton(buttons, "Remove", () => {
            if (CanEdit() && Selected is { } endpoint) { var index = endpoints.IndexOf(endpoint); endpoints.Remove(endpoint); grid.SelectedIndex = Math.Min(index, endpoints.Count - 1); }
        }));
        selectedButtons.Add(AddButton(buttons, "Enable / disable", () => {
            if (CanEdit() && Selected is { } endpoint) { var edited = endpoint with { Enabled = !endpoint.Enabled }; endpoints[endpoints.IndexOf(endpoint)] = edited; grid.SelectedItem = edited; }
        }));
        testButton = SafetyDialogLayout.Button("Test source");
        testButton.Click += async (_, _) => await TestSelectedAsync();
        buttons.Children.Add(testButton);
        DockPanel.SetDock(buttons, Dock.Top); sources.Children.Add(buttons);
        var help = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        help.Children.Add(new TextBlock { Text = "HOW TO USE", FontSize = 12, FontWeight = FontWeights.SemiBold });
        help.Children.Add(SafetyDialogLayout.Note("Select a row to edit, enable/disable, remove or test that source. Save to profile applies your draft without disconnecting. Poll (s) is the normal time between server checks; Max age (s) limits retained safe evidence when updates stop."));
        DockPanel.SetDock(help, Dock.Bottom); sources.Children.Add(help);
        grid = new DataGrid { ItemsSource = endpoints, IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
            RowHeight = 38, ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star), Margin = new Thickness(0, 8, 0, 8) };
        var cellText = new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("PrimaryBrush")));
        cellText.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        cellText.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0)));
        cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        foreach (var (title, path) in new[] { ("Enabled", "Enabled"), ("Source", "Label"), ("Server URL", "BaseUrl"),
            ("Device", "DeviceNumber"), ("Poll (s)", "PollSeconds"), ("Max age (s)", "MaximumSafeAgeSeconds") })
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(path), ElementStyle = cellText });
        grid.Columns[0].Width = 70;
        grid.Columns[1].Width = 180;
        grid.Columns[2].Width = new DataGridLength(3, DataGridLengthUnitType.Star);
        grid.Columns[3].Width = 65;
        grid.Columns[4].Width = 75;
        grid.Columns[5].Width = 90;
        grid.SelectionChanged += (_, _) => UpdateControls();
        sources.Children.Add(grid);
        tabs.Items.Add(new TabItem { Header = SafetyDialogLayout.TabHeading("Sources"), Content = sources });
        var status = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var export = new WrapPanel();
        AddButton(export, "Export diagnostics", ExportDiagnostics);
        AddButton(export, "Copy report", () => {
            try { Clipboard.SetText(device.ExportDiagnosticReport()); notice.Text = "Diagnostic report copied; source labels are included."; }
            catch (System.Runtime.InteropServices.ExternalException) { notice.Text = "Clipboard is busy. Try again or export the report."; }
        });
        AddButton(export, "Log snapshot", () => {
            device.RecordDiagnostic("ManualSnapshot", new { snapshot = device.GetSnapshot() });
            notice.Text = "Current snapshot written to the NINA log. Search for FieldKitSafety.";
        });
        DockPanel.SetDock(export, Dock.Bottom); status.Children.Add(export);
        var tracePanel = new StackPanel();
        traceButton.HorizontalAlignment = HorizontalAlignment.Left;
        traceButton.Click += (_, _) => { device.SetDiagnosticTrace(device.TraceRemaining == TimeSpan.Zero); RefreshStatus(); };
        tracePanel.Children.Add(traceButton); tracePanel.Children.Add(traceStatus);
        DockPanel.SetDock(tracePanel, Dock.Top); status.Children.Add(tracePanel);
        status.Children.Add(diagnostics);
        tabs.Items.Add(new TabItem { Header = SafetyDialogLayout.TabHeading("Live diagnostics"), Content = status });
        var history = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var historyHelp = SafetyDialogLayout.Note("Newest first • last 300 events from this plugin session. Events also go to NINA's log under FieldKitSafety. Retries, missed checks and recovery are always recorded. Enable tracing in Live diagnostics to include every poll result.");
        DockPanel.SetDock(historyHelp, Dock.Top); history.Children.Add(historyHelp); history.Children.Add(eventHistory);
        tabs.Items.Add(new TabItem { Header = SafetyDialogLayout.TabHeading("Recent events"), Content = history });
        panel.Children.Add(tabs);
        try { foreach (var endpoint in device.LoadConfiguration().Endpoints) endpoints.Add(endpoint); }
        catch (ArgumentException exception) { notice.Text = exception.Message; }
        if (endpoints.Count > 0) grid.SelectedIndex = 0;
        else notice.Text = "No sources yet. Choose Add source to get started.";
        endpoints.CollectionChanged += (_, _) => notice.Text = "Unsaved changes — choose Save to profile to apply them. Closing discards this draft.";
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        Closed += (_, _) => { timer.Stop(); cancellation.Cancel(); cancellation.Dispose(); };
        RefreshStatus();
    }

    private SafetyEndpointOptions? Selected => grid.SelectedItem as SafetyEndpointOptions;
    private static Button AddButton(Panel panel, string title, Action action) {
        var button = SafetyDialogLayout.Button(title);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        return button;
    }

    private bool CanEdit() {
        if (!ReferenceEquals(profileIdentity, device.ProfileIdentity)) { notice.Text = "Profile changed. Close and reopen setup."; return false; }
        if (testing) { notice.Text = "Wait for the source test to finish before editing."; return false; }
        return true;
    }

    private bool CanApplyOrTest() {
        if (!CanEdit()) return false;
        if (device.Connected) { notice.Text = "A separate source test requires disconnecting. Live monitoring already checks saved sources; saving does not require disconnecting."; return false; }
        return true;
    }

    private void UpdateControls() {
        var sameProfile = ReferenceEquals(profileIdentity, device.ProfileIdentity);
        var editable = sameProfile && !testing;
        foreach (var button in draftButtons) button.IsEnabled = editable;
        foreach (var button in selectedButtons) button.IsEnabled = editable && Selected is not null;
        saveButton.IsEnabled = editable;
        testButton.IsEnabled = editable && !device.Connected && Selected is not null;


        editStatus.Text = !sameProfile ? "Profile changed. Close and reopen setup." : testing ? "Source test in progress…" : device.Connected
            ? "Monitoring is active. Save applies your draft live and retains the last result while sources refresh."
            : "Monitoring is stopped. Select a source to edit or test, or choose Add source.";
    }

    private void EditEndpoint(SafetyEndpointOptions? existing) {
        if (!CanEdit()) return;
        var editor = new SafetyEndpointEditor(existing ?? new SafetyEndpointOptions()) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } edited) {
            if (existing is null) endpoints.Add(edited);
            else endpoints[endpoints.IndexOf(existing)] = edited;
            grid.SelectedItem = edited;
        }
    }

    private void Apply() {
        if (!CanEdit()) return;
        try {
            device.SaveConfiguration(new SafetyConfiguration { Endpoints = endpoints.ToArray() }, profileIdentity);
            notice.Text = "Settings applied. While connected, the last result is retained during refresh within its original missing-update deadline.";
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { notice.Text = exception.Message; }
    }

    private async Task TestSelectedAsync() {
        if (!CanApplyOrTest() || Selected is not { } endpoint) return;
        testing = true;
        UpdateControls();
        device.RecordDiagnostic("TestStarted", new { endpoint.Id, endpoint.Label, endpoint.ConnectionPolicy });
        notice.Text = "Testing selected source; no live safety cache will be updated…";
        try {
            using var client = new AlpacaSafetyClient(endpoint);
            var result = await client.PollAsync(cancellation.Token);
            device.RecordDiagnostic(result.Outcome == PollOutcome.Observation ? "TestCompleted" : "TestFailed",
                new { endpoint.Id, endpoint.Label, result });
            notice.Text = $"Test: {result.Outcome}; interface={result.InterfaceVersion}; connected={result.Connected}; IsSafe={result.IsSafe}; " +
                $"latency={result.Received - result.RequestStarted:F3}s; {result.Reason}. Server transaction={result.ServerTransactionId}.";
        } catch (OperationCanceledException) { device.RecordDiagnostic("TestCancelled", new { endpoint.Id }); }
        catch {
            device.RecordDiagnostic("TestFailed", new { endpoint.Id, reason = "Unexpected test error; check source configuration." });
            notice.Text = "Test failed. Check the endpoint settings; no live safety state was changed.";
        }
        finally { testing = false; UpdateControls(); }
    }

    private void RefreshStatus() {
        UpdateControls();
        var current = device.GetSnapshot();
        live.Text = current is null ? "DISCONNECTED — no live safety monitoring" :
            (current.IsSafe ? "SAFE — " : "UNSAFE — ") + current.Summary;
        var remaining = device.TraceRemaining;
        ((TextBlock)traceButton.Content).Text = remaining > TimeSpan.Zero ? "Stop poll tracing" : "Trace polls for 5 minutes";
        traceStatus.Text = remaining > TimeSpan.Zero
            ? $"Tracing polls and retries to NINA's log and Recent events. Stops automatically in {remaining:mm\\:ss}."
            : "Normal logging includes retries, missed checks, recovery and state changes. Tracing adds every poll result.";
        var historyText = string.Join(Environment.NewLine + Environment.NewLine, device.DiagnosticEvents.Reverse()
            .Select(e => $"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}  {e.Level}  {e.Event}\n{e.Details}"));
        if (eventHistory.Text != historyText) eventHistory.Text = historyText;
        var snapshot = device.GetSnapshot();
        IReadOnlyList<SafetyEndpointOptions> applied = Array.Empty<SafetyEndpointOptions>();
        try { applied = device.LoadConfiguration().Endpoints; } catch (ArgumentException) { }
        if (current is not null) {
            var activity = DescribeRetryActivity(current.Endpoints, applied);
            if (activity.Length > 0) live.Text += " | " + activity;
        }
        diagnostics.Text = snapshot is null ? "Monitor disconnected. Connect from NINA to see live source status.\nUse Sources → Test source for a one-time check while disconnected. Test results appear in Recent events." :
            string.Join(Environment.NewLine + Environment.NewLine, snapshot.Endpoints.Select(s => {
                var config = applied.FirstOrDefault(e => e.Id == s.Id);
                string Seconds(double? value) => value is double v ? $"{v:F1} s" : "—";
                var poll = s.LastPoll;
                return $"{s.Label} — {s.Phase} — {(s.PermitsSafe ? "permits safety" : "blocks safety")}\n{s.Reason}\n" +
                    $"Network polling: {config?.PollSeconds} s between normal cycles. Screen refresh: 0.5 s (no network request).\n" +
                    $"Latest poll: {poll?.Outcome.ToString() ?? "waiting"}; {Seconds(s.LastPollAgeSeconds)} ago; " +
                    $"latency {(poll is null ? "—" : $"{(poll.Received - poll.RequestStarted) * 1000:F0} ms")}\n" +
                    $"Last raw safety: {s.RawIsSafe?.ToString() ?? "unknown"}; safe evidence age {Seconds(s.SafeAgeSeconds)} / {config?.MaximumSafeAgeSeconds} s\n" +
                    $"Unsafe readings {s.UnsafeReadings}/{config?.UnsafeReadingsToUnsafe}; failed cycles {s.FailedCycles}/{config?.FailedCyclesToUnsafe}; " +
                    $"recovery {s.SafeReadings}/{config?.SafeReadingsToSafe} safe readings + {s.SafeHoldSeconds:F1}/{config?.ReturnToSafeHoldSeconds} s hold\n" +
                    $"{DescribeCheck(s, config?.AttemptsPerCycle ?? 1)}\n" +
                    $"Completed requests {s.CompletedPolls}; failed attempts {s.FailedPolls}\n" +
                    $"Interface {poll?.InterfaceVersion?.ToString() ?? "—"}; upstream connected {poll?.Connected?.ToString() ?? "unknown"}; " +
                    $"server transaction {poll?.ServerTransactionId?.ToString() ?? "—"}; Alpaca error {poll?.ErrorNumber?.ToString() ?? "—"}\n" +
                    $"Latest result: {poll?.Reason ?? "No request completed"}\n" +
                    $"HTTP pool lifetime: {config?.ConnectionLifetimeSeconds} s (maximum reuse age; not a socket-renewal counter).";
            }));
    }

    public static string DescribeRetryActivity(IReadOnlyList<EndpointSafetySnapshot> sources,
        IReadOnlyList<SafetyEndpointOptions> applied) => string.Join("; ", sources.Select(source => {
            var attempts = applied.FirstOrDefault(e => e.Id == source.Id)?.AttemptsPerCycle;
            var limit = attempts is int count ? $" of {count}" : "";
            if (source.CheckInProgress && source.NextRequestInSeconds is double delay)
                return $"{source.Label}: retry in {delay:F1} s (attempt {source.Attempt + 1}{limit})";
            if (source.CheckInProgress && source.Attempt > 1)
                return $"{source.Label}: retrying (attempt {source.Attempt}{limit})";
            if (!source.CheckInProgress && source.LastPoll?.Outcome == PollOutcome.TransientFailure)
                return $"{source.Label}: check missed after {source.Attempt} attempts";
            if (!source.CheckInProgress && source.LastPoll?.Outcome == PollOutcome.Observation && source.Attempt > 1)
                return $"{source.Label}: check recovered on attempt {source.Attempt}{limit}";
            return null;
        }).Where(text => text is not null));

    public static string DescribeCheck(EndpointSafetySnapshot source, int attemptsAllowed) {
        var missed = $"Missed checks: {source.FailedCycles}.";
        if (source.CheckInProgress) {
            if (source.NextRequestInSeconds is double delay)
                return $"RETRY in {delay:F1} s — attempt {source.Attempt + 1} of {attemptsAllowed}. This check is not yet missed. {missed}";
            return source.Attempt > 1
                ? $"RETRYING — attempt {source.Attempt} of {attemptsAllowed}. This check is not yet missed. {missed}"
                : $"Checking now (running or queued). {missed}";
        }
        var next = source.NextRequestInSeconds is double seconds ? $" Next check in {seconds:F1} s." : "";
        if (source.LastPoll?.Outcome == PollOutcome.TransientFailure)
            return $"CHECK MISSED — all {source.Attempt} attempts failed. {missed}{next}";
        if (source.LastPoll?.Outcome == PollOutcome.PermanentFailure)
            return $"SOURCE ERROR — {source.LastPoll.Reason}.{next}";
        if (source.LastPoll is null) return "Waiting for first check.";
        return (source.Attempt > 1 ? $"Check recovered on attempt {source.Attempt} of {attemptsAllowed}. " : "Last check completed. ") + missed + next;
    }

    private void ExportDiagnostics() {
        var dialog = new SaveFileDialog { Filter = "JSON diagnostics (*.json)|*.json", FileName = "field-kit-safety-diagnostics.json" };
        if (dialog.ShowDialog(this) != true) return;
        try {
            // No endpoint URLs, credential references, raw response bodies, or HTTP headers.
            File.WriteAllText(dialog.FileName, device.ExportDiagnosticReport());
            notice.Text = "Redacted diagnostics exported (endpoint labels and Boolean safety evidence are included).";
        } catch (IOException) { notice.Text = "Could not write diagnostics to the selected file."; }
        catch (UnauthorizedAccessException) { notice.Text = "The selected diagnostic file is not writable."; }
    }
}

public sealed class SafetyEndpointEditor : Window {
    public SafetyEndpointOptions? Result { get; private set; }
    private readonly Dictionary<string, TextBox> inputs = new();
    private readonly Dictionary<string, TabItem> fieldTabs = new();

    public SafetyEndpointEditor(SafetyEndpointOptions original) {
        Title = "Configure safety source"; Width = 760; Height = 720; MinWidth = 660; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = SafetyDialogLayout.Root(this);
        var heading = new StackPanel();
        heading.Children.Add(FieldKitIcon.Heading("Configure safety source"));
        heading.Children.Add(SafetyDialogLayout.Note("Start with the source address. Review safety behavior before saving; advanced network settings have defaults."));
        DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var footer = new StackPanel();
        var error = SafetyDialogLayout.Note("");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = SafetyDialogLayout.Button("Cancel"); cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = SafetyDialogLayout.Button("Use these settings");
        actions.Children.Add(cancel); actions.Children.Add(save);
        footer.Children.Add(error); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var tabs = new TabControl(); dock.Children.Add(tabs);
        (TabItem Tab, StackPanel Panel) Page(string title, string description) {
            var panel = new StackPanel { Margin = new Thickness(4, 12, 16, 8) };
            panel.Children.Add(SafetyDialogLayout.Note(description));
            var tab = new TabItem { Header = SafetyDialogLayout.TabHeading(title), Content = new ScrollViewer {
                Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
            tabs.Items.Add(tab);
            return (tab, panel);
        }
        void Field((TabItem Tab, StackPanel Panel) page, string name, string title, string hint) {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var label = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            var box = new TextBox { Name = name, Text = Convert.ToString(typeof(SafetyEndpointOptions).GetProperty(name)!.GetValue(original), CultureInfo.InvariantCulture) ?? "",
                VerticalAlignment = VerticalAlignment.Center, MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            if (name == "FailedCyclesToUnsafe") box.Text = (original.FailedCyclesToUnsafe - 1).ToString(CultureInfo.InvariantCulture);
            label.Children.Add(new Label { Content = title, Target = box, Padding = new Thickness(0), FontWeight = FontWeights.SemiBold });
            label.Children.Add(SafetyDialogLayout.Note(hint, 3));
            if (name is "Label" or "BaseUrl") {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumnSpan(label, 2); Grid.SetColumnSpan(box, 2); Grid.SetRow(box, 1);
                box.Margin = new Thickness(0, 4, 0, 0);
            } else Grid.SetColumn(box, 1);
            row.Children.Add(label); row.Children.Add(box); page.Panel.Children.Add(row);
            inputs.Add(name, box); fieldTabs.Add(name, page.Tab);
        }
        var source = Page("Source", "Enter the server address and the SafetyMonitor device number supplied by your observatory.");
        Field(source, "Label", "Source name", "A name you will recognize in NINA.");
        Field(source, "BaseUrl", "Server URL", "Example address: http://192.0.2.1:11111 (replace with your server) — omit /api/v1/…");
        Field(source, "DeviceNumber", "SafetyMonitor device number", "Use the SafetyMonitor device number assigned by your server.");
        // NINA's checkbox template is an ON/OFF switch and does not display Content.
        var enabled = new CheckBox { IsChecked = original.Enabled, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(enabled, "Require this source to be safe");
        var enableRow = new DockPanel { Margin = new Thickness(0, 10, 0, 14) };
        DockPanel.SetDock(enabled, Dock.Right); enableRow.Children.Add(enabled);
        enableRow.Children.Add(new Label { Content = "Enabled — require this source to be safe", Target = enabled, Padding = new Thickness(0, 4, 0, 4) });
        source.Panel.Children.Add(enableRow);
        var connection = new ComboBox { SelectedValuePath = "Tag", MinHeight = 32 };
        connection.Items.Add(new ComboBoxItem { Content = "No — read status only", Tag = ConnectionPolicy.ExternallyManaged });
        connection.Items.Add(new ComboBoxItem { Content = "Yes — send Connect when needed", Tag = ConnectionPolicy.Managed });
        connection.SelectedValue = original.ConnectionPolicy;
        source.Panel.Children.Add(new Label { Content = "Send Alpaca Connect commands?", Target = connection, Padding = new Thickness(0, 0, 0, 5) });
        source.Panel.Children.Add(connection);
        source.Panel.Children.Add(SafetyDialogLayout.Note("Choose No if the server already reports its device as connected. Choose Yes if the safety driver requires a Connect command before it can report status. Test follows this choice. HTTP connections renew automatically in either mode."));

        var safety = Page("Safety behavior", "Check the server at the interval below. Temporary errors use the missed-check allowance. A valid unsafe response is handled separately. Startup remains unsafe until confirmed.");
        Field(safety, "PollSeconds", "Check server every (seconds)", "Normal delay between checks. Default: 30 s. Errors may retry sooner; an unsafe change is noticed at the next check.");

        Field(safety, "UnsafeReadingsToUnsafe", "Unsafe readings before reporting unsafe", "1 reports unsafe on the first reading. Higher counts delay that response.");
        Field(safety, "FailedCyclesToUnsafe", "Missed checks tolerated", "Default: 2. Keep the last confirmed safe result through two failed checks; the third reports unsafe. Each check includes its retries.");
        Field(safety, "SafeReadingsToSafe", "Safe readings before allowing safety", "Consecutive successful safe readings are needed at startup and recovery.");
        Field(safety, "ReturnToSafeHoldSeconds", "Recovery hold (seconds)", "Safe readings must also span this duration to reduce repeated state changes.");
        var timing = SafetyDialogLayout.Note(""); safety.Panel.Children.Add(timing);

        var advanced = Page("Advanced", "Check frequency and missed-check tolerance are on Safety behavior. These limits control stalled updates and error retries.");

        Field(advanced, "MaximumSafeAgeSeconds", "Maximum time without a safe update (seconds)", "Hard limit if checks stall entirely. Default: 90 s — two missed 30-second checks, then the next check is due. This limit may report unsafe before the missed-check count is exceeded.");
        Field(advanced, "RequestTimeoutSeconds", "Request timeout (seconds)", "Time allowed for each HTTP request. Default: 1 s.");
        Field(advanced, "ConnectionLifetimeSeconds", "Renew HTTP connections (seconds)", "Maximum 1800 s (30 minutes). New requests replace expired pooled connections.");
        var retryPanel = new StackPanel();
        var retry = new Expander { Header = "Retry tuning", Content = retryPanel, Margin = new Thickness(0, 12, 0, 0) };
        advanced.Panel.Children.Add(retry);
        var retryPage = (advanced.Tab, retryPanel);
        Field(retryPage, "AttemptsPerCycle", "Attempts per poll cycle", "Includes the first request. Range: 1–10. Default: 3.");
        Field(retryPage, "InitialBackoffSeconds", "Initial retry delay (seconds)", "Randomized to spread retries. Default: 0.5 s.");
        Field(retryPage, "BackoffMultiplier", "Retry delay multiplier", "Increases delays after failures. Default: 2.");
        Field(retryPage, "BackoffCapSeconds", "Retry delay cap (seconds)", "Also sets permanent-error probe interval. Server Retry-After may exceed this.");
        retryPanel.Children.Add(SafetyDialogLayout.Note("To stop on the first request error, set attempts per cycle to 1 and missed checks tolerated to 0."));
        void ShowTiming() {
            timing.Text = $"Check every {inputs["PollSeconds"].Text} s. If updates fail, safe evidence expires at {inputs["MaximumSafeAgeSeconds"].Text} s of age; " +
                $"tolerate {inputs["FailedCyclesToUnsafe"].Text} missed check(s). {inputs["UnsafeReadingsToUnsafe"].Text} unsafe reading(s) withdraw safety. " +
                $"Recovery requires {inputs["SafeReadingsToSafe"].Text} safe readings AND a {inputs["ReturnToSafeHoldSeconds"].Text} s hold.";
        }
        foreach (var input in inputs.Values) input.TextChanged += (_, _) => ShowTiming();
        ShowTiming();
        void FocusField(string name) {
            tabs.SelectedItem = fieldTabs[name];
            if (retryPanel.Children.OfType<Grid>().Any(row => row.Children.Contains(inputs[name]))) retry.IsExpanded = true;
            Dispatcher.BeginInvoke(() => { inputs[name].BringIntoView(); inputs[name].Focus(); inputs[name].SelectAll(); });
        }
        save.Click += (_, _) => {
            string? parsing = null;
            try {
                var json = JObject.FromObject(original);
                foreach (var (name, input) in inputs) {
                    parsing = name;
                    var type = typeof(SafetyEndpointOptions).GetProperty(name)!.PropertyType;
                    json[name] = type == typeof(string) ? new JValue(input.Text.Trim()) : type == typeof(int)
                        ? new JValue(checked(int.Parse(input.Text, CultureInfo.InvariantCulture) + (name == "FailedCyclesToUnsafe" ? 1 : 0))) : new JValue(double.Parse(input.Text, CultureInfo.InvariantCulture));
                }
                parsing = null;
                json["Enabled"] = enabled.IsChecked == true;
                json["ConnectionPolicy"] = (int)(connection.SelectedValue ?? ConnectionPolicy.ExternallyManaged);
                var result = json.ToObject<SafetyEndpointOptions>()!;
                result.Validate();
                Result = result;
                DialogResult = true;
            } catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or JsonException) {
                var field = parsing ?? inputs.Keys.FirstOrDefault(key => exception.Message.Contains(key, StringComparison.Ordinal));
                if (field is not null) FocusField(field);
                error.Text = parsing is null ? "Check settings: " + exception.Message : "Enter a valid number in the highlighted field (use a decimal point for fractions).";
            }
        };
    }
}

internal static class SafetyDialogLayout {
    internal static TextBlock TabHeading(string title) {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("PrimaryBrush")));
        foreach (var property in new[] { "IsSelected", "IsMouseOver" }) {
            var trigger = new DataTrigger { Binding = new System.Windows.Data.Binding(property) {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(TabItem), 1) }, Value = true };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("ButtonForegroundBrush")));
            style.Triggers.Add(trigger);
        }
        return new TextBlock { Text = title, Style = style, Margin = new Thickness(3, 5, 3, 5) };
    }
    internal static Button Button(string title) {
        // NINA's button template ignores Padding and expects explicitly colored content.
        var text = new TextBlock { Text = title, Margin = new Thickness(12, 8, 12, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForegroundBrush");
        return new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), VerticalContentAlignment = VerticalAlignment.Center };
    }
    // Resolve the host's live profile brushes; never substitute Windows' light palette.
    internal static DockPanel Root(Window window) {
        window.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "PrimaryBrush");
        window.FontSize = 14;
        window.Icon = FieldKitIcon.WindowIcon();
        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(20) };
        panel.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        panel.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "PrimaryBrush");
        window.Content = panel;
        return panel;
    }

    internal static TextBlock Note(string text, double margin = 8) => new() {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, margin, 0, margin), FontSize = 13
    };
}
