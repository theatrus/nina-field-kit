using System.IO;
using System.Windows;

namespace Nina.FieldKit.Plugin;

public partial class TwilightFlatsSetupWindow : Window {
    private readonly TwilightFlatsSetupModel draft;
    public TwilightFlatsSetupWindow(TwilightFlatFiltersContainer container) {
        InitializeComponent();
        Icon = FieldKitIcon.WindowIcon();
        DataContext = draft = container.CreateSetup();
    }
    private void CancelClick(object sender, RoutedEventArgs args) => Close();
    private void ApplyClick(object sender, RoutedEventArgs args) {
        try { draft.Apply(); Close(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Notice.Text = e.Message; }
    }
    private void PreviewClick(object sender, RoutedEventArgs args) {
        try { RotationPreview.Text = draft.PreviewRotations(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException) {
            RotationPreview.Text = e.Message;
        }
    }
}
