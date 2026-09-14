namespace Nina.FieldKit.Core.Safety;

public enum ConnectionPolicy { ExternallyManaged, Managed }

public sealed record SafetyEndpointOptions {
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "Safety source";
    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "http://localhost:11111";
    public int DeviceNumber { get; init; }
    public ConnectionPolicy ConnectionPolicy { get; init; }
    public double PollSeconds { get; init; } = 30;
    public double RequestTimeoutSeconds { get; init; } = 1;
    public int AttemptsPerCycle { get; init; } = 3;
    public double InitialBackoffSeconds { get; init; } = .5;
    public double BackoffMultiplier { get; init; } = 2;
    public double BackoffCapSeconds { get; init; } = 30;
    public int FailedCyclesToUnsafe { get; init; } = 3;
    public int UnsafeReadingsToUnsafe { get; init; } = 1;
    public int SafeReadingsToSafe { get; init; } = 3;
    public double MaximumSafeAgeSeconds { get; init; } = 90;
    public double ReturnToSafeHoldSeconds { get; init; } = 10;
    public double ConnectionLifetimeSeconds { get; init; } = 1800;
    public string? CredentialReference { get; init; }

    public Uri NormalizedBaseUri() {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Base URL must be HTTP(S), without credentials, query, or fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public void Validate() {
        _ = NormalizedBaseUri();
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Label) || Label.Length > 100 || DeviceNumber < 0)
            throw new ArgumentException("Each endpoint needs an ID, a label of 1–100 characters, and a nonnegative device number.");
        if (!Enum.IsDefined(ConnectionPolicy)) throw new ArgumentException("Unsupported connection policy.");
        if (!string.IsNullOrEmpty(CredentialReference)) throw new ArgumentException("Credential schemes are not supported in this version.");
        static void Range(double value, double min, double max, string name) {
            if (!double.IsFinite(value) || value < min || value > max)
                throw new ArgumentException($"{name} must be between {min} and {max}.");
        }
        Range(PollSeconds, .1, 3600, nameof(PollSeconds));
        Range(RequestTimeoutSeconds, .05, 60, nameof(RequestTimeoutSeconds));
        Range(InitialBackoffSeconds, .05, 300, nameof(InitialBackoffSeconds));
        Range(BackoffMultiplier, 1, 10, nameof(BackoffMultiplier));
        Range(BackoffCapSeconds, InitialBackoffSeconds, 3600, nameof(BackoffCapSeconds));
        Range(MaximumSafeAgeSeconds, .1, 3600, nameof(MaximumSafeAgeSeconds));
        Range(ReturnToSafeHoldSeconds, 0, 3600, nameof(ReturnToSafeHoldSeconds));
        Range(ConnectionLifetimeSeconds, 1, 1800, nameof(ConnectionLifetimeSeconds));
        if (PollSeconds + RequestTimeoutSeconds >= MaximumSafeAgeSeconds)
            throw new ArgumentException("The safe-result age limit must be longer than the server check interval plus request timeout, so a normal check can finish before evidence expires.");
        if (AttemptsPerCycle is < 1 or > 10 || FailedCyclesToUnsafe is < 1 or > 1000 ||
            UnsafeReadingsToUnsafe is < 1 or > 1000 || SafeReadingsToSafe is < 1 or > 1000)
            throw new ArgumentException("Attempts must be 1–10 and confirmation counts 1–1000.");
    }
}

public sealed record SafetyConfiguration {
    public int SchemaVersion { get; init; } = 1;
    public Guid Revision { get; init; } = Guid.NewGuid();
    public IReadOnlyList<SafetyEndpointOptions> Endpoints { get; init; } = Array.Empty<SafetyEndpointOptions>();

    public SafetyConfiguration Freeze() {
        if (SchemaVersion != 1 || Revision == Guid.Empty) throw new ArgumentException("Unsupported configuration schema or missing revision.");
        if (Endpoints is null || Endpoints.Count > 16 || !Endpoints.Any(e => e is { Enabled: true }))
            throw new ArgumentException("Configure 1–16 endpoints with at least one enabled source.");
        var endpoints = Endpoints.ToArray();
        foreach (var endpoint in endpoints) {
            if (endpoint is null) throw new ArgumentException("An endpoint cannot be null.");
            endpoint.Validate();
        }
        if (endpoints.Select(e => e.Id).Distinct().Count() != endpoints.Length ||
            endpoints.Select(e => (e.NormalizedBaseUri().AbsoluteUri, e.DeviceNumber)).Distinct().Count() != endpoints.Length)
            throw new ArgumentException("Duplicate endpoint IDs or normalized URL/device pairs are not allowed.");
        return this with { Endpoints = Array.AsReadOnly(endpoints) };
    }
}
