using System.Diagnostics;
using System.Text.Json;

namespace Nina.FieldKit.Testing;

public sealed record JournalEntry(long Sequence, DateTimeOffset Utc, double ElapsedSeconds, string Event, object Data);
public sealed class RunJournal : IDisposable {
    private readonly object gate = new();
    private readonly Queue<JournalEntry> entries = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly StreamWriter? writer;
    private long sequence;
    public RunJournal(string? path = null) {
        if (path is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); writer = new(path, append: false) { AutoFlush = true }; }
    }
    public void Write(string name, object data) {
        lock (gate) {
            var entry = new JournalEntry(++sequence, DateTimeOffset.UtcNow, clock.Elapsed.TotalSeconds, name, data);
            entries.Enqueue(entry); if (entries.Count > 10000) entries.Dequeue();
            writer?.WriteLine(JsonSerializer.Serialize(entry, FaultScenario.Json));
        }
    }
    public JournalEntry[] Snapshot() { lock (gate) return entries.ToArray(); }
    public void Dispose() { lock (gate) writer?.Dispose(); }
}
