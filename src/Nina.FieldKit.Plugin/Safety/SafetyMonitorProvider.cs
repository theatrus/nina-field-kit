using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;

namespace Nina.FieldKit.Plugin.Safety;

[Export(typeof(IEquipmentProvider))]
[Export(typeof(SafetyMonitorProvider))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class SafetyMonitorProvider : IEquipmentProvider<ISafetyMonitor>, IDisposable {
    public string Name => "NINA Field Kit";
    public FieldKitSafetyMonitor Device { get; }

    [ImportingConstructor]
    public SafetyMonitorProvider(IProfileService profiles) => Device = new FieldKitSafetyMonitor(profiles);
    public IList<ISafetyMonitor> GetEquipment() => new List<ISafetyMonitor> { Device };
    public void Dispose() => Device.Dispose();
}
