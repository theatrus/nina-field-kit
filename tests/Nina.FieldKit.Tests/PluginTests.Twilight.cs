using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moq;
using Nina.FieldKit.Plugin;
using NINA.Core.Model.Equipment;
using NINA.Profile;
using NINA.Sequencer.Behaviors;
using NINA.Sequencer.Container;
using NINA.Sequencer.DragDrop;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.View.Sequencer;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed partial class PluginTests {
    private static IEnumerable<DependencyObject> TwilightVisuals(DependencyObject node) {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in TwilightVisuals(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static void TwilightLayout(FrameworkElement host, double width = 1200, double height = 950) {
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
    }
    private static void SaveTwilightPreview(FrameworkElement host, string filename, int width, int height) {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        using var file = System.IO.File.Create(System.IO.Path.Combine(output, filename)); encoder.Save(file);
    }
    private static void CheckTwilightFilterContainer(SequenceTemplates resources) {
        var template = Assert.IsType<DataTemplate>(resources[new DataTemplateKey(typeof(TwilightFlatFiltersContainer))]);
        Assert.IsType<SequenceContainerView>(template.LoadContent());
        var previousResources = Application.Current.Resources;
        try {
            foreach (var theme in new[] { "Dark", "Persian Faint", "Light" }) {
                var rig = new TwilightFlatFiltersTests.Rig();
                var settings = new ColorSchemaSettings(); settings.ColorSchema = settings.ColorSchemas.Items.First(x => x.Name == theme);
                Mock.Get(rig.Rotation.Profiles.Object.ActiveProfile).SetupGet(x => x.ColorSchemaSettings).Returns(settings);
                var themed = new ResourceDictionary { ["ProfileService"] = rig.Rotation.Profiles.Object };
                Application.Current.Resources = themed;
                var cache = NINA.WPF.Base.Utility.SharedResourceDictionary.SharedDictionaries; cache.Clear();
                cache[new Uri("/NINA.WPF.Base;component/Resources/StaticResources/ProfileService.xaml", UriKind.Relative)] = new WeakReference(themed);
                foreach (var resource in new[] { "StaticResources/Brushes", "StaticResources/Converters", "StaticResources/SVGDictionary",
                    "Styles/Button", "Styles/TextBlock", "Styles/TextBox", "Styles/ComboBox", "Styles/CheckBox", "Styles/ScrollViewer",
                    "Styles/RepeatButton", "Styles/ToggleButton", "Styles/Expander", "Styles/ListView", "Styles/TabControl" })
                    themed.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/NINA.WPF.Base;component/Resources/{resource}.xaml", UriKind.Relative) });
                themed.MergedDictionaries.Add(new NINA.Sequencer.SequenceItem.FlatDevice.Datatemplates());
                var root = new SequenceRootContainer(); var targets = new TargetAreaContainer();
                root.Add(new StartAreaContainer()); root.Add(targets); root.Add(new EndAreaContainer()); targets.Add(rig.Container);
                var native = new SequenceView { DataContext = new { Sequencer = root, IsLocked = false, CanDragAndDrop = true } };
                native.Resources.MergedDictionaries.Add(new NINA.Sequencer.Container.Datatemplates());
                native.Resources.MergedDictionaries.Add(resources);
                var host = new Border { Child = native, Padding = new Thickness(8) };
                host.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
                host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "PrimaryBrush"); TwilightLayout(host);
                var view = Assert.Single(TwilightVisuals(host).OfType<SequenceContainerView>(), v => ReferenceEquals(v.DataContext, rig.Container));
                var target = Assert.Single(TwilightVisuals(view).OfType<FrameworkElement>().Where(element =>
                    Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(element).OfType<DropIntoBehavior>().Any(b => b.OnDropCommand == "DropIntoCommand")));
                var drop = Assert.Single(Microsoft.Xaml.Behaviors.Interaction.GetBehaviors(target).OfType<DropIntoBehavior>());
                var hit = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight - 8), host);
                var drag = new DragDropBehavior(new Grid());
                VisualTreeHelper.HitTest(host, null, drag.GetFirstDropIntoBehaviorBelowMyself, new PointHitTestParameters(hit));
                Assert.Same(drop, typeof(DragDropBehavior).GetField("hitTestDropIntoBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(drag));
                var source = rig.NativeFlat(); source.Name = "Twilight Sky Flats"; source.GetSwitchFilterItem().Filter = rig.Known[2];
                drop.ExecuteDropInto(new DropIntoParameters(source)); TwilightLayout(host);
                var added = Assert.IsType<SkyFlat>(Assert.Single(rig.Container.Items));
                Assert.NotSame(source, added); Assert.Same(rig.Container, added.Parent);
                Assert.True(Assert.Single(TwilightVisuals(view).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, added)).ActualHeight > 0);
                Assert.DoesNotContain(TwilightVisuals(view).OfType<ItemsControl>(), x => Equals(x.Tag, "FlatFilterOrder"));
                var compact = Assert.IsType<StackPanel>(view.SequenceContainerContent);
                Assert.InRange(compact.ActualHeight, 40, 100);
                Assert.Single(TwilightVisuals(compact).OfType<Button>(), b => b.Content is TextBlock { Text: "Setup…" } && b.Command is not null);
                var window = new TwilightFlatsSetupWindow(rig.Container);
                var draft = Assert.IsType<TwilightFlatsSetupModel>(window.DataContext);
                var dialog = Assert.IsType<DockPanel>(window.Content); window.Content = null;
                dialog.DataContext = draft; dialog.Resources = themed; TwilightLayout(dialog, window.Width, window.Height);
                Assert.Equal(settings.ColorSchema.BackgroundColor, Assert.IsType<SolidColorBrush>(dialog.Background).Color);
                var order = Assert.Single(TwilightVisuals(dialog).OfType<ItemsControl>(), x => Equals(x.Tag, "FlatFilterOrder"));
                var row = draft.Filters[1];
                var up = Assert.Single(TwilightVisuals(order).OfType<Button>(), b => ReferenceEquals(b.DataContext, row) && b.Content is TextBlock { Text: "↑" });
                Assert.True(up.IsEnabled); Assert.Same(row, up.CommandParameter); up.Command.Execute(up.CommandParameter); TwilightLayout(dialog, window.Width, window.Height);
                Assert.Same(row, draft.Filters[0]); Assert.Equal("L", rig.Container.FilterOrder[0].Name);
                var checkboxes = TwilightVisuals(order).OfType<CheckBox>().ToArray();
                Assert.All(checkboxes, box => Assert.Null(box.Content));
                Assert.All(checkboxes, box => {
                    Assert.True(box.ActualWidth >= 65, "NINA's ON/OFF switch must not be squeezed into a checkbox-sized column.");
                    Assert.Equal(box.IsChecked == true ? Visibility.Visible : Visibility.Collapsed,
                        Assert.IsType<TextBlock>(box.Template.FindName("OnText", box)).Visibility);
                });
                var labels = TwilightVisuals(order).OfType<TextBlock>().Where(label => Equals(label.Tag, "FilterName")).ToArray();
                Assert.Equal(new[] { "R", "L", "Ha" }, labels.Select(label => label.Text));
                Assert.All(labels, label => {
                    Assert.True(label.ActualWidth >= 180, "Filter names need a usable independent text column.");
                    Assert.Equal(settings.ColorSchema.PrimaryColor, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
                });
                checkboxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, false); TwilightLayout(dialog, window.Width, window.Height); Assert.False(row.Enabled);
                checkboxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, true); TwilightLayout(dialog, window.Width, window.Height); Assert.True(row.Enabled);
                rig.Known = new(rig.Known.Append(new FilterInfo { Name = "OIII", Position = 3 }));
                var refresh = Assert.Single(TwilightVisuals(dialog).OfType<Button>(), b => b.Content is TextBlock { Text: "Refresh profile filters" });
                refresh.Command.Execute(refresh.CommandParameter); TwilightLayout(dialog, window.Width, window.Height);
                Assert.Equal(4, order.Items.Count); Assert.False(draft.Filters[3].Enabled); Assert.Equal(3, rig.Container.FilterOrder.Count);
                var preview = Assert.Single(TwilightVisuals(dialog).OfType<Button>(), button => button.Content is TextBlock { Text: "Show rotations" });
                var previewText = Assert.Single(TwilightVisuals(dialog).OfType<TextBlock>(), text => text.Name == "RotationPreview");
                foreach (var failure in new Exception[] {
                    new System.IO.InvalidDataException("Recorded flat rotations could not be read."),
                    new System.IO.IOException("Rotation history is unavailable.")
                }) {
                    rig.Rotation.History.PlanFailure = failure;
                    preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(failure.Message, previewText.Text);
                }
                rig.Rotation.History.PlanFailure = null;
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("10°, 190°, 45°", previewText.Text);
                SaveTwilightPreview(dialog, $"twilight-setup-{theme.Replace(' ', '-')}.png", (int)window.Width, (int)window.Height);
                // Resizing must preserve the names and keep the settings out of the sequence body.
                TwilightLayout(dialog, window.MinWidth, window.MinHeight);
                Assert.All(TwilightVisuals(order).OfType<TextBlock>().Where(label => Equals(label.Tag, "FilterName")), label => Assert.True(label.ActualWidth >= 180));
                draft.Apply(); window.Close(); TwilightLayout(host);
                Assert.Equal("R → L", rig.Container.FilterSummary);
                var expander = Assert.Single(TwilightVisuals(view).OfType<NINA.CustomControlLibrary.DetachingExpander>(), x => ReferenceEquals(x.DataContext, rig.Container));
                Assert.True(expander.IsExpanded);
                expander.SetCurrentValue(Expander.IsExpandedProperty, false); TwilightLayout(host);
                Assert.False(rig.Container.IsExpanded); Assert.DoesNotContain(TwilightVisuals(host).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, added));
                expander.SetCurrentValue(Expander.IsExpandedProperty, true); TwilightLayout(host);
                Assert.True(rig.Container.IsExpanded); Assert.Single(TwilightVisuals(host).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, added));
                SaveTwilightPreview(host, $"twilight-filters-{theme.Replace(' ', '-')}.png", 1200, 950);
                var cloned = Assert.IsType<TwilightFlatFiltersContainer>(rig.Container.Clone());
                var embedded = new ItemsControl { ItemsSource = new[] { cloned } }; embedded.Resources.MergedDictionaries.Add(resources);
                var embeddedHost = new Border { Child = embedded }; TwilightLayout(embeddedHost);
                var clonedFlat = Assert.Single(cloned.Items);
                Assert.True(Assert.Single(TwilightVisuals(embeddedHost).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, clonedFlat)).ActualHeight > 0);
            }
        } finally { Application.Current.Resources = previousResources; }
    }
}
