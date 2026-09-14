using System.IO;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Utility.AutoFocus;

namespace Nina.FieldKit.Plugin;

public sealed class NinaHfrAutofocusService(IProfileService profile, IImageHistoryVM history,
    ICameraMediator camera, IFilterWheelMediator filters, IFocuserMediator focuser,
    IAutoFocusVMFactory factory) : IHfrAutofocusService {
    public IWindowServiceFactory WindowServiceFactory { get; set; } = new WindowServiceFactory();
    public string ReportDirectory { get; set; } = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");

    public IList<string> Validate() {
        var issues = new List<string>();
        if (camera.GetInfo()?.Connected != true) issues.Add("Connect a camera for autofocus.");
        if (focuser.GetInfo()?.Connected != true) issues.Add("Connect a focuser for autofocus.");
        if (profile.ActiveProfile.FocuserSettings.AutoFocusMethod != AFMethodEnum.STARHFR)
            issues.Add("Select the Star HFR autofocus method; contrast measurements cannot be compared to an HFR limit.");
        return issues;
    }

    public HfrReading? ReadLatest() {
        // Do not search backward for a good value: missing data on the newest point must not hide a bad focus.
        var image = history.ImageHistory?.Where(p => p.Type is "LIGHT" or "SNAPSHOT")
            .OrderByDescending(p => p.dateTime).ThenByDescending(p => p.Id).FirstOrDefault();
        var af = history.AutoFocusPoints?.Select(p => p.AutoFocusPoint).Where(p => p != null)
            .OrderByDescending(p => p.Time).FirstOrDefault();
        if (af != null && (image == null || af.Time >= image.dateTime)) {
            // NINA history reuses the previous image as its AF marker. Its HFR is NOT the AF result.
            return ReadReport(af.Time, af.Filter);
        }
        return image == null ? null : new HfrReading(image.HFR, "Image");
    }

    private HfrReading? ReadReport(DateTime timestamp, string filter) {
        try {
            if (!Directory.Exists(ReportDirectory)) return null;
            // Only reports for the active profile and the recorded AF date are candidates.
            foreach (var path in Directory.EnumerateFiles(ReportDirectory, $"{timestamp:yyyy-MM-dd}--*--{profile.ActiveProfile.Id}.json")
                .OrderByDescending(p => p, StringComparer.Ordinal).Take(256)) {
                if (new FileInfo(path).Length > 1024 * 1024) continue;
                var report = JsonConvert.DeserializeObject<AutoFocusReport>(File.ReadAllText(path), new JsonSerializerSettings { MaxDepth = 32 });
                if (report?.Timestamp == timestamp && report.Filter == filter) return FromReport(report);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("FieldKit could not read the latest autofocus report: " + e.GetType().Name);
        }
        return null;
    }

    public static HfrReading? FromReport(AutoFocusReport? report) =>
        report?.Method == AFMethodEnum.STARHFR.ToString() && report.CalculatedFocusPoint != null
            ? new HfrReading(report.CalculatedFocusPoint.Value, "Autofocus fit") : null;

    public async Task<HfrReading?> RunAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var autofocus = factory.Create();
        var window = WindowServiceFactory.Create();
        try {
            window.Show(autofocus, "Field Kit Autofocus", System.Windows.ResizeMode.CanResize, System.Windows.WindowStyle.ToolWindow);
            var selected = filters.GetInfo()?.SelectedFilter;
            var filter = selected == null ? null : profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters
                .FirstOrDefault(f => f.Position == selected.Position);
            var report = await autofocus.StartAutoFocus(filter, token, progress);
            token.ThrowIfCancellationRequested();
            if (report != null) history.AppendAutoFocusPoint(report);
            return FromReport(report);
        } finally {
            window.DelayedClose(TimeSpan.FromSeconds(10));
        }
    }
}
