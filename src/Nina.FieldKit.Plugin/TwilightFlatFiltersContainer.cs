using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows.Input;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin.SkyFlats;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.Utility;

namespace Nina.FieldKit.Plugin;

[JsonObject(MemberSerialization.OptIn)]
public sealed class FlatFilterSelection : INotifyPropertyChanged {
    private bool enabled;
    [JsonConstructor]
    public FlatFilterSelection(string name, int position, bool enabled = true) {
        Name = name; Position = position; this.enabled = enabled;
    }
    [JsonProperty] public string Name { get; }
    [JsonProperty] public int Position { get; }
    [JsonProperty] public bool Enabled {
        get => enabled;
        set { if (enabled == value) return; enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public FlatFilterSelection Copy() => new(Name, Position, Enabled);
}

[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[ExportMetadata("Name", "Twilight flats by filter and rotation")]
[ExportMetadata("Description", "Run Twilight Sky Flats at every captured rotation for each filter, in your chosen order.")]
[ExportMetadata("Icon", "FlatWizardSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class TwilightFlatFiltersContainer : SkyFlatRotationsContainer {
    private readonly INightRotationHistory history;
    private readonly IProfileService profiles;
    private readonly IRotatorMediator rotator;
    private readonly ICameraMediator camera;
    private readonly ISafetyMonitorMediator safety;
    private readonly IFilterWheelMediator wheel;
    private ObservableCollection<FlatFilterSelection> filterOrder = new();
    private readonly HashSet<FlatFilterSelection> observed = new();
    private FlatFilterSelection? runningFilter;
    private string? runningWheelId;
    private string filterStatus = "No filter active";
    private bool executing;

    [ImportingConstructor]
    public TwilightFlatFiltersContainer(INightRotationHistory history, IProfileService profiles,
        IRotatorMediator rotator, ICameraMediator camera, ISafetyMonitorMediator safety, IFilterWheelMediator wheel)
        : base(history, profiles, rotator, camera, safety) {
        this.history = history; this.profiles = profiles; this.rotator = rotator;
        this.camera = camera; this.safety = safety; this.wheel = wheel;
        filterOrder.CollectionChanged += FilterOrderChanged;
        RefreshFilters();
    }
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public ObservableCollection<FlatFilterSelection> FilterOrder {
        get => filterOrder;
        private set {
            filterOrder.CollectionChanged -= FilterOrderChanged;
            filterOrder = value ?? new();
            filterOrder.CollectionChanged += FilterOrderChanged;
            ObserveRows(); RaisePropertyChanged();
        }
    }
    public bool CanEditFilters => !executing;
    public string FilterStatus { get => filterStatus; private set { filterStatus = value; RaisePropertyChanged(); } }
    public ICommand RefreshFiltersCommand => new GalaSoft.MvvmLight.Command.RelayCommand(RefreshFilters, () => CanEditFilters);
    public ICommand MoveFilterUpCommand => new GalaSoft.MvvmLight.Command.RelayCommand<FlatFilterSelection>(row => MoveFilter(row, -1), row => CanMove(row, -1));
    public ICommand MoveFilterDownCommand => new GalaSoft.MvvmLight.Command.RelayCommand<FlatFilterSelection>(row => MoveFilter(row, 1), row => CanMove(row, 1));
    private IEnumerable<FilterInfo> KnownFilters => profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters?.AsEnumerable() ?? [];
    private void FilterOrderChanged(object? sender, NotifyCollectionChangedEventArgs args) {
        ObserveRows(); RaisePropertyChanged(nameof(FilterOrder));
    }
    private void ObserveRows() {
        foreach (var row in observed) row.PropertyChanged -= FilterChanged;
        observed.Clear();
        foreach (var row in filterOrder) if (row is not null && observed.Add(row)) row.PropertyChanged += FilterChanged;
    }
    private void FilterChanged(object? sender, PropertyChangedEventArgs args) => RaisePropertyChanged(nameof(FilterOrder));
    private bool CanMove(FlatFilterSelection? row, int step) {
        if (!CanEditFilters || row is null) return false;
        var index = FilterOrder.IndexOf(row);
        return index >= 0 && index + step >= 0 && index + step < FilterOrder.Count;
    }
    private void MoveFilter(FlatFilterSelection row, int step) {
        if (CanMove(row, step)) { var index = FilterOrder.IndexOf(row); FilterOrder.Move(index, index + step); }
    }
    private void RefreshFilters() {
        if (!CanEditFilters) return;
        var known = KnownFilters.Where(f => f.Position >= 0 && !string.IsNullOrWhiteSpace(f.Name)).ToArray();
        var old = FilterOrder.ToArray();
        var rows = old.Where(row => row is not null && known.Any(f => f.Position == row.Position && f.Name == row.Name)).ToList();
        foreach (var filter in known)
            if (!rows.Any(row => row.Position == filter.Position && row.Name == filter.Name))
                rows.Add(new(filter.Name, filter.Position, enabled: old.Length == 0));
        FilterOrder = new(rows);
    }
    private FlatFilterSelection[] CaptureFilters() {
        if (FilterOrder.Any(row => row is null)) throw new ArgumentException("The filter list is invalid; refresh profile filters.");
        var plan = FilterOrder.Where(row => row.Enabled).Select(row => row.Copy()).ToArray();
        if (plan.Length == 0) throw new ArgumentException("Enable at least one filter in the ordered list.");
        if (plan.Select(row => row.Position).Distinct().Count() != plan.Length)
            throw new ArgumentException("Each enabled filter position must appear once.");
        foreach (var row in plan) _ = ResolveFilter(row);
        return plan;
    }
    private FilterInfo ResolveFilter(FlatFilterSelection row) => KnownFilters.SingleOrDefault(f =>
        f.Position == row.Position && f.Name == row.Name) ?? throw new ArgumentException(
            $"Filter '{row.Name}' no longer matches this profile. Refresh profile filters and review the order.");
    private SkyFlat[] TwilightInstructions() {
        var found = new List<SkyFlat>();
        void Visit(ISequenceContainer container) {
            foreach (var item in container.GetItemsSnapshot().Where(item => item.Status != SequenceEntityStatus.DISABLED)) {
                if (item is SkyFlat flat) { found.Add(flat); continue; }
                if (item is SwitchFilter) throw new ArgumentException("Remove Switch Filter instructions; this container chooses each filter.");
                if (item is SkyFlatRotationsContainer) throw new ArgumentException("Add Twilight Sky Flats directly; this container already repeats captured rotations.");
                if (item is ISequenceContainer nested) Visit(nested);
            }
        }
        Visit(this);
        if (found.Count == 0) throw new ArgumentException("Add NINA's Twilight Sky Flats instruction inside this container.");
        return found.ToArray();
    }
    public override bool Validate() {
        _ = base.Validate(); var errors = Issues.ToList();
        try { _ = CaptureFilters(); _ = TwilightInstructions(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { errors.Add(e.Message); }
        if (wheel.GetInfo()?.Connected != true) errors.Add("Connect a filter wheel for twilight flats by filter.");
        Issues = errors; RaisePropertyChanged(nameof(Issues)); return errors.Count == 0;
    }
    public override object Clone() {
        var clone = new TwilightFlatFiltersContainer(history, profiles, rotator, camera, safety, wheel) {
            FilterOrder = new(FilterOrder.Select(row => row.Copy()))
        };
        CopyLoopTo(clone); return clone;
    }
    protected override void CheckEquipment(FlatRotationScope scope, CancellationToken token) {
        base.CheckEquipment(scope, token);
        if (camera.GetInfo()?.Connected != true) throw new SequenceEntityFailedException("Connect a camera for twilight flats.");
        var info = wheel.GetInfo();
        if (info?.Connected != true || info.IsMoving) throw new SequenceEntityFailedException("The filter wheel must be connected and stationary.");
        if (runningWheelId is not null && (profiles.ActiveProfile.FilterWheelSettings.Id != runningWheelId ||
            (!string.IsNullOrEmpty(info.DeviceId) && info.DeviceId != runningWheelId)))
            throw new SequenceEntityFailedException("The filter wheel changed during twilight flats.");
        if (runningFilter is not null) {
            _ = ResolveFilter(runningFilter);
            if (info.SelectedFilter?.Position != runningFilter.Position || info.SelectedFilter.Name != runningFilter.Name)
                throw new SequenceEntityFailedException("The filter wheel did not retain the requested filter; flat instructions were not started.");
        }
    }
    internal override async Task RunRotations(IProgress<ApplicationStatus> progress, CancellationToken token) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var profileChanged = 0;
        void ProfileChanging(object? sender, EventArgs args) {
            Interlocked.Exchange(ref profileChanged, 1);
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }
        var originals = new List<(SwitchFilter Control, FilterInfo Filter)>();
        var log = new RunDiagnostics("TwilightFlats");
        profiles.BeforeProfileChanging += ProfileChanging;
        executing = true; RaisePropertyChanged(nameof(CanEditFilters));
        FilterStatus = "Preparing filter and rotation plan";
        try {
            lifetime.Token.ThrowIfCancellationRequested();
            var filters = CaptureFilters(); var run = CaptureRun(); var instructions = TwilightInstructions();
            runningWheelId = profiles.ActiveProfile.FilterWheelSettings.Id;
            if (string.IsNullOrWhiteSpace(runningWheelId) || runningWheelId == "No_Device")
                throw new SequenceEntityFailedException("Select a filter wheel in this profile.");
            log.Write("Plan", new { filters = filters.Select(f => new { f.Name, f.Position }), run.Plan.Scope, run.Plan.Night, run.Plan.Angles });
            if (run.Plan.Angles.Length == 0) { FilterStatus = "No filters run"; LastResult = "No captured rotations for this night; skipped"; return; }
            originals.AddRange(instructions.Select(flat => (flat.GetSwitchFilterItem(), flat.GetSwitchFilterItem().Filter)));
            for (var index = 0; index < filters.Length; index++) {
                lifetime.Token.ThrowIfCancellationRequested(); runningFilter = null;
                ResetForRotation();
                if (!ConditionsPermit(Items.FirstOrDefault(item => item.Status == SequenceEntityStatus.CREATED))) {
                    LastResult = "Stopped by sequence condition"; log.Write("ConditionStopped", new { index }); return;
                }
                CheckEquipment(run.Plan.Scope, lifetime.Token);
                var filter = filters[index];
                FilterStatus = $"Filter {index + 1}/{filters.Length}: {filter.Name}";
                LastResult = $"Selecting {filter.Name}";
                progress?.Report(new ApplicationStatus { Status = LastResult });
                log.Write("FilterMoveStarted", new { index, filter.Name, filter.Position });
                using (var change = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    change.CancelAfter(TimeSpan.FromSeconds(run.Timeout));
                    FilterInfo result;
                    try { result = await wheel.ChangeFilter(ResolveFilter(filter), change.Token, progress); }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && change.IsCancellationRequested) {
                        throw new SequenceEntityFailedException("Filter change timed out; flat instructions were not started.");
                    }
                    if (change.IsCancellationRequested && !lifetime.IsCancellationRequested)
                        throw new SequenceEntityFailedException("Filter change timed out; flat instructions were not started.");
                    change.Token.ThrowIfCancellationRequested();
                    if (result?.Position != filter.Position || result.Name != filter.Name)
                        throw new SequenceEntityFailedException("The filter change did not reach the requested filter.");
                }
                runningFilter = filter; CheckEquipment(run.Plan.Scope, lifetime.Token);
                foreach (var flat in instructions) flat.GetSwitchFilterItem().Filter = ResolveFilter(filter);
                log.Write("FilterInstructionsStarted", new { index, filter.Name });
                if (!await RunRotationLoop(progress, lifetime.Token, run)) return;
                log.Write("FilterCompleted", new { index, filter.Name });
            }
            LastResult = $"Completed {filters.Length} filters at {run.Plan.Angles.Length} captured rotations";
            FilterStatus = "Filter sequence complete";
            log.Write("Completed", new { filters = filters.Length, rotations = run.Plan.Angles.Length });
        } catch (OperationCanceledException) when (Volatile.Read(ref profileChanged) != 0) {
            LastResult = "Profile changed; twilight flats stopped"; log.Write("ProfileChanged");
            throw new SequenceEntityFailedException(LastResult);
        } catch (OperationCanceledException) {
            LastResult = "Twilight flats cancelled"; log.Write("Cancelled"); throw;
        } catch (Exception e) {
            LastResult = "Failed: " + e.Message; log.Write("Failure", e.ToString()); throw;
        } finally {
            // Restore instruction configuration only. Cleanup never commands a filter or rotator move.
            foreach (var original in originals) original.Control.Filter = original.Filter;
            runningFilter = null; runningWheelId = null;
            profiles.BeforeProfileChanging -= ProfileChanging;
            executing = false; RaisePropertyChanged(nameof(CanEditFilters));
            progress?.Report(new ApplicationStatus());
        }
    }
}
