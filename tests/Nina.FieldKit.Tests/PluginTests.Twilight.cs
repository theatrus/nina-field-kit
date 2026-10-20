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
    private static void TwilightLayout(FrameworkElement host) {
        host.Measure(new Size(1200, 950)); host.Arrange(new Rect(0, 0, 1200, 950)); host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); host.UpdateLayout();
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
                    "Styles/Button", "Styles/TextBlock", "Styles/TextBox", "Styles/ComboBox", "Styles/ToggleButton", "Styles/Expander", "Styles/ListView", "Styles/TabControl" })
                    themed.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/NINA.WPF.Base;component/Resources/{resource}.xaml", UriKind.Relative) });
                themed.MergedDictionaries.Add(new NINA.Sequencer.SequenceItem.FlatDevice.Datatemplates());
                var root = new SequenceRootContainer(); var targets = new TargetAreaContainer();
                root.Add(new StartAreaContainer()); root.Add(targets); root.Add(new EndAreaContainer()); targets.Add(rig.Container);
                var native = new SequenceView { DataContext = new { Sequencer = root, IsLocked = false, CanDragAndDrop = true } };
                native.Resources.MergedDictionaries.Add(new NINA.Sequencer.Container.Datatemplates());
                native.Resources.MergedDictionaries.Add(resources);
                var host = new Border { Child = native, Padding = new Thickness(8) };
                host.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush"); TwilightLayout(host);
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
                var order = Assert.Single(TwilightVisuals(view).OfType<ItemsControl>(), x => Equals(x.Tag, "FlatFilterOrder"));
                var row = rig.Container.FilterOrder[1];
                var up = Assert.Single(TwilightVisuals(order).OfType<Button>(), b => ReferenceEquals(b.DataContext, row) && b.Content is TextBlock { Text: "↑" });
                Assert.True(up.IsEnabled); Assert.Same(row, up.CommandParameter); up.Command.Execute(up.CommandParameter); TwilightLayout(host);
                Assert.Same(row, rig.Container.FilterOrder[0]);
                var checkboxes = TwilightVisuals(order).OfType<CheckBox>().ToArray();
                Assert.Equal(new[] { "R", "L", "Ha" }, checkboxes.Select(c => c.Content));
                checkboxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, false); TwilightLayout(host); Assert.False(row.Enabled);
                checkboxes[0].SetCurrentValue(ToggleButton.IsCheckedProperty, true); TwilightLayout(host); Assert.True(row.Enabled);
                rig.Known = new(rig.Known.Append(new FilterInfo { Name = "OIII", Position = 3 }));
                var refresh = Assert.Single(TwilightVisuals(view).OfType<Button>(), b => b.Content is TextBlock { Text: "Refresh profile filters" });
                refresh.Command.Execute(refresh.CommandParameter); TwilightLayout(host);
                Assert.Equal(4, order.Items.Count); Assert.False(rig.Container.FilterOrder[3].Enabled);
                var expander = Assert.Single(TwilightVisuals(view).OfType<NINA.CustomControlLibrary.DetachingExpander>(), x => ReferenceEquals(x.DataContext, rig.Container));
                Assert.True(expander.IsExpanded);
                expander.SetCurrentValue(Expander.IsExpandedProperty, false); TwilightLayout(host);
                Assert.False(rig.Container.IsExpanded); Assert.DoesNotContain(TwilightVisuals(host).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, added));
                expander.SetCurrentValue(Expander.IsExpandedProperty, true); TwilightLayout(host);
                Assert.True(rig.Container.IsExpanded); Assert.Single(TwilightVisuals(host).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, added));
                var bitmap = new RenderTargetBitmap(1200, 950, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var output = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
                using (var file = System.IO.File.Create(System.IO.Path.Combine(output, $"twilight-filters-{theme.Replace(' ', '-')}.png"))) encoder.Save(file);
                var cloned = Assert.IsType<TwilightFlatFiltersContainer>(rig.Container.Clone());
                var embedded = new ItemsControl { ItemsSource = new[] { cloned } }; embedded.Resources.MergedDictionaries.Add(resources);
                var embeddedHost = new Border { Child = embedded }; TwilightLayout(embeddedHost);
                var clonedFlat = Assert.Single(cloned.Items);
                Assert.True(Assert.Single(TwilightVisuals(embeddedHost).OfType<SequenceBlockView>(), b => ReferenceEquals(b.DataContext, clonedFlat)).ActualHeight > 0);
            }
        } finally { Application.Current.Resources = previousResources; }
    }
}
