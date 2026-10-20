using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Windows.Input;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using Nina.FieldKit.Plugin.SkyFlats;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceItem))]
[Export(typeof(ISequenceContainer))]
[ExportMetadata("Name", "Sky flats at captured rotations")]
[ExportMetadata("Description", "Run the instructions inside at each mechanical rotation captured during the night.")]
[ExportMetadata("Icon", "RotatorSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public class SkyFlatRotationsContainer : SequenceContainer {
    private readonly INightRotationHistory history;
    private readonly IProfileService profiles;
    private readonly IRotatorMediator rotator;
    private readonly ICameraMediator camera;
    private readonly ISafetyMonitorMediator safety;
    private int nightOffset;
    private double tolerance = .1, moveTimeoutSeconds = 180;
    private string lastResult = "Run after light imaging; add your flat instructions inside";
    [ImportingConstructor]
    public SkyFlatRotationsContainer(INightRotationHistory history, IProfileService profiles, IRotatorMediator rotator,
        ICameraMediator camera, ISafetyMonitorMediator safety) : base(new FlatRotationStrategy()) {
        this.history = history; this.profiles = profiles; this.rotator = rotator; this.camera = camera; this.safety = safety;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }
    [JsonProperty] public int NightOffset { get => nightOffset; set { nightOffset = value; RaisePropertyChanged(); } }
    [JsonProperty] public double ToleranceDegrees { get => tolerance; set { tolerance = value; RaisePropertyChanged(); } }
    [JsonProperty] public double MoveTimeoutSeconds { get => moveTimeoutSeconds; set { moveTimeoutSeconds = value; RaisePropertyChanged(); } }
    public string LastResult { get => lastResult; protected set { lastResult = value; RaisePropertyChanged(); } }
    public ICommand PreviewCommand => new GalaSoft.MvvmLight.Command.RelayCommand(() => {
        try { var plan = history.GetPlan(NightOffset, ToleranceDegrees); LastResult = Describe(plan); }
        catch (Exception e) { LastResult = e.Message; }
    }, () => Status != SequenceEntityStatus.RUNNING);
    private static string Describe(FlatRotationPlan plan) => $"Night {plan.Night:yyyy-MM-dd}: " +
        (plan.Angles.Length == 0 ? "no captured rotations" : string.Join(", ", plan.Angles.Select(a => $"{a:0.##}°")));
    private void ValidateSettings() {
        if (NightOffset is < -1 or > 0) throw new ArgumentException("Choose the current or previous observing night.");
        _ = FlatRotations.Distinct([], ToleranceDegrees);
        if (!double.IsFinite(MoveTimeoutSeconds) || MoveTimeoutSeconds is < 5 or > 1800)
            throw new ArgumentException("Move timeout must be between 5 and 1800 seconds.");
    }
    public override bool Validate() {
        var errors = new List<string>();
        try { ValidateSettings(); } catch (ArgumentException e) { errors.Add(e.Message); }
        if (rotator.GetInfo()?.Connected != true) errors.Add("Connect a rotator for the flat rotation loop.");
        if (!base.Validate()) errors.Add("Review the instructions, conditions and triggers inside this container.");
        Issues = errors; RaisePropertyChanged(nameof(Issues)); return errors.Count == 0;
    }
    public override void AfterParentChanged() { base.AfterParentChanged(); Validate(); }
    public override object Clone() {
        var clone = new SkyFlatRotationsContainer(history, profiles, rotator, camera, safety);
        CopyLoopTo(clone);
        return clone;
    }
    protected void CopyLoopTo(SkyFlatRotationsContainer clone) {
        clone.NightOffset = NightOffset; clone.ToleranceDegrees = ToleranceDegrees; clone.MoveTimeoutSeconds = MoveTimeoutSeconds;
        clone.IsExpanded = IsExpanded;
        clone.Items = new ObservableCollection<ISequenceItem>(Items.Select(i => (ISequenceItem)i.Clone()));
        clone.Conditions = new ObservableCollection<ISequenceCondition>(Conditions.Select(i => (ISequenceCondition)i.Clone()));
        clone.Triggers = new ObservableCollection<ISequenceTrigger>(Triggers.Select(i => (ISequenceTrigger)i.Clone()));
        clone.CopyMetaData(this);
        foreach (var item in clone.Items) item.AttachNewParent(clone);
        foreach (var condition in clone.Conditions) condition.AttachNewParent(clone);
        foreach (var trigger in clone.Triggers) trigger.AttachNewParent(clone);
    }
    protected bool ConditionsPermit(ISequenceItem? next) {
        for (ISequenceContainer? container = this; container is not null; container = container.Parent) {
            if (container is not IConditionable conditionable) continue;
            var active = conditionable.GetConditionsSnapshot().Any(c => c.Status != SequenceEntityStatus.DISABLED);
            if (active && !conditionable.CheckConditions(null!, next!)) return false;
            if (!active && container != this && container.Iterations >= 1) return false;
        }
        return true;
    }
    protected void ResetForRotation() {
        // Native ResetAll/ResetConditions also enable disabled conditions and triggers.
        // Replaying an angle must preserve the user's disabled selections throughout the body.
        var restore = new List<Action>();
        void RememberDisabled(ISequenceContainer container) {
            foreach (var item in container.GetItemsSnapshot()) {
                if (item.Status == SequenceEntityStatus.DISABLED) restore.Add(() => item.Status = SequenceEntityStatus.DISABLED);
                if (item is ISequenceContainer nested) RememberDisabled(nested);
            }
            if (container is IConditionable conditions) foreach (var condition in conditions.GetConditionsSnapshot())
                if (condition.Status == SequenceEntityStatus.DISABLED) restore.Add(() => condition.Status = SequenceEntityStatus.DISABLED);
            if (container is ITriggerable triggers) foreach (var trigger in triggers.GetTriggersSnapshot()) {
                if (trigger.Status == SequenceEntityStatus.DISABLED) restore.Add(() => trigger.Status = SequenceEntityStatus.DISABLED);
                if (trigger is SequenceTrigger native && native.TriggerRunner is not null) RememberDisabled(native.TriggerRunner);
            }
        }
        RememberDisabled(this);
        try {
            foreach (var item in GetItemsSnapshot()) {
                if (item is ISequenceContainer nested) nested.ResetAll(); else item.ResetProgress();
            }
            ResetConditions();
            foreach (var trigger in GetTriggersSnapshot()) if (trigger.Status != SequenceEntityStatus.DISABLED) trigger.Status = SequenceEntityStatus.CREATED;
            Iterations = 0;
        } finally { foreach (var reset in restore) reset(); }
    }
    protected virtual void CheckEquipment(FlatRotationScope scope, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (history.Scope != scope) throw new SequenceEntityFailedException("Profile, camera or rotator changed during the flat rotation loop.");
        var info = rotator.GetInfo();
        if (info?.Connected != true || info.IsMoving) throw new SequenceEntityFailedException("The rotator must be connected and stationary.");
        if (!string.IsNullOrEmpty(info.DeviceId) && info.DeviceId != scope.RotatorId)
            throw new SequenceEntityFailedException("The connected rotator differs from the recorded rotator.");
        if (camera.GetInfo()?.IsExposing == true) throw new SequenceEntityFailedException("Wait for the camera exposure before rotating.");
        var monitor = safety.GetInfo();
        if (monitor is { Connected: true, IsSafe: false } || (monitor?.Connected != true &&
            !string.IsNullOrEmpty(profiles.ActiveProfile.SafetyMonitorSettings.Id) && profiles.ActiveProfile.SafetyMonitorSettings.Id != "No_Device"))
            throw new SequenceEntityFailedException("The safety monitor must permit the flat rotation move.");
    }
    protected sealed record RotationRun(FlatRotationPlan Plan, double Tolerance, double Timeout);
    protected RotationRun CaptureRun() {
        ValidateSettings();
        var plan = history.GetPlan(NightOffset, ToleranceDegrees);
        return new(plan with { Angles = plan.Angles.ToArray() }, ToleranceDegrees, MoveTimeoutSeconds);
    }
    internal virtual Task RunRotations(IProgress<ApplicationStatus> progress, CancellationToken token) =>
        RunRotationLoop(progress, token);
    protected async Task<bool> RunRotationLoop(IProgress<ApplicationStatus> progress, CancellationToken token, RotationRun? frozen = null) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var profileChanged = 0;
        void ProfileChanging(object? sender, EventArgs args) {
            Interlocked.Exchange(ref profileChanged, 1);
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }
        profiles.BeforeProfileChanging += ProfileChanging;
        var log = new RunDiagnostics("SkyFlatRotations");
        try {
            token.ThrowIfCancellationRequested();
            // Freeze both the plan and settings; new image saves cannot grow a running loop.
            var settings = frozen ?? CaptureRun();
            var plan = settings.Plan;
            var matchTolerance = settings.Tolerance; var timeout = settings.Timeout;
            log.Write("Plan", new { plan.Scope, plan.Night, plan.Angles, matchTolerance });
            if (plan.Angles.Length == 0) { LastResult = Describe(plan) + "; skipped"; return false; }
            if (Items.All(i => i.Status == SequenceEntityStatus.DISABLED)) { LastResult = "No enabled instructions; skipped"; return false; }
            var sequence = new SequentialStrategy();
            for (var index = 0; index < plan.Angles.Length; index++) {
                lifetime.Token.ThrowIfCancellationRequested();
                ResetForRotation();
                if (!ConditionsPermit(Items.FirstOrDefault(i => i.Status == SequenceEntityStatus.CREATED))) {
                    LastResult = "Stopped by sequence condition"; log.Write("ConditionStopped", new { index }); return false;
                }
                CheckEquipment(plan.Scope, lifetime.Token);
                var angle = plan.Angles[index];
                var adjusted = rotator.GetTargetMechanicalPosition((float)angle);
                if (!FlatRotations.ValidAngle(adjusted) || FlatRotations.Distance(adjusted, angle) > matchTolerance)
                    throw new SequenceEntityFailedException("The rotator's configured limits would substitute a different mechanical rotation.");
                LastResult = $"Rotation {index + 1}/{plan.Angles.Length}: moving to {angle:0.##}°";
                progress?.Report(new ApplicationStatus { Status = LastResult }); log.Write("MoveStarted", new { index, angle });
                using (var move = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    move.CancelAfter(TimeSpan.FromSeconds(timeout));
                    float result;
                    try { result = await rotator.MoveMechanical((float)angle, move.Token); }
                    catch (OperationCanceledException) when (move.IsCancellationRequested && !lifetime.IsCancellationRequested) {
                        throw new SequenceEntityFailedException("Rotator move timed out; flat instructions were not started.");
                    }
                    if (move.IsCancellationRequested && !lifetime.IsCancellationRequested)
                        throw new SequenceEntityFailedException("Rotator move timed out; flat instructions were not started.");
                    move.Token.ThrowIfCancellationRequested();
                    if (!FlatRotations.ValidAngle(result)) throw new SequenceEntityFailedException("The rotator move did not complete.");
                }
                CheckEquipment(plan.Scope, lifetime.Token);
                var actual = rotator.GetInfo().MechanicalPosition;
                if (!FlatRotations.ValidAngle(actual) || FlatRotations.Distance(actual, angle) > matchTolerance)
                    throw new SequenceEntityFailedException($"Rotator stopped at {actual:0.##}°; requested {angle:0.##}°. Flat instructions were not started.");
                LastResult = $"Rotation {index + 1}/{plan.Angles.Length}: running instructions at {actual:0.##}°";
                log.Write("InstructionsStarted", new { index, angle, actual });
                await sequence.Execute(this, progress, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (HasSkippedInstruction(this)) {
                    LastResult = "Instructions skipped; rotation loop stopped"; return false;
                }
            }
            LastResult = $"Ran instructions at {plan.Angles.Length} captured rotations"; log.Write("Completed", new { rotations = plan.Angles.Length });
            return true;
        } catch (OperationCanceledException) when (Volatile.Read(ref profileChanged) != 0) {
            LastResult = "Profile changed; flat rotation loop stopped"; log.Write("ProfileChanged");
            throw new SequenceEntityFailedException(LastResult);
        } catch (OperationCanceledException) {
            LastResult = "Flat rotation loop cancelled"; log.Write("Cancelled"); throw;
        } catch (Exception e) {
            LastResult = "Failed: " + e.Message; log.Write("Failure", e.ToString()); throw;
        } finally { profiles.BeforeProfileChanging -= ProfileChanging; progress?.Report(new ApplicationStatus()); }
    }
    // Immutable native actions own their hidden steps; inspect editable nested instruction sets.
    private static bool HasSkippedInstruction(ISequenceContainer container) => container.GetItemsSnapshot().Any(item =>
        item.Status == SequenceEntityStatus.SKIPPED || (item.Status != SequenceEntityStatus.DISABLED &&
            item is ISequenceContainer nested && item is not IImmutableContainer && HasSkippedInstruction(nested)));
}

public sealed class FlatRotationStrategy : IExecutionStrategy {
    public object Clone() => new FlatRotationStrategy();
    public Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        context is SkyFlatRotationsContainer rotations ? rotations.RunRotations(progress, token)
            : throw new ArgumentException("Flat rotation strategy requires its rotation container.");
}
