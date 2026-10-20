using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using GalaSoft.MvvmLight.Command;

namespace Nina.FieldKit.Plugin;

// The dialog owns a detached draft. Closing it never changes the sequence.
public sealed class TwilightFlatsSetupModel {
    private readonly TwilightFlatFiltersContainer container;
    internal object ProfileIdentity { get; }
    internal TwilightFlatsSetupModel(TwilightFlatFiltersContainer container, object profileIdentity) {
        this.container = container; ProfileIdentity = profileIdentity;
        Filters = new(container.FilterOrder.Select(row => row.Copy()));
        NightOffset = container.NightOffset;
        ToleranceDegrees = container.ToleranceDegrees.ToString(CultureInfo.CurrentCulture);
        MoveTimeoutSeconds = container.MoveTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
    }
    public ObservableCollection<FlatFilterSelection> Filters { get; }
    public int NightOffset { get; set; }
    public string ToleranceDegrees { get; set; }
    public string MoveTimeoutSeconds { get; set; }
    public ICommand MoveUpCommand => new RelayCommand<FlatFilterSelection>(row => Move(row, -1), row => CanMove(row, -1));
    public ICommand MoveDownCommand => new RelayCommand<FlatFilterSelection>(row => Move(row, 1), row => CanMove(row, 1));
    public ICommand RefreshCommand => new RelayCommand(Refresh);
    private bool CanMove(FlatFilterSelection? row, int step) => row is not null && Filters.IndexOf(row) is var index &&
        index >= 0 && index + step >= 0 && index + step < Filters.Count;
    private void Move(FlatFilterSelection row, int step) {
        if (CanMove(row, step)) Filters.Move(Filters.IndexOf(row), Filters.IndexOf(row) + step);
    }
    private void Refresh() {
        var known = container.GetProfileFilters();
        var retained = Filters.Where(row => known.Any(filter => filter.Position == row.Position && filter.Name == row.Name)).ToArray();
        Filters.Clear(); foreach (var row in retained) Filters.Add(row);
        foreach (var filter in known)
            if (!Filters.Any(row => row.Position == filter.Position && row.Name == filter.Name)) Filters.Add(filter);
    }
    private static double Number(string text, string label) {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException($"Enter a valid number for {label}.");
        return value;
    }
    public string PreviewRotations() => container.DescribeRotations(NightOffset, Number(ToleranceDegrees, "rotation tolerance"));
    public void Apply() => container.ApplySetup(this, Number(ToleranceDegrees, "rotation tolerance"), Number(MoveTimeoutSeconds, "move timeout"));
}
