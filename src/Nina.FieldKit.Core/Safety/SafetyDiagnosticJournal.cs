namespace Nina.FieldKit.Core.Safety;

public sealed record SafetyDiagnosticEvent(DateTimeOffset Timestamp, string Level, string Event, string Details);

// Independent of the safety lock: diagnostics must never change safety decisions.
public sealed class SafetyDiagnosticJournal(TimeProvider? timeProvider = null) {
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Queue<SafetyDiagnosticEvent> events = new();
    private long? traceStart;
    public const int Capacity = 300;
    public TimeSpan TraceRemaining {
        get { lock (gate) {
            var remaining = traceStart is long start ? TimeSpan.FromMinutes(5) - clock.GetElapsedTime(start) : TimeSpan.Zero;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        } }
    }
    public void SetTrace(bool enabled) { lock (gate) traceStart = enabled ? clock.GetTimestamp() : null; }
    public SafetyDiagnosticEvent[] Snapshot() { lock (gate) return events.ToArray(); }
    public SafetyDiagnosticEvent? Add(string name, string details, string level = "Info", bool traceOnly = false) {
        lock (gate) {
            if (traceOnly && TraceRemaining == TimeSpan.Zero) return null;
            // Keep single-line bounded entries, including user-supplied source labels.
            var singleLine = details.Replace("\r", "\\r").Replace("\n", "\\n");
            var entry = new SafetyDiagnosticEvent(clock.GetUtcNow(), level, name, singleLine[..Math.Min(singleLine.Length, 16000)]);
            events.Enqueue(entry);
            while (events.Count > Capacity) events.Dequeue();
            return entry;
        }
    }
}
