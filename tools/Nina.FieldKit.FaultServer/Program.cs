using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Nina.FieldKit.Testing;

// Explicit arguments only; no observing profile or production endpoint is discovered or modified.
var values = new Dictionary<string, string>();
try {
    for (var i = 0; i < args.Length; i += 2) {
        if (i + 1 == args.Length || args[i] is not ("--scenario" or "--alpaca-port" or "--control-port" or "--output" or "--suite")) throw new ArgumentException("Unknown/missing argument");
        values.Add(args[i], args[i + 1]);
    }
    var output = Path.GetFullPath(values.GetValueOrDefault("--output", $"artifacts/fault-run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
    Directory.CreateDirectory(output);
    if (values.TryGetValue("--suite", out var suite)) return await AcceptanceSuite.RunAsync(suite, output);
    if (!values.TryGetValue("--scenario", out var path)) throw new ArgumentException("--scenario is required (or --suite defaults|soak)");
    var scenario = FaultScenario.Load(path);
    int Port(string name) { var value = int.Parse(values.GetValueOrDefault(name, "0")); return value is >= 0 and <= 65535 ? value : throw new ArgumentException("Invalid port"); }
    var alpacaPort = Port("--alpaca-port"); var controlPort = Port("--control-port");
    if (alpacaPort != 0 && alpacaPort == controlPort) throw new ArgumentException("Use different ports for data and control");
    File.WriteAllText(Path.Combine(output, "scenario.json"), JsonSerializer.Serialize(scenario, FaultScenario.Json));
    using var journal = new RunJournal(Path.Combine(output, "server.jsonl"));
    await using var server = new Nina.FieldKit.Testing.FaultServer(scenario, alpacaPort, journal: journal);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, controlPort); k.Limits.MaxRequestBodySize = 1024; });
    await using var app = builder.Build();
    app.Use(async (context, next) => {
        if (context.Request.Host.Host != "127.0.0.1" || context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
        if (context.Request.Method != "GET" && context.Request.Headers["X-Control-Token"] != token) { context.Response.StatusCode = 403; return; }
        await next(context);
    });
    app.MapGet("/status", () => server.Status);
    app.MapGet("/events", () => journal.Snapshot());
    app.MapPost("/activate", () => { server.Activate(); return Results.Ok(server.Status); });
    app.MapPost("/reset", () => { server.Reset(); return Results.Ok(server.Status); });
    app.MapPost("/pause", async () => { await server.PauseAsync(); return Results.Ok(server.Status); });
    app.MapPost("/resume", () => { server.Resume(); return Results.Ok(server.Status); });
    app.MapPost("/stop", () => { app.Lifetime.StopApplication(); return Results.Ok(); });
    await app.StartAsync();
    var ready = new { server.BaseUrl, controlUrl = app.Urls.Single(), controlToken = token, scenario.Id, devices = scenario.Devices.Select(d => d.Number), output };
    File.WriteAllText(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(ready, FaultScenario.Json));
    Console.WriteLine(JsonSerializer.Serialize(ready, FaultScenario.Json));
    using var lifetime = server.Lifetime.Register(app.Lifetime.StopApplication);
    await app.WaitForShutdownAsync();
    return server.Errors.Length == 0 ? 0 : 2;
} catch (Exception e) {
    Console.Error.WriteLine($"Fault runner failed: {e.Message}");
    Console.Error.WriteLine("Usage: --scenario <json> [--alpaca-port 11111] [--control-port 11112] [--output <directory>] OR --suite defaults|soak [--output <directory>]");
    return 1;
}
