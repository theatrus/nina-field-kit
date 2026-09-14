using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nina.FieldKit.Testing;

public sealed record SimulatedDevice(int Number = 0, int InterfaceVersion = 3, bool Connected = true);
public enum ReplyKind { Alpaca, Http, Raw, Close, Reset, Stall }
public sealed record FaultReply {
    public ReplyKind Kind { get; init; }
    public JsonElement Value { get; init; } = JsonSerializer.SerializeToElement(true);
    public int Status { get; init; } = 200;
    public int ErrorNumber { get; init; }
    public string ErrorMessage { get; init; } = "";
    public bool OmitErrorFields { get; init; }
    public string? Body { get; init; }
    public string? RawHttp { get; init; }
    public string? BodyBase64 { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public int HeaderDelayMs { get; init; }
    public int BodyDelayMs { get; init; }
    public int ChunkBytes { get; init; }
    public int ChunkDelayMs { get; init; }
    public int? TruncateAfterBytes { get; init; }
    public bool StallAfterHeaders { get; init; }
    public bool CloseAfter { get; init; }
    public bool Chunked { get; init; }
    public static FaultReply Safe(bool value = true) => new() { Value = JsonSerializer.SerializeToElement(value) };
    public static FaultReply Http(int status) => new() { Kind = ReplyKind.Http, Status = status, Body = "temporary failure" };
    public void Validate() {
        if (!Enum.IsDefined(Kind) || Status is < 100 or > 599 || Value.ValueKind == JsonValueKind.Undefined ||
            HeaderDelayMs is < 0 or > 120000 || BodyDelayMs is < 0 or > 120000 || ChunkDelayMs is < 0 or > 120000 ||
            ChunkBytes is < 0 or > 1048576 || TruncateAfterBytes is < 0 or > 1048576 ||
            (Body?.Length ?? 0) > 1048576 || (RawHttp?.Length ?? 0) > 1048576 || ErrorMessage.Length > 65536 ||
            Headers.Count > 64 || Headers.Any(h => h.Key.Length > 128 || h.Value.Length > 65536 ||
                h.Key.IndexOfAny(['\r', '\n', ':']) >= 0 || h.Value.IndexOfAny(['\r', '\n']) >= 0))
            throw new ArgumentException("Invalid or unbounded fault reply");
        if (Kind == ReplyKind.Raw && Body is null && RawHttp is null && BodyBase64 is null) throw new ArgumentException("Raw reply requires Body or RawHttp");
        if (BodyBase64 is not null && (BodyBase64.Length > 1400000 || Convert.FromBase64String(BodyBase64).Length > 1048576)) throw new ArgumentException("Raw body exceeds limit");
    }
}
public sealed record FaultStep(int Repeat, FaultReply Reply);
public sealed record FaultRule {
    public int Device { get; init; }
    public string Method { get; init; } = "GET";
    public string Member { get; init; } = "issafe";
    public FaultReply Baseline { get; init; } = FaultReply.Safe();
    public FaultStep[] Steps { get; init; } = [];
    public FaultReply After { get; init; } = FaultReply.Safe(false);
}
public sealed record FaultScenario {
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "manual";
    public int MaximumRunSeconds { get; init; } = 600;
    public bool Armed { get; init; } = true;
    public SimulatedDevice[] Devices { get; init; } = [new()];
    public FaultRule[] Rules { get; init; } = [];
    public static JsonSerializerOptions Json { get; } = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public static FaultScenario Load(string path) {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ArgumentException("Scenario exceeds 4 MiB");
        var result = JsonSerializer.Deserialize<FaultScenario>(File.ReadAllText(path), Json) ?? throw new ArgumentException("Empty scenario");
        result.Validate(); return result;
    }
    public void Validate() {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || MaximumRunSeconds is < 1 or > 7200 ||
            Devices.Length is < 1 or > 16 || Devices.Any(d => d.Number < 0 || d.InterfaceVersion is < 1 or > 3) ||
            Devices.Select(d => d.Number).Distinct().Count() != Devices.Length || Rules.Length > 128)
            throw new ArgumentException("Invalid scenario header/devices");
        if (Rules.Select(r => (r.Device, r.Method, r.Member)).Distinct().Count() != Rules.Length) throw new ArgumentException("Ambiguous duplicate rules");
        foreach (var rule in Rules) {
            if (!Devices.Any(d => d.Number == rule.Device) || rule.Method is not ("GET" or "PUT") ||
                rule.Member is not ("issafe" or "connected" or "interfaceversion" or "connect" or "connecting") ||
                rule.Steps.Length > 1000 || rule.Steps.Any(s => s.Repeat is < 1 or > 100000)) throw new ArgumentException("Invalid rule");
            rule.Baseline.Validate(); rule.After.Validate(); foreach (var step in rule.Steps) step.Reply.Validate();
        }
    }
}
