using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Nina.FieldKit.Plugin;

internal static class FieldKitIcon {
    internal const string ImageUri = "pack://application:,,,/Nina.FieldKit.Plugin;component/Assets/field-kit.png";
    private static readonly Geometry geometry = LoadGeometry();

    private static Geometry LoadGeometry() {
        using var stream = Application.GetResourceStream(new Uri("/Nina.FieldKit.Plugin;component/Assets/field-kit.svg", UriKind.Relative))!.Stream;
        var svg = XDocument.Load(stream);
        var shape = Geometry.Parse(svg.Root!.Element(XName.Get("path", "http://www.w3.org/2000/svg"))!.Attribute("d")!.Value);
        shape.Freeze();
        return shape;
    }

    internal static FrameworkElement Heading(string title) {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new System.Windows.Shapes.Path { Data = geometry, Width = 36, Height = 36, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "PrimaryBrush");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    internal static ImageSource WindowIcon() => BitmapFrame.Create(new Uri(ImageUri));
}
