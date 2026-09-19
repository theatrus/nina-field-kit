using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Nina.FieldKit.Core.Safety;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Nina.FieldKit.Plugin.Safety;

public enum SafetyOutputOverride { Passthrough, Safe, Unsafe }

public sealed class FieldKitSafetyMonitor : BaseINPC, ISafetyMonitor, IDisposable {
    private readonly object gate = new();
    private readonly IProfileService profiles;
    private readonly IPluginOptionsAccessor settings;
    private SafetyMonitorService? service;
    private bool disposed;
    private SafetyOutputOverride outputOverride;
    private string status = "Disconnected";
    private readonly SafetyDiagnosticJournal journal = new();
    public TimeSpan TraceRemaining => journal.TraceRemaining;
    public SafetyDiagnosticEvent[] DiagnosticEvents => journal.Snapshot();

    public void SetDiagnosticTrace(bool enabled) {
        journal.SetTrace(enabled);
        RecordDiagnostic(enabled ? "TraceEnabled" : "TraceDisabled", new { durationMinutes = enabled ? 5 : 0 });
    }

    public void RecordDiagnostic(string name, object evidence) {
        try {
            if (name is "PollCompleted" && journal.TraceRemaining == TimeSpan.Zero) return;
            var details = JsonConvert.SerializeObject(evidence, JsonSettings);
            var level = name is "ServiceFault" or "ConnectFailed" or "TestFailed" ? "Error" : "Info";
            if (name == "OutputOverrideChanged") level = "Warning";
            if (name == "AggregateTransition" && JObject.Parse(details).Value<bool?>("IsSafe") == false) level = "Warning";
            if (name == "EndpointTransition" && (string?)JObject.Parse(details)["endpoint"]?["Phase"] is "Faulted" or "Stale" or "GraceSafe" or "Unsafe") level = "Warning";
            var entry = journal.Add(name, details, level,
                traceOnly: name is "PollCompleted");
            if (entry is null) return;
            var message = "FieldKitSafety " + JsonConvert.SerializeObject(entry);
            // Explicit short-lived tracing uses Info so it works with NINA's normal log level.
            if (level == "Error") Logger.Error(message);
            else if (level == "Warning") Logger.Warning(message);
            else Logger.Info(message);
        } catch { /* Diagnostic sinks cannot alter equipment or safety state. */ }
    }

    public string ExportDiagnosticReport() {
        SafetyConfiguration? config = null;
        try { config = LoadConfiguration(); } catch (ArgumentException) { }
        return JsonConvert.SerializeObject(new {
            timestamp = DateTimeOffset.UtcNow, pluginVersion = DriverVersion,
            ninaVersion = typeof(IProfileService).Assembly.GetName().Version?.ToString(),
            traceRemainingSeconds = TraceRemaining.TotalSeconds, outputOverride = OutputOverride, reportedIsSafe = IsSafe,
            snapshot = GetSnapshot(), events = DiagnosticEvents,
            revision = config?.Revision, policies = config?.Endpoints.Select(e => new {
                e.Id, e.Label, e.Enabled, e.DeviceNumber, e.ConnectionPolicy, e.PollSeconds, e.RequestTimeoutSeconds,
                e.AttemptsPerCycle, e.InitialBackoffSeconds, e.BackoffMultiplier, e.BackoffCapSeconds,
                e.MaximumSafeAgeSeconds, e.ConnectionLifetimeSeconds, e.FailedCyclesToUnsafe,
                e.UnsafeReadingsToUnsafe, e.SafeReadingsToSafe, e.ReturnToSafeHoldSeconds
            }),
            note = "URLs, credentials, HTTP headers and response bodies are omitted. Source labels are included."
        }, Formatting.Indented, JsonSettings);
    }
    private static readonly JsonSerializerSettings JsonSettings = new() { Converters = { new StringEnumConverter() } };
    public const string SettingsKey = "AlpacaSafetyConfigurationV1";
    public const string PluginId = "d2487d32-9277-45ec-b5df-89ecbff61c78";

    public FieldKitSafetyMonitor(IProfileService profiles) {
        this.profiles = profiles;
        settings = new PluginOptionsAccessor(profiles, Guid.Parse(PluginId));
        profiles.BeforeProfileChanging += ProfileChanging;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    public string Id => "c3eab984-eadf-4564-aa85-e0196f08d424";
    public string Name => "Field Kit Alpaca Safety Monitor";
    public string DisplayName => Name;
    public string Category => "NINA Field Kit";
    public string Description => "Combines your enabled safety sources into one safe/unsafe result.";
    public string DriverInfo => "Background safety checks. Open Setup for settings and diagnostics.";
    public string DriverVersion => typeof(FieldKitPlugin).Assembly.GetName().Version!.ToString();
    public bool HasSetupDialog => true;
    public bool Connected { get { lock (gate) return service is not null; } }
    public bool IsSafe { get { lock (gate) return service is not null && (outputOverride switch {
        SafetyOutputOverride.Safe => true, SafetyOutputOverride.Unsafe => false, _ => service.Snapshot().IsSafe
    }); } }
    public SafetyOutputOverride OutputOverride { get { lock (gate) return outputOverride; } }
    public string Status { get { lock (gate) {
        var snapshot = service?.Snapshot();
        if (snapshot is null) return status;
        return outputOverride == SafetyOutputOverride.Passthrough ? snapshot.Summary :
            $"OVERRIDE {outputOverride.ToString().ToUpperInvariant()} — sources {(snapshot.IsSafe ? "SAFE" : "UNSAFE")}: {snapshot.Summary}";
    } } }

    public void SetOutputOverride(SafetyOutputOverride value, object expectedProfile) {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(expectedProfile, profiles.ActiveProfile)) throw new InvalidOperationException("Profile changed; reopen setup.");
            if (service is null) throw new InvalidOperationException("Connect the monitor before changing its output override.");
            if (outputOverride == value) return;
            var previous = outputOverride;
            outputOverride = value;
            RecordDiagnostic("OutputOverrideChanged", new { previous, current = value, reportedIsSafe = IsSafe, sourceIsSafe = service.Snapshot().IsSafe });
        }
        RaiseStateChanged();
    }
    public object ProfileIdentity => profiles.ActiveProfile;
    public IList<string> SupportedActions => Array.Empty<string>();

    public SafetyConfiguration LoadConfiguration() {
        lock (gate) {
            var json = settings.GetValueString(SettingsKey, "");
            if (json.Length == 0) return new SafetyConfiguration();
            try {
                var config = JsonConvert.DeserializeObject<SafetyConfiguration>(json, JsonSettings)
                    ?? throw new ArgumentException("Empty safety configuration.");
                return config.Freeze();
            } catch {
                throw new ArgumentException("Stored safety configuration is invalid. Review and replace it while disconnected.");
            }
        }
    }

    public void SaveConfiguration(SafetyConfiguration configuration, object expectedProfile) {
        var frozen = (configuration with { Revision = Guid.NewGuid() }).Freeze();
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(expectedProfile, profiles.ActiveProfile)) throw new InvalidOperationException("Profile changed; reopen setup before applying settings.");
            var replacement = service?.CreateReplacement(frozen);
            try { settings.SetValueString(SettingsKey, JsonConvert.SerializeObject(frozen, JsonSettings)); }
            catch { replacement?.Dispose(); throw; }
            if (replacement is not null) {
                var retired = service!;
                retired.Changed -= RaiseStateChanged;
                service = replacement;
                replacement.Changed += RaiseStateChanged;
                retired.Dispose();
                replacement.Start();
                RecordDiagnostic("ConfigurationAppliedLive", new { frozen.Revision, reason = "Previous state retained within its original freshness deadline while new sources refresh" });
            }
            status = "Settings saved";
        }
        RecordDiagnostic("ConfigurationSaved", new { frozen.Revision, sources = frozen.Endpoints.Select(e => new {
            e.Id, e.Label, e.Enabled, e.ConnectionPolicy, e.MaximumSafeAgeSeconds, e.ConnectionLifetimeSeconds }) });
        RaiseStateChanged();
    }

    public Task<bool> Connect(CancellationToken token) {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            if (service is not null) return Task.FromResult(true);
            try {
                var config = LoadConfiguration().Freeze();
                var created = new SafetyMonitorService(config, log: RecordDiagnostic);
                created.Changed += RaiseStateChanged;
                service = created;
                created.Start();
            } catch (ArgumentException exception) {
                status = exception.Message;
                RecordDiagnostic("ConnectFailed", new { reason = "Configuration is invalid; review source settings." });
                return Task.FromResult(false);
            }
        }
        RaiseStateChanged();
        return Task.FromResult(true);
    }

    public void Disconnect() {
        SafetyMonitorService? retired;
        lock (gate) {
            retired = service;
            service = null; // Immediate unsafe, before waiting for HTTP cancellation.
            if (outputOverride != SafetyOutputOverride.Passthrough) {
                RecordDiagnostic("OutputOverrideReset", new { previous = outputOverride, reason = "Monitor disconnected" });
                outputOverride = SafetyOutputOverride.Passthrough;
            }
            status = "Disconnected; safety history cleared";
        }
        if (retired is not null) {
            retired.Changed -= RaiseStateChanged;
            retired.Dispose();
        }
        RaiseStateChanged();
    }

    public SafetyMonitorSnapshot? GetSnapshot() { lock (gate) return service?.Snapshot(); }
    private void ProfileChanging(object? sender, EventArgs e) {
        RecordDiagnostic("ProfileChanging", new { reason = "Disconnecting and clearing safety evidence" });
        journal.SetTrace(false);
        Disconnect();
    }
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) {
            RecordDiagnostic("PowerChanged", new { mode = e.Mode.ToString(), reason = "Fresh connection required" });
            Disconnect();
        }
    }

    private void RaiseStateChanged() {
        void Raise() { RaisePropertyChanged(nameof(Connected)); RaisePropertyChanged(nameof(IsSafe)); RaisePropertyChanged(nameof(Status)); RaisePropertyChanged(nameof(OutputOverride)); }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)Raise);
        } else Raise();
    }

    public void SetupDialog() {
        void Show() { new SafetySetupWindow(this).ShowDialog(); }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.Invoke(Show);
        else Show();
    }

    public string Action(string actionName, string actionParameters) => throw new NotSupportedException("No device-specific actions are exposed.");
    public string SendCommandString(string command, bool raw = true) => throw new NotSupportedException();
    public bool SendCommandBool(string command, bool raw = true) => throw new NotSupportedException();
    public void SendCommandBlind(string command, bool raw = true) => throw new NotSupportedException();
    public void Dispose() {
        lock (gate) { if (disposed) return; disposed = true; }
        profiles.BeforeProfileChanging -= ProfileChanging;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        Disconnect();
    }
}
