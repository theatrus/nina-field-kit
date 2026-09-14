using System.ComponentModel.Composition;
using System.Windows;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class SequenceTemplates : ResourceDictionary {
    public SequenceTemplates() => InitializeComponent();
}
