namespace Nina.FieldKit.Core.Safety;

public sealed class RetryBackoff(SafetyEndpointOptions options, Func<double>? random = null) {
    private int streak;
    public void Reset() => streak = 0;

    public TimeSpan Next(TimeSpan? retryAfter = null) {
        streak = Math.Min(streak + 1, 1024);
        var ceiling = Math.Min(options.BackoffCapSeconds,
            options.InitialBackoffSeconds * Math.Pow(options.BackoffMultiplier, streak - 1));
        var unit = Math.Clamp((random ?? Random.Shared.NextDouble)(), 0, 1);
        var delay = ceiling * (.5 + unit / 2);
        return TimeSpan.FromSeconds(Math.Max(delay, retryAfter?.TotalSeconds ?? 0));
    }
}
