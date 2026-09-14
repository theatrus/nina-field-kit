using System.Text.Json;
using System.Text.Json.Serialization;
using Nina.FieldKit.Core.Safety;

if (args.Length is < 2 or > 3 || !int.TryParse(args[1], out var deviceNumber) ||
    (args.Length == 3 && (!int.TryParse(args[2], out var count) || count is < 1 or > 30))) {
    Console.Error.WriteLine("Usage: Nina.FieldKit.Probe <base-url> <device-number> [samples:1-30]");
    return 1;
}
var samples = args.Length == 3 ? int.Parse(args[2]) : 3;
var options = new SafetyEndpointOptions {
    BaseUrl = args[0], DeviceNumber = deviceNumber, ConnectionPolicy = ConnectionPolicy.ExternallyManaged
};
try { options.Validate(); }
catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 1; }
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
using var client = new AlpacaSafetyClient(options);
var failed = false;
try {
    for (var sample = 1; sample <= samples; sample++) {
        var result = await client.PollAsync(stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(new {
            timestamp = DateTimeOffset.UtcNow, sample, deviceNumber,
            connectionPolicy = options.ConnectionPolicy, options.MaximumSafeAgeSeconds,
            options.ConnectionLifetimeSeconds, result
        }, json));
        failed |= result.Outcome != PollOutcome.Observation;
        if (result.Outcome == PollOutcome.PermanentFailure) break;
        if (sample < samples) await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stop.Token);
    }
} catch (OperationCanceledException) { Console.Error.WriteLine("Probe cancelled or its two-minute budget expired."); return 3; }
// Unsafe is a valid observation, not a transport/protocol test failure.
return failed ? 2 : 0;
