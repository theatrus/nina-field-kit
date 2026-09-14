using System.IO;
using System.Runtime.CompilerServices;

namespace Nina.FieldKit.Tests;

internal static class TestEnvironment {
    // NINA's logger normally writes to the user's NINA folder, even inside a test host.
    [ModuleInitializer]
    internal static void Initialize() {
        NINA.Core.Utility.CoreUtil.APPLICATIONTEMPPATH = Path.Combine(Path.GetTempPath(), "nina-field-kit-tests", Environment.ProcessId.ToString());
    }
}
