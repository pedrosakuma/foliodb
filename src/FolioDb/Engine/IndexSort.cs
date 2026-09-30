namespace FolioDb.Engine;

/// <summary>Bounded-memory sort of index entries, spilling sorted runs to delete-on-close temporary files.</summary>
internal sealed class IndexSort : IDisposable
{
    internal readonly record struct Entry(byte[] Key, int ValueLength, byte[] Hint, long Sequence);
    private const int RunBytes = 8 * 1024 * 1024;
    private const int MaxRuns = 128;
    private readonly List<Entry> _buffer = [];
    private readonly List<FileStream> _runs = [];
    private int _bytes;
    private long _sequence;
    private long _scratchBytes;
    [ThreadStatic] private static (long ScratchBytes, int Runs) s_lastMetrics;
    internal static (long ScratchBytes, int Runs) LastMetrics => s_lastMetrics;
    private static readonly Comparer<Entry> s_order = Comparer<Entry>.Create(static (a, b) =>
    {
        int comparison = a.Key.AsSpan().SequenceCompareTo(b.Key);
        return comparison != 0 ? comparison : a.Sequence.CompareTo(b.Sequence);
    });

    public IndexSort() => s_lastMetrics = default;

    public void Add(byte[] key, int valueLength, byte[] hint)
    {
        _buffer.Add(new Entry(key, valueLength, hint, _sequence++));
        _bytes += key.Length + hint.Length + 64;
        if (_bytes >= RunBytes) Flush();
    }

    private void Flush()
    {
        if (_buffer.Count == 0) return;
        if (_runs.Count == MaxRuns)
            throw new FolioException($"Index rebuild exceeds {MaxRuns} temporary sort runs.");
        _buffer.Sort(s_order);
        var stream = CreateRun();
        _runs.Add(stream);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var entry in _buffer)
            {
                writer.Write(entry.Key.Length);
                writer.Write(entry.ValueLength);
                writer.Write(entry.Hint.Length);
                writer.Write(entry.Sequence);
                writer.Write(entry.Key);
                writer.Write(entry.Hint);
            }
        stream.Position = 0;
        _scratchBytes += stream.Length;
        s_lastMetrics = (_scratchBytes, _runs.Count);
        _buffer.Clear();
        _bytes = 0;
    }

    public IEnumerable<Entry> Sorted()
    {
        if (_runs.Count == 0)
        {
            _buffer.Sort(s_order);
            foreach (var entry in _buffer) yield return entry;
            yield break;
        }
        Flush();
        var readers = _runs.Select(s => new BinaryReader(s, System.Text.Encoding.UTF8, leaveOpen: true)).ToArray();
        try
        {
            var heap = new PriorityQueue<(int Run, Entry Entry), Entry>(s_order);
            for (int i = 0; i < readers.Length; i++)
                if (Read(i) is { } entry) heap.Enqueue((i, entry), entry);
            while (heap.TryDequeue(out var item, out _))
            {
                yield return item.Entry;
                if (Read(item.Run) is { } next) heap.Enqueue((item.Run, next), next);
            }

            Entry? Read(int run)
            {
                var reader = readers[run];
                if (reader.BaseStream.Position == reader.BaseStream.Length) return null;
                int keyLength = reader.ReadInt32();
                int valueLength = reader.ReadInt32();
                int hintLength = reader.ReadInt32();
                long sequence = reader.ReadInt64();
                if (keyLength < 0 || valueLength < 0 || valueLength > keyLength || hintLength < 0)
                    throw new InvalidDataException("Invalid temporary index sort run.");
                var key = reader.ReadBytes(keyLength);
                var hint = reader.ReadBytes(hintLength);
                if (key.Length != keyLength || hint.Length != hintLength)
                    throw new EndOfStreamException("Truncated temporary index sort run.");
                return new Entry(key, valueLength, hint, sequence);
            }
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }

    internal static FileStream CreateRun()
    {
        var path = Path.Combine(Path.GetTempPath(), "foliodb-index-" + Guid.NewGuid().ToString("N"));
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 65536,
            Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public void Dispose()
    {
        foreach (var run in _runs) run.Dispose();
    }
}
