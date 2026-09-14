using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NINA.Core.Utility;

namespace Nina.FieldKit.Plugin;

internal sealed class RunDiagnostics(string action) {
    private readonly Guid correlationId = Guid.NewGuid();
    private static readonly JsonSerializerSettings Settings = new() {
        Converters = { new StringEnumConverter() }
    };

    public void Write(string transition, object? evidence = null) => Logger.Info("FieldKit " + JsonConvert.SerializeObject(new {
        correlationId, action, version = typeof(FieldKitPlugin).Assembly.GetName().Version?.ToString(),
        timestamp = DateTimeOffset.UtcNow, transition, evidence
    }, Settings));
}
