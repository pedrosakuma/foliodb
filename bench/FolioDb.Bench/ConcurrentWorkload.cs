using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using FolioDb;

/// <summary>Closed-loop contention experiment, not a BenchmarkDotNet microbenchmark.</summary>
public static class ConcurrentWorkload
{
    private const int Documents = 10000;
    private sealed record Scenario(int Writers, int Readers, int SnapshotMs = 0);

    private sealed class Histogram
    {
        private readonly long[] _bins = new long[2048];
        public long Count { get; private set; }
        public double Max { get; private set; }

        public void Add(double us)
        {
            int bin = Math.Min(_bins.Length - 1, (int)(Math.Log(us + 1) / Math.Log(1.02)));
            _bins[bin]++;
            Count++;
            Max = Math.Max(Max, us);
        }

        public void Merge(Histogram other)
        {
            for (int i = 0; i < _bins.Length; i++) _bins[i] += other._bins[i];
            Count += other.Count;
            Max = Math.Max(Max, other.Max);
        }

        public double Percentile(double p)
        {
            if (Count == 0) return 0;
            long target = (long)Math.Ceiling(Count * p), cumulative = 0;
            for (int i = 0; i < _bins.Length; i++)
                if ((cumulative += _bins[i]) >= target) return Math.Min(Max, Math.Pow(1.02, i + 1) - 1);
            return Max;
        }
    }

    private sealed class Worker
    {
        public Histogram Read { get; } = new();
        public Histogram Wait { get; } = new();
        public Histogram Service { get; } = new();
        public Histogram Write { get; } = new();
        public Histogram Commit { get; } = new();
        public Histogram TimeoutWait { get; } = new();
    }

    public static void Run(string[] args, bool sharding = false)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3;
        int repeats = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
        if (args.Length > 2 || seconds is < 1 or > 60 || repeats is < 1 or > 10)
            throw new ArgumentException("Usage: --concurrency | --sharding [seconds:1..60] [repeats:1..10]");
        Console.WriteLine("# Closed-loop; dedicated threads; 10k docs; 1KB payload; hot 256 IDs; $inc one Int64, no secondary index.");
        Console.WriteLine("# AutoCheckpointFrames=1000; BusyTimeout=1s; sampled WAL peak every 50ms; fresh DB per trial.");
        Console.WriteLine("# Latency in microseconds; histogram upper bounds <=2% plus 0.02us; service includes commit/checkpoint.");
        if (sharding) Console.WriteLine("# Experimental integer-only id % shards routing; 1/2/4 independent files on same device; total cache=4096 pages; no cross-shard guarantees.");
        Console.WriteLine("# " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            + "; CPUs=" + Environment.ProcessorCount + "; temp=" + Path.GetTempPath());
        Console.WriteLine("mode,writers,readers,snapshot_ms,trial,seconds,reads_s,writes_s,timeouts,writer_min,writer_max,wal_peak_mb,wal_end_mb,read_p50,read_p95,read_p99,wait_p50,wait_p95,wait_p99,service_p50,service_p95,service_p99,write_p50,write_p95,write_p99,write_max,commit_p50,commit_p95,commit_p99,timeout_wait_max,shards");
        Scenario[] scenarios = sharding ? [new(4, 0), new(4, 4)]
            : [new(0, 4), new(1, 0), new(4, 0), new(1, 4), new(4, 4), new(4, 4, 100)];
        foreach (var scenario in scenarios)
        {
            int[] shardCounts = sharding ? [1, 2, 4] : [1];
            var configurations = shardCounts.SelectMany(shards => new[] { SynchronousMode.Full, SynchronousMode.Normal }
                .Select(mode => (mode, shards))).ToArray();
            foreach (var (mode, shards) in configurations) Trial(mode, scenario, 1, 0, print: false, shards);
            for (int trial = 1; trial <= repeats; trial++)
            {
                foreach (var (mode, shards) in configurations) Trial(mode, scenario, seconds, trial, print: true, shards);
                Array.Reverse(configurations);
            }
        }
    }

    private static void Trial(SynchronousMode mode, Scenario scenario, int seconds, int trial, bool print, int shards)
    {
        var paths = Enumerable.Range(0, shards).Select(_ => Workload.TempFile(".folio")).ToArray();
        var databases = new List<FolioDatabase>();
        try
        {
            foreach (var path in paths) databases.Add(FolioDatabase.Open(path, new FolioOptions
            {
                Synchronous = mode, AutoCheckpointFrames = 1000, BusyTimeout = TimeSpan.FromSeconds(1),
                CacheSizePages = 4096 / shards,
            }));
            for (int shard = 0; shard < shards; shard++)
            {
                using var tx = databases[shard].BeginTransaction();
                var c = tx.GetCollection("items");
                for (int i = shard; i < Documents; i += shards)
                    c.Insert(new Document { ["_id"] = i, ["n"] = 0L, ["payload"] = new string('x', 1024) });
                tx.Commit();
            }
            foreach (var db in databases)
                if (!db.Checkpoint()) throw new InvalidOperationException("Setup checkpoint blocked.");
            for (int i = 0; i < 256; i++) _ = databases[i % shards].GetCollection("items").FindById(i);
            using var stop = new CancellationTokenSource();
            using var ready = new CountdownEvent(scenario.Readers + scenario.Writers);
            using var start = new ManualResetEventSlim();
            var errors = new ConcurrentQueue<Exception>();
            var workers = Enumerable.Range(0, scenario.Readers + scenario.Writers).Select(_ => new Worker()).ToArray();
            var threads = workers.Select((worker, index) => new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    if (index < scenario.Writers) WriteLoop(databases, worker, index, stop.Token);
                    else ReadLoop(databases, worker, index, scenario.SnapshotMs, stop.Token);
                }
                catch (Exception e) { errors.Enqueue(e); stop.Cancel(); }
            }) { IsBackground = true }).ToArray();
            foreach (var thread in threads) thread.Start();
            ready.Wait();
            var elapsed = Stopwatch.StartNew();
            var wals = paths.Select(path => new FileInfo(path + "-wal")).ToArray();
            long peak = 0;
            start.Set();
            try
            {
                while (elapsed.Elapsed.TotalSeconds < seconds && !stop.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                    long bytes = 0;
                    foreach (var wal in wals) { wal.Refresh(); bytes += wal.Length; }
                    peak = Math.Max(peak, bytes);
                }
            }
            finally
            {
                stop.Cancel();
                foreach (var thread in threads) thread.Join();
                elapsed.Stop();
            }
            if (!errors.IsEmpty) throw new AggregateException(errors);
            var total = new Worker();
            foreach (var w in workers)
            {
                total.Read.Merge(w.Read); total.Wait.Merge(w.Wait); total.Service.Merge(w.Service);
                total.Write.Merge(w.Write); total.Commit.Merge(w.Commit); total.TimeoutWait.Merge(w.TimeoutWait);
            }
            long walEnd = databases.Sum(db => db.GetStats().WalFileBytes);
            peak = Math.Max(peak, walEnd);
            long actual = 0;
            int documentCount = 0;
            for (int shard = 0; shard < shards; shard++)
            {
                var db = databases[shard];
                foreach (var doc in db.GetCollection("items").Find())
                {
                    if (doc["_id"].AsInt32 % shards != shard) throw new InvalidOperationException("Incorrect shard routing.");
                    actual += doc["n"].AsInt64;
                    documentCount++;
                }
                db.CheckIntegrity();
                if (!db.Checkpoint()) throw new InvalidOperationException("Leaked reader after trial.");
            }
            if (documentCount != Documents) throw new InvalidOperationException("Incorrect total document count.");
            if (actual != total.Write.Count) throw new InvalidOperationException($"Lost writes: {actual} != {total.Write.Count}.");
            if (!print) return;
            var writerCounts = workers.Take(scenario.Writers).Select(w => w.Write.Count).DefaultIfEmpty(0).ToArray();
            var values = new List<string>
            {
                mode.ToString(), scenario.Writers.ToString(), scenario.Readers.ToString(), scenario.SnapshotMs.ToString(),
                trial.ToString(), F(elapsed.Elapsed.TotalSeconds), F(total.Read.Count / elapsed.Elapsed.TotalSeconds),
                F(total.Write.Count / elapsed.Elapsed.TotalSeconds), total.TimeoutWait.Count.ToString(),
                writerCounts.Min().ToString(), writerCounts.Max().ToString(), F(peak / 1048576.0), F(walEnd / 1048576.0),
            };
            foreach (var h in new[] { total.Read, total.Wait, total.Service, total.Write })
                values.AddRange(new[] { F(h.Percentile(.50)), F(h.Percentile(.95)), F(h.Percentile(.99)) });
            values.Add(F(total.Write.Max));
            values.AddRange(new[] { F(total.Commit.Percentile(.50)), F(total.Commit.Percentile(.95)),
                F(total.Commit.Percentile(.99)), F(total.TimeoutWait.Max), shards.ToString() });
            Console.WriteLine(string.Join(',', values));
        }
        finally
        {
            try { foreach (var db in databases) db.Dispose(); }
            finally { foreach (var path in paths) Workload.Delete(path); }
        }
    }

    private static void WriteLoop(IReadOnlyList<FolioDatabase> databases, Worker worker, int seed, CancellationToken stop)
    {
        var random = new Random(seed);
        var update = new Document { ["$inc"] = new Document { ["n"] = 1L } };
        while (!stop.IsCancellationRequested)
        {
            int id = random.Next(256);
            var filter = new Document { ["_id"] = id };
            long before = Stopwatch.GetTimestamp();
            var db = databases[id % databases.Count];
            Transaction tx;
            try { tx = db.BeginTransaction(); }
            catch (FolioException e) when (e.Message == "The database is busy (timed out waiting for the write lock).")
            {
                worker.TimeoutWait.Add(Us(before, Stopwatch.GetTimestamp()));
                continue;
            }
            long acquired = Stopwatch.GetTimestamp(), commitStart;
            using (tx)
            {
                var result = tx.GetCollection("items").UpdateOne(filter, update);
                if (result.ModifiedCount != 1) throw new InvalidOperationException("Update did not modify one document.");
                commitStart = Stopwatch.GetTimestamp();
                tx.Commit();
            }
            long after = Stopwatch.GetTimestamp();
            worker.Wait.Add(Us(before, acquired));
            worker.Service.Add(Us(acquired, after));
            worker.Write.Add(Us(before, after));
            worker.Commit.Add(Us(commitStart, after));
        }
    }

    private static void ReadLoop(IReadOnlyList<FolioDatabase> databases, Worker worker, int seed, int snapshotMs, CancellationToken stop)
    {
        var random = new Random(seed);
        var collections = databases.Select(db => db.GetCollection("items")).ToArray();
        while (!stop.IsCancellationRequested)
        {
            if (snapshotMs == 0)
            {
                int id = random.Next(256);
                long before = Stopwatch.GetTimestamp();
                CheckRead(collections[id % collections.Length].FindById(id), id);
                worker.Read.Add(Us(before, Stopwatch.GetTimestamp()));
            }
            else
            {
                int id = random.Next(256);
                using var snapshot = databases[id % databases.Count].BeginSnapshot();
                var c = snapshot.GetCollection("items");
                long value = c.FindById(id)!["n"].AsInt64;
                long opened = Stopwatch.GetTimestamp();
                do
                {
                    long before = Stopwatch.GetTimestamp();
                    var doc = c.FindById(id);
                    CheckRead(doc, id);
                    if (doc!["n"].AsInt64 != value) throw new InvalidOperationException("Snapshot changed.");
                    worker.Read.Add(Us(before, Stopwatch.GetTimestamp()));
                } while (!stop.IsCancellationRequested && Stopwatch.GetElapsedTime(opened).TotalMilliseconds < snapshotMs);
            }
        }
    }

    private static void CheckRead(Document? doc, int id)
    {
        if (doc is null || doc["_id"].AsInt32 != id || doc["n"].AsInt64 < 0 || doc["payload"].AsString.Length != 1024)
            throw new InvalidOperationException("Invalid read.");
    }

    private static double Us(long start, long end) => (end - start) * 1000000.0 / Stopwatch.Frequency;
    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
