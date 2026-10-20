using System.IO;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class TwilightFlatFiltersTests {
    [Fact] public void SetupDraftDoesNotChangeSequenceUntilApplyAndUpdatesSummary() {
        var rig = new Rig(); var draft = rig.Container.CreateSetup();
        draft.MoveUpCommand.Execute(draft.Filters[1]); draft.Filters[1].Enabled = false;
        draft.NightOffset = -1; draft.ToleranceDegrees = "0.2"; draft.MoveTimeoutSeconds = "90";
        Assert.Equal("L → R", rig.Container.FilterSummary); Assert.Equal(0, rig.Container.NightOffset);
        var unchangedPreviewStatus = rig.Container.LastResult;
        _ = draft.PreviewRotations(); Assert.Equal(unchangedPreviewStatus, rig.Container.LastResult);
        draft.Apply();
        Assert.Equal("R", rig.Container.FilterSummary); Assert.Equal("Previous night", rig.Container.NightSummary);
        Assert.Equal(.2, rig.Container.ToleranceDegrees); Assert.Equal(90, rig.Container.MoveTimeoutSeconds);
        draft.Filters[0].Enabled = false; Assert.Equal("R", rig.Container.FilterSummary);
    }
    [Theory] [InlineData("-1", "90")] [InlineData("bad", "90")] [InlineData("NaN", "90")]
    [InlineData("0.1", "4")] [InlineData("0.1", "Infinity")]
    public void InvalidSetupDoesNotPartiallyChangeSequence(string tolerance, string timeout) {
        var rig = new Rig(); var draft = rig.Container.CreateSetup();
        draft.MoveUpCommand.Execute(draft.Filters[1]); draft.NightOffset = -1;
        draft.ToleranceDegrees = tolerance; draft.MoveTimeoutSeconds = timeout;
        Assert.ThrowsAny<ArgumentException>(draft.Apply);
        Assert.Equal("L → R", rig.Container.FilterSummary); Assert.Equal(0, rig.Container.NightOffset);
        Assert.Equal(.1, rig.Container.ToleranceDegrees); Assert.Equal(180, rig.Container.MoveTimeoutSeconds);
    }
    [Fact] public void StaleSetupCannotApplyToAnotherProfile() {
        var rig = new Rig(); var draft = rig.Container.CreateSetup();
        rig.Rotation.Profiles.SetupGet(profiles => profiles.ActiveProfile).Returns(Mock.Of<NINA.Profile.Interfaces.IProfile>());
        Assert.Throws<InvalidOperationException>(draft.Apply);
        Assert.Equal("L → R", rig.Container.FilterSummary);
    }
    internal sealed class Rig {
        public readonly SkyFlatRotationsTests.Rig Rotation = new();
        public readonly Mock<IFilterWheelMediator> Wheel = new();
        public ObserveAllCollection<FilterInfo> Known = new(new[] {
            new FilterInfo { Name = "L", Position = 0 }, new FilterInfo { Name = "R", Position = 1 }, new FilterInfo { Name = "Ha", Position = 2 }
        });
        public readonly FilterWheelInfo Info = new() { Connected = true, DeviceId = "wheel-A" };
        public readonly TwilightFlatFiltersContainer Container;
        public Action<ProbeSkyFlat>? AfterFlat;
        public Rig() {
            Mock.Get(Rotation.Profiles.Object.ActiveProfile).SetupGet(x => x.Id).Returns(Rotation.History.Scope.ProfileId);
            Mock.Get(Rotation.Profiles.Object.ActiveProfile.FilterWheelSettings).SetupGet(x => x.FilterWheelFilters).Returns(() => Known);
            Mock.Get(Rotation.Profiles.Object.ActiveProfile.FilterWheelSettings).SetupGet(x => x.Id).Returns("wheel-A");
            Mock.Get(Rotation.Profiles.Object.ActiveProfile.ImageFileSettings).SetupGet(x => x.FilePath).Returns(Path.GetTempPath());
            Info.SelectedFilter = Known[0];
            Wheel.Setup(x => x.GetInfo()).Returns(Info);
            Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
                .Returns((FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> _) => {
                    token.ThrowIfCancellationRequested(); Rotation.Calls.Add("filter:" + filter.Name);
                    Info.SelectedFilter = filter; return Task.FromResult(filter);
                });
            Container = new(Rotation.History, Rotation.Profiles.Object, Rotation.Rotator.Object, Rotation.Camera.Object,
                Rotation.Safety.Object, Wheel.Object) { Name = "Twilight flats by filter and rotation" };
            Container.FilterOrder[2].Enabled = false;
        }
        public SkyFlat NativeFlat() => new(Rotation.Profiles.Object, Rotation.Camera.Object, Mock.Of<ITelescopeMediator>(),
            Mock.Of<IImagingMediator>(), Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(), Wheel.Object, Mock.Of<ITwilightCalculator>());
        public ProbeSkyFlat AddProbe() { var flat = new ProbeSkyFlat(this); Container.Add(flat); return flat; }
    }
    internal sealed class ProbeSkyFlat : SkyFlat {
        private readonly Rig rig;
        public int Calls;
        public ProbeSkyFlat(Rig rig) : base(rig.Rotation.Profiles.Object, rig.Rotation.Camera.Object, Mock.Of<ITelescopeMediator>(),
            Mock.Of<IImagingMediator>(), Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>(), rig.Wheel.Object, Mock.Of<ITwilightCalculator>()) {
            this.rig = rig; Name = "Twilight Sky Flats"; GetSwitchFilterItem().Filter = rig.Known[2];
        }
        public override bool Validate() => true;
        public override object Clone() => new ProbeSkyFlat(rig);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Assert.Equal(0, GetIterations().CompletedIterations);
            GetIterations().CompletedIterations = GetIterations().Iterations;
            Calls++; rig.Rotation.Calls.Add($"flat:{GetSwitchFilterItem().Filter.Name}:{rig.Rotation.Info.MechanicalPosition}");
            rig.AfterFlat?.Invoke(this); return Task.CompletedTask;
        }
    }
    [Fact] public async Task RunsEveryRotationOnOneFilterBeforeMovingToTheNextAndRestoresTemplate() {
        var rig = new Rig(); var flat = rig.AddProbe(); var original = flat.GetSwitchFilterItem().Filter;
        rig.Container.MoveFilterUpCommand.Execute(rig.Container.FilterOrder[1]);
        await rig.Container.Execute(null!, default);
        Assert.Equal(new[] { "filter:R", "move:10", "flat:R:10", "move:190", "flat:R:190", "move:45", "flat:R:45",
            "filter:L", "move:10", "flat:L:10", "move:190", "flat:L:190", "move:45", "flat:L:45" }, rig.Rotation.Calls);
        Assert.Equal(6, flat.Calls); Assert.Same(original, flat.GetSwitchFilterItem().Filter);
        Assert.Equal("L", rig.Info.SelectedFilter.Name); Assert.Equal(45, rig.Rotation.Info.MechanicalPosition);
        Assert.Contains("Completed 2 filters at 3 captured rotations", rig.Container.LastResult);
        Assert.True(rig.Container.CanEditFilters);
    }
    [Fact] public async Task FilterOrderAndRotationSettingsAreFrozenForTheWholeRun() {
        var rig = new Rig(); rig.AddProbe();
        rig.AfterFlat = _ => {
            rig.Rotation.History.Angles[1] = 300;
            rig.Container.FilterOrder.Move(1, 0); rig.Container.FilterOrder[0].Enabled = false;
            rig.Container.NightOffset = -1; rig.Container.ToleranceDegrees = 5; rig.Container.MoveTimeoutSeconds = 5;
        };
        await rig.Container.Execute(null!, default);
        Assert.Equal(3, rig.Rotation.Calls.Count(x => x.StartsWith("flat:L:")));
        Assert.Equal(3, rig.Rotation.Calls.Count(x => x.StartsWith("flat:R:")));
        Assert.Equal(2, rig.Rotation.Calls.Count(x => x == "move:190")); Assert.DoesNotContain("move:300", rig.Rotation.Calls);
    }
    [Fact] public void ReorderEnableRefreshAndClonePreserveUserChoicesWithoutChangingTheProfile() {
        var rig = new Rig(); var row = rig.Container.FilterOrder[1];
        Assert.False(rig.Container.MoveFilterUpCommand.CanExecute(rig.Container.FilterOrder[0]));
        rig.Container.MoveFilterUpCommand.Execute(row);
        rig.Container.FilterOrder[1].Enabled = false;
        rig.Known = new(rig.Known.Append(new FilterInfo { Name = "OIII", Position = 3 }));
        rig.Container.RefreshFiltersCommand.Execute(null);
        Assert.Equal(new[] { "R", "L", "Ha", "OIII" }, rig.Container.FilterOrder.Select(x => x.Name));
        Assert.False(rig.Container.FilterOrder[3].Enabled); Assert.False(rig.Container.FilterOrder[1].Enabled);
        Assert.Equal(new[] { "L", "R", "Ha", "OIII" }, rig.Known.Select(x => x.Name));
        var clone = Assert.IsType<TwilightFlatFiltersContainer>(rig.Container.Clone());
        Assert.NotSame(row, clone.FilterOrder[0]); Assert.True(clone.FilterOrder[0].Enabled);
        clone.FilterOrder[0].Enabled = false; Assert.True(row.Enabled);
    }
    [Theory] [InlineData("missing")] [InlineData("renamed")] [InlineData("duplicate")] [InlineData("none")]
    public async Task InvalidFilterPlansFailBeforeAnyMove(string problem) {
        var rig = new Rig(); rig.AddProbe();
        if (problem == "missing") rig.Known = new(rig.Known.Where(x => x.Position != 1));
        if (problem == "renamed") rig.Known = new(rig.Known.Select(x => x.Position == 1 ? new FilterInfo { Name = "Different filter", Position = 1 } : x));
        if (problem == "duplicate") rig.Container.FilterOrder.Add(new("R", 1));
        if (problem == "none") foreach (var row in rig.Container.FilterOrder) row.Enabled = false;
        Assert.False(rig.Container.Validate());
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Container.Execute(null!, default));
        Assert.Empty(rig.Rotation.Calls);
    }
    [Theory] [InlineData("unsafe")] [InlineData("exposing")] [InlineData("moving wheel")] [InlineData("disconnected wheel")] [InlineData("wrong wheel")]
    public async Task EquipmentGatePreventsFilterAndRotatorMoves(string problem) {
        var rig = new Rig(); rig.AddProbe();
        if (problem == "unsafe") rig.Rotation.SafetyInfo.IsSafe = false;
        if (problem == "exposing") rig.Rotation.CameraInfo.IsExposing = true;
        if (problem == "moving wheel") rig.Info.IsMoving = true;
        if (problem == "disconnected wheel") rig.Info.Connected = false;
        if (problem == "wrong wheel") rig.Info.DeviceId = "wheel-B";
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Empty(rig.Rotation.Calls);
    }
    [Fact] public async Task FailedOrCancelledFilterMoveNeverStartsAFlat() {
        var rig = new Rig(); var flat = rig.AddProbe(); var original = flat.GetSwitchFilterItem().Filter;
        rig.Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .ReturnsAsync((FilterInfo)null!);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Empty(rig.Rotation.Calls); Assert.Same(original, flat.GetSwitchFilterItem().Filter);
        rig = new(); rig.AddProbe(); using var stop = new CancellationTokenSource();
        rig.Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns((FilterInfo filter, CancellationToken _, IProgress<ApplicationStatus> _) => { stop.Cancel(); return Task.FromResult(filter); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Container.Execute(null!, stop.Token));
        Assert.Empty(rig.Rotation.Calls);
    }
    [Fact] public async Task FilterMoveDeadlineStopsBeforeRotationOrExposure() {
        var rig = new Rig(); rig.AddProbe(); rig.Container.MoveTimeoutSeconds = 5;
        rig.Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns(async (FilterInfo filter, CancellationToken token, IProgress<ApplicationStatus> _) => { await Task.Delay(Timeout.Infinite, token); return filter; });
        var error = await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Contains("timed out", error.Message); Assert.Empty(rig.Rotation.Calls);
    }
    [Fact] public async Task WrongReportedFilterAndFilterChangedBetweenRotationsStopAcquisition() {
        var rig = new Rig(); rig.AddProbe();
        rig.Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .ReturnsAsync(rig.Known[2]);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default)); Assert.Empty(rig.Rotation.Calls);
        rig = new(); rig.AddProbe(); var current = rig;
        rig.AfterFlat = _ => current.Info.SelectedFilter = current.Known[2];
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default));
        Assert.Equal(new[] { "filter:L", "move:10", "flat:L:10" }, rig.Rotation.Calls);
    }
    [Fact] public async Task ProfileChangesDuringFilterMoveCancelBeforeAnyRotation() {
        var rig = new Rig(); rig.AddProbe();
        rig.Wheel.Setup(x => x.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns((FilterInfo filter, CancellationToken _, IProgress<ApplicationStatus> _) => {
                rig.Rotation.Profiles.Raise(x => x.BeforeProfileChanging += null, EventArgs.Empty); return Task.FromResult(filter);
            });
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => rig.Container.Execute(null!, default)); Assert.Empty(rig.Rotation.Calls);
    }
    [Fact] public async Task EmptyHistoryAndAncestorConditionsAvoidAllMoves() {
        var rig = new Rig(); rig.AddProbe(); rig.Rotation.History.Angles = [];
        await rig.Container.Execute(null!, default); Assert.Empty(rig.Rotation.Calls);
        rig = new(); rig.AddProbe(); var parent = new SequentialContainer(); parent.Add(rig.Container); parent.Add(new LoopCondition { Iterations = 0 });
        await rig.Container.Execute(null!, default); Assert.Empty(rig.Rotation.Calls);
    }
    [Fact] public async Task ChildSkipStopsRemainingFiltersAndCancellationRestoresConfiguration() {
        var rig = new Rig(); var flat = rig.AddProbe(); var original = flat.GetSwitchFilterItem().Filter;
        rig.AfterFlat = _ => throw new SequenceItemSkippedException("Flat window ended");
        await rig.Container.Execute(null!, default);
        Assert.Equal(new[] { "filter:L", "move:10", "flat:L:10" }, rig.Rotation.Calls);
        Assert.Same(original, flat.GetSwitchFilterItem().Filter);
        rig = new(); flat = rig.AddProbe(); original = flat.GetSwitchFilterItem().Filter; using var stop = new CancellationTokenSource();
        rig.AfterFlat = _ => stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Container.Execute(null!, stop.Token));
        Assert.Equal(new[] { "filter:L", "move:10", "flat:L:10" }, rig.Rotation.Calls);
        Assert.Same(original, flat.GetSwitchFilterItem().Filter); Assert.True(rig.Container.CanEditFilters);
    }
    [Fact] public async Task SkippedNestedTwilightInstructionStopsRemainingRotationsAndFilters() {
        var rig = new Rig(); var nested = new SequentialContainer(); var flat = new ProbeSkyFlat(rig);
        nested.Add(flat); rig.Container.Add(nested);
        rig.AfterFlat = _ => throw new SequenceItemSkippedException("Flat window ended");
        await rig.Container.Execute(null!, default);
        Assert.Equal(new[] { "filter:L", "move:10", "flat:L:10" }, rig.Rotation.Calls);
        Assert.Contains("skipped", rig.Container.LastResult);
    }
    [Fact] public async Task NestedTwilightInstructionsRepeatWhileDisabledOnesStayDisabledAndOrderingLocks() {
        var rig = new Rig(); var nested = new SequentialContainer(); var flat = new ProbeSkyFlat(rig); nested.Add(flat); rig.Container.Add(nested);
        var disabled = rig.AddProbe(); disabled.Status = NINA.Core.Enum.SequenceEntityStatus.DISABLED;
        rig.AfterFlat = _ => {
            Assert.False(rig.Container.CanEditFilters);
            Assert.False(rig.Container.MoveFilterDownCommand.CanExecute(rig.Container.FilterOrder[0]));
            Assert.False(rig.Container.RefreshFiltersCommand.CanExecute(null));
        };
        await rig.Container.Execute(null!, default);
        Assert.Equal(6, flat.Calls); Assert.Equal(0, disabled.Calls);
        Assert.Equal(NINA.Core.Enum.SequenceEntityStatus.DISABLED, disabled.Status);
    }
    [Fact] public void RejectsConflictingSwitchFilterAndDoubleRotationLoops() {
        var rig = new Rig(); rig.AddProbe(); rig.Container.Add(new SwitchFilter(rig.Rotation.Profiles.Object, rig.Wheel.Object));
        Assert.False(rig.Container.Validate()); Assert.Contains(rig.Container.Issues, x => x.Contains("Remove Switch Filter"));
        rig = new(); rig.AddProbe(); rig.Container.Add(rig.Rotation.Container);
        Assert.False(rig.Container.Validate()); Assert.Contains(rig.Container.Issues, x => x.Contains("already repeats"));
    }
    [Fact] public void NinaJsonRoundtripRetainsFilterOrderAndNativeTwilightSettingsWithoutDuplicateRows() {
        var rig = new Rig(); var flat = rig.NativeFlat(); flat.Name = "Twilight Sky Flats";
        flat.MinExposure = .2; flat.MaxExposure = 20; flat.GetIterations().Iterations = 7;
        flat.GetSwitchFilterItem().Filter = rig.Known[2]; rig.Container.Add(flat);
        rig.Container.MoveFilterUpCommand.Execute(rig.Container.FilterOrder[1]);
        var factory = new Mock<NINA.Sequencer.ISequencerFactory>();
        factory.SetupGet(x => x.DateTimeProviders).Returns(new List<NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider>());
        factory.Setup(x => x.GetContainer<TwilightFlatFiltersContainer>()).Returns(() => new(rig.Rotation.History,
            rig.Rotation.Profiles.Object, rig.Rotation.Rotator.Object, rig.Rotation.Camera.Object, rig.Rotation.Safety.Object, rig.Wheel.Object));
        factory.Setup(x => x.GetContainer<SequentialContainer>()).Returns(() => new());
        factory.Setup(x => x.GetContainer<SkyFlat>()).Returns(rig.NativeFlat);
        factory.Setup(x => x.GetItem<SkyFlat>()).Returns(rig.NativeFlat);
        factory.Setup(x => x.GetItem<Annotation>()).Returns(() => new());
        factory.Setup(x => x.GetItem<SwitchFilter>()).Returns(() => new(rig.Rotation.Profiles.Object, rig.Wheel.Object));
        factory.Setup(x => x.GetItem<TakeExposure>()).Returns(() => new(rig.Rotation.Profiles.Object, rig.Rotation.Camera.Object,
            Mock.Of<IImagingMediator>(), Mock.Of<IImageSaveMediator>(), Mock.Of<IImageHistoryVM>()));
        factory.Setup(x => x.GetCondition<LoopCondition>()).Returns(() => new());
        var converter = new NINA.Sequencer.Serialization.SequenceJsonConverter(factory.Object);
        var json = converter.Serialize(rig.Container);
        Assert.DoesNotContain("LastResult", json); Assert.DoesNotContain("FilterStatus", json);
        var restored = Assert.IsType<TwilightFlatFiltersContainer>(converter.Deserialize(json));
        Assert.Equal(new[] { "R", "L", "Ha" }, restored.FilterOrder.Select(x => x.Name));
        Assert.False(restored.FilterOrder[2].Enabled);
        var restoredFlat = Assert.IsType<SkyFlat>(Assert.Single(restored.Items));
        Assert.Same(restored, restoredFlat.Parent);
        Assert.Equal(.2, restoredFlat.MinExposure); Assert.Equal(20, restoredFlat.MaxExposure);
        Assert.Equal(7, restoredFlat.GetIterations().Iterations); Assert.Equal("Ha", restoredFlat.GetSwitchFilterItem().Filter.Name);
    }
}
