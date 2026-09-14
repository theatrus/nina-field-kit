using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

[assembly: Guid("d2487d32-9277-45ec-b5df-89ecbff61c78")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("Repository", "https://github.com/theatrus/nina-field-kit")]
[assembly: AssemblyMetadata("License", "Apache-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.apache.org/licenses/LICENSE-2.0")]
[assembly: AssemblyMetadata("FeaturedImageURL", "pack://application:,,,/Nina.FieldKit.Plugin;component/Assets/field-kit.png")]

namespace Nina.FieldKit.Plugin;

[Export(typeof(IPluginManifest))]
public sealed class FieldKitPlugin : PluginBase {
    [Import(AllowDefault = true)]
    public Safety.SafetyMonitorProvider? SafetyProvider { get; set; }

    public override Task Teardown() {
        SafetyProvider?.Dispose();
        return base.Teardown();
    }
}
