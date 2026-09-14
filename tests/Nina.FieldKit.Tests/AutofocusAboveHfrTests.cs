using System.IO;
using NINA.Sequencer.Utility;
using Moq;
using Newtonsoft.Json;
using Nina.FieldKit.Plugin;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Model;
using NINA.WPF.Base.Utility.AutoFocus;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class AutofocusAboveHfrTests {
    private sealed class Service : IHfrAutofocusService {
        public HfrReading? Latest = new(3, "Image");
        public HfrReading? Result = new(1.6, "Autofocus fit");
        public int Runs, Reads;
        public IList<string> Errors = new List<string>();
        public Func<CancellationToken, Task<HfrReading?>>? Run;
        public HfrReading? ReadLatest() { Reads++; return Latest; }
        public IList<string> Validate() => Errors;
        public Task<HfrReading?> RunAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Runs++;
            return Run?.Invoke(token) ?? Task.FromResult(Result);
        }
    }

    [Theory]
    [InlineData(1.2)] [InlineData(1.7)]
    public async Task AtOrBelowLimitSkips(double value) {
        var service = new Service { Latest = new(value, "Image") };
        var action = new AutofocusAboveHfr(service);
        await action.Execute(null!, CancellationToken.None);
        Assert.Equal(0, service.Runs);
        Assert.Contains("skipped", action.LastResult);
    }

    [Theory]
    [InlineData(3)] [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task HighOrInvalidLatestRunsOnce(double value) {
        var service = new Service { Latest = new(value, "Autofocus fit") };
        var action = new AutofocusAboveHfr(service);
        await action.Execute(null!, CancellationToken.None);
        Assert.Equal(1, service.Runs);
        Assert.Contains("accepted", action.LastResult);
    }

    [Fact] public async Task MissingHistoryRunsAutofocus() {
        var service = new Service { Latest = null };
        await new AutofocusAboveHfr(service).Execute(null!, CancellationToken.None);
        Assert.Equal(1, service.Runs);
    }

    [Theory]
    [InlineData(3)] [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task BadResultFailsWithoutAnInternalLoop(double value) {
        var service = new Service { Result = new(value, "Autofocus fit") };
        var action = new AutofocusAboveHfr(service);
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => action.Execute(null!, CancellationToken.None));
        Assert.Equal(1, service.Runs);
        Assert.StartsWith("Failed:", action.LastResult);
    }

    [Fact] public async Task NullResultFails() {
        var service = new Service { Result = null };
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => new AutofocusAboveHfr(service).Execute(null!, CancellationToken.None));
    }

    [Fact] public async Task LimitIsFrozenDuringAutofocus() {
        var service = new Service();
        var action = new AutofocusAboveHfr(service);
        service.Run = _ => { action.MaximumHfr = 4; return Task.FromResult<HfrReading?>(new(3, "Autofocus fit")); };
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => action.Execute(null!, CancellationToken.None));
    }

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task InvalidThresholdFailsBeforeReading(double limit) {
        var service = new Service();
        var action = new AutofocusAboveHfr(service) { MaximumHfr = limit };
        Assert.False(action.Validate());
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => action.Execute(null!, CancellationToken.None));
        Assert.Equal(0, service.Reads);
        Assert.Equal(0, service.Runs);
    }

    [Fact] public async Task CancellationRejectsLateSuccess() {
        using var stop = new CancellationTokenSource();
        var service = new Service { Run = _ => { stop.Cancel(); return Task.FromResult<HfrReading?>(new(1.2, "Autofocus fit")); } };
        var action = new AutofocusAboveHfr(service);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.Execute(null!, stop.Token));
        Assert.Equal("Cancelled", action.LastResult);
    }

    [Fact] public async Task DisconnectedEquipmentDoesNotStartAutofocus() {
        var service = new Service { Errors = new List<string> { "Camera disconnected" } };
        await Assert.ThrowsAsync<SequenceEntityFailedException>(() => new AutofocusAboveHfr(service).Execute(null!, CancellationToken.None));
        Assert.Equal(0, service.Runs);
    }

    [Fact] public void CloneAndSequenceJsonKeepLimitAndErrorPolicy() {
        var original = new AutofocusAboveHfr(new Service()) { MaximumHfr = 2.25, Attempts = 2 };
        var clone = Assert.IsType<AutofocusAboveHfr>(original.Clone());
        Assert.Equal(2.25, clone.MaximumHfr);
        Assert.Equal(2, clone.Attempts);
        Assert.Equal(InstructionErrorBehavior.AbortOnError, clone.ErrorBehavior);
        var json = JsonConvert.SerializeObject(original);
        Assert.DoesNotContain("LastResult", json);
        var restored = new AutofocusAboveHfr(new Service());
        JsonConvert.PopulateObject(json, restored);
        Assert.Equal(2.25, restored.MaximumHfr);
    }

    private static ImageHistoryPoint Image(int id, double hfr, DateTime time, string type = "LIGHT") {
        var point = new ImageHistoryPoint(id, type);
        typeof(ImageHistoryPoint).GetProperty(nameof(ImageHistoryPoint.HFR))!.SetValue(point, hfr);
        typeof(ImageHistoryPoint).GetProperty(nameof(ImageHistoryPoint.dateTime))!.SetValue(point, time);
        return point;
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void LatestAutofocusUsesMatchingReportNeverTheOldImageHfr(bool reportExists) {
        var time = DateTime.Now;
        var report = new AutoFocusReport { Timestamp = time, Filter = "L", Method = "STARHFR", CalculatedFocusPoint = new() { Value = 3 } };
        var image = Image(1, 1.2, time.AddMinutes(-1));
        image.PopulateAFPoint(report);
        var history = new Mock<IImageHistoryVM>();
        history.SetupGet(x => x.ImageHistory).Returns(new List<ImageHistoryPoint> { image });
        history.SetupGet(x => x.AutoFocusPoints).Returns(new AsyncObservableCollection<ImageHistoryPoint>(new[] { image }));
        var profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        var profileId = Guid.NewGuid();
        profile.SetupGet(x => x.ActiveProfile.Id).Returns(profileId);
        var directory = Path.Combine(Path.GetTempPath(), "fieldkit-hfr-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            if (reportExists) File.WriteAllText(Path.Combine(directory, $"{time:yyyy-MM-dd--HH-mm-ss}--{profileId}.json"), JsonConvert.SerializeObject(report));
            var service = new NinaHfrAutofocusService(profile.Object, history.Object, Mock.Of<ICameraMediator>(),
                Mock.Of<IFilterWheelMediator>(), Mock.Of<IFocuserMediator>(), Mock.Of<IAutoFocusVMFactory>()) { ReportDirectory = directory };
            var reading = service.ReadLatest();
            if (reportExists) Assert.Equal(3, reading!.Value); else Assert.Null(reading);
            // A newer image supersedes AF, even when its HFR is invalid. Dark frames do not.
            history.Object.ImageHistory.Add(Image(2, 0, time.AddMinutes(1)));
            history.Object.ImageHistory.Add(Image(3, 4, time.AddMinutes(2), "DARK"));
            Assert.Equal(0, service.ReadLatest()!.Value);
        } finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [Fact] public void ContrastReportIsNotHfr() {
        Assert.Null(NinaHfrAutofocusService.FromReport(new AutoFocusReport { Method = "CONTRAST", CalculatedFocusPoint = new() { Value = 1.2 } }));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeAdapterUsesSelectedProviderAndRecordsOnlyCompletedReports(bool cancel) {
        var profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        var history = new Mock<IImageHistoryVM>();
        var provider = new Mock<IAutoFocusVM>();
        var factory = new Mock<IAutoFocusVMFactory>();
        factory.Setup(x => x.Create()).Returns(provider.Object);
        var windows = new Mock<NINA.Core.Utility.WindowService.IWindowServiceFactory> { DefaultValue = DefaultValue.Mock };
        var window = Mock.Get(windows.Object.Create());
        var report = new AutoFocusReport { Method = "STARHFR", CalculatedFocusPoint = new() { Value = 1.6 } };
        using var stop = new CancellationTokenSource();
        provider.Setup(x => x.StartAutoFocus(It.IsAny<NINA.Core.Model.Equipment.FilterInfo>(), stop.Token, It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns(() => { if (cancel) stop.Cancel(); return Task.FromResult(report); });
        var service = new NinaHfrAutofocusService(profile.Object, history.Object, Mock.Of<ICameraMediator>(),
            Mock.Of<IFilterWheelMediator>(), Mock.Of<IFocuserMediator>(), factory.Object) { WindowServiceFactory = windows.Object };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(null!, stop.Token));
        else Assert.Equal(1.6, (await service.RunAsync(null!, stop.Token))!.Value);
        history.Verify(x => x.AppendAutoFocusPoint(report), cancel ? Times.Never() : Times.Once());
        factory.Verify(x => x.Create(), Times.Once());
        window.Verify(x => x.DelayedClose(TimeSpan.FromSeconds(10)), Times.Once());
    }}



