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
        public long[] Increments { get; } = new long[256];
        public Histogram Read { get; } = new();
        public Histogram Wait { get; } = new();
        public Histogram Service { get; } = new();
        public Histogram Write { get; } = new();
        public Histogram Commit { get; } = new();
        public Histogram TimeoutWait { get; } = new();
    }

    public static void Run(string[] args, bool sharding = false, bool batching = false, bool fairness = false)
    {
        if ((sharding ? 1 : 0) + (batching ? 1 : 0) + (fairness ? 1 : 0) > 1)
            throw new ArgumentException("Sharding, batching and fairness are separate experiments.");
        int seconds = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3;
        int repeats = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
        var fairnessMode = SynchronousMode.Full;
        if (fairness && args.Length > 2)
            fairnessMode = args[2] switch
            {
                "full" => SynchronousMode.Full,
                "normal" => SynchronousMode.Normal,
                _ => throw new ArgumentException("Fairness durability must be full or normal."),
            };
        if (args.Length > (fairness ? 3 : 2) || seconds is < 1 or > 60 || repeats is < 1 or > 10)
            throw new ArgumentException("Usage: --concurrency | --sharding | --batching [seconds:1..60] [repeats:1..10] | --fairness [seconds] [repeats] [full|normal]");
        if (fairness) FifoAdmission.Verify();
        Console.WriteLine("# Closed-loop; dedicated threads; 10k docs; 1KB payload; hot 256 IDs; $inc one Int64, no secondary index.");
        Console.WriteLine("# AutoCheckpointFrames=1000; BusyTimeout=1s; sampled WAL peak every 50ms; fresh DB per trial.");
        Console.WriteLine("# Latency in microseconds; histogram upper bounds <=2% plus 0.02us; service includes commit/checkpoint.");
        if (sharding) Console.WriteLine("# Experimental integer-only id % shards routing; 1/2/4 independent files on same device; total cache=4096 pages; no cross-shard guarantees.");
        if (batching) Console.WriteLine("# Explicit batches of 1/8/32 updates per atomic transaction, single file; no request-arrival/batch-fill delay modeled.");
        if (fairness) Console.WriteLine($"# {fairnessMode} only; direct vs external FIFO vs integrated FIFO admission; 1s admission timeout.");
        Console.WriteLine("# writes_s and writer_min/max count successful transactions; updates_s counts committed increments; write_* latency is per whole transaction.");
        Console.WriteLine("# " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            + "; CPUs=" + Environment.ProcessorCount + "; temp=" + Path.GetTempPath());
        Console.WriteLine("mode,writers,readers,snapshot_ms,trial,seconds,reads_s,writes_s,timeouts,writer_min,writer_max,wal_peak_mb,wal_end_mb,read_p50,read_p95,read_p99,wait_p50,wait_p95,wait_p99,service_p50,service_p95,service_p99,write_p50,write_p95,write_p99,write_max,commit_p50,commit_p95,commit_p99,timeout_wait_max,shards,batch_size,updates_s,admission,wait_max,writer_counts");
        Scenario[] scenarios = fairness ? [new(1, 0), new(1, 4), new(4, 0), new(4, 4), new(16, 0), new(16, 4)]
            : batching ? [new(1, 0), new(4, 0), new(4, 4)] : sharding ? [new(4, 0), new(4, 4)]
            : [new(0, 4), new(1, 0), new(4, 0), new(1, 4), new(4, 4), new(4, 4, 100)];
        foreach (var scenario in scenarios)
        {
            int[] shardCounts = sharding ? [1, 2, 4] : [1];
            int[] batchSizes = batching ? [1, 8, 32] : [1];
            SynchronousMode[] modes = fairness ? [fairnessMode] : [SynchronousMode.Full, SynchronousMode.Normal];
            string[] admissions = fairness ? ["direct", "fifo", "integrated"] : ["direct"];
            var configurations = shardCounts.SelectMany(shards => batchSizes.SelectMany(batchSize =>
                modes.SelectMany(mode => admissions.Select(fifo => (mode, shards, batchSize, fifo))))).ToArray();
            foreach (var (mode, shards, batchSize, fifo) in configurations) Trial(mode, scenario, 1, 0, print: false, shards, batchSize, fifo);
            for (int trial = 1; trial <= repeats; trial++)
            {
                foreach (var (mode, shards, batchSize, fifo) in configurations) Trial(mode, scenario, seconds, trial, print: true, shards, batchSize, fifo);
                Array.Reverse(configurations);
            }
        }
    }

    public static void Profile(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("direct" or "integrated")
            || !int.TryParse(args[1], out int seconds) || seconds is < 5 or > 300)
            throw new ArgumentException("Usage: --profile-writers direct|integrated seconds:5..300");
        Console.WriteLine($"# PROFILE pid={Environment.ProcessId} admission={args[0]} Full 16 writers 4 readers");
        Trial(SynchronousMode.Full, new(16, 4), 3, 0, false, 1, 1, args[0]);
        Trial(SynchronousMode.Full, new(16, 4), seconds, 1, true, 1, 1, args[0], profile: true);
    }

    private static void Trial(SynchronousMode mode, Scenario scenario, int seconds, int trial, bool print, int shards, int batchSize, string fifo, bool profile = false)
    {
        var paths = Enumerable.Range(0, shards).Select(_ => Workload.TempFile(".folio")).ToArray();
        var databases = new List<FolioDatabase>();
        try
        {
            foreach (var path in paths) databases.Add(FolioDatabase.Open(path, new FolioOptions
            {
                Synchronous = mode, AutoCheckpointFrames = 1000, BusyTimeout = TimeSpan.FromSeconds(1),
                CacheSizePages = 4096 / shards,
                WriterAdmission = fifo == "integrated" ? WriterAdmissionMode.Fifo : WriterAdmissionMode.Unordered,
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
            var admission = fifo == "fifo" ? new FifoAdmission() : null;
            var workers = Enumerable.Range(0, scenario.Readers + scenario.Writers).Select(_ => new Worker()).ToArray();
            var threads = workers.Select((worker, index) => new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                try
                {
                    if (index < scenario.Writers) WriteLoop(databases, worker, index, batchSize, admission, stop.Token);
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
            if (profile) Console.WriteLine($"# LOAD_START pid={Environment.ProcessId} utc={DateTime.UtcNow:O} seconds={seconds}");
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
            if (profile) Console.WriteLine($"# LOAD_END utc={DateTime.UtcNow:O}");
            if (!errors.IsEmpty) throw new AggregateException(errors);
            var total = new Worker();
            foreach (var w in workers)
            {
                total.Read.Merge(w.Read); total.Wait.Merge(w.Wait); total.Service.Merge(w.Service);
                total.Write.Merge(w.Write); total.Commit.Merge(w.Commit); total.TimeoutWait.Merge(w.TimeoutWait);
                for (int id = 0; id < total.Increments.Length; id++) total.Increments[id] += w.Increments[id];
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
                    int id = doc["_id"].AsInt32;
                    if (id < 0 || id >= Documents || id % shards != shard) throw new InvalidOperationException("Incorrect shard routing.");
                    long expected = id < total.Increments.Length ? total.Increments[id] : 0;
                    if (doc["n"].AsInt64 != expected) throw new InvalidOperationException($"Incorrect increments for ID {id}.");
                    actual += doc["n"].AsInt64;
                    documentCount++;
                }
                db.CheckIntegrity();
                if (!db.Checkpoint()) throw new InvalidOperationException("Leaked reader after trial.");
            }
            if (documentCount != Documents) throw new InvalidOperationException("Incorrect total document count.");
            long expectedUpdates = total.Write.Count * batchSize;
            if (actual != expectedUpdates) throw new InvalidOperationException($"Lost writes: {actual} != {expectedUpdates}.");
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
                F(total.Commit.Percentile(.99)), F(total.TimeoutWait.Max), shards.ToString(), batchSize.ToString(),
                F(expectedUpdates / elapsed.Elapsed.TotalSeconds), fifo, F(total.Wait.Max),
                string.Join(';', workers.Take(scenario.Writers).Select(w => w.Write.Count)) });
            Console.WriteLine(string.Join(',', values));
        }
        finally
        {
            try { foreach (var db in databases) db.Dispose(); }
            finally { foreach (var path in paths) Workload.Delete(path); }
        }
    }

    private static void WriteLoop(IReadOnlyList<FolioDatabase> databases, Worker worker, int seed, int batchSize, FifoAdmission? admission, CancellationToken stop)
    {
        var random = new Random(seed);
        var update = new Document { ["$inc"] = new Document { ["n"] = 1L } };
        var ids = new int[batchSize];
        var filters = Enumerable.Range(0, batchSize).Select(_ => new Document { ["_id"] = 0 }).ToArray();
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < batchSize; i++)
            {
                ids[i] = random.Next(256);
                filters[i]["_id"] = ids[i];
            }
            long before = Stopwatch.GetTimestamp();
            var db = databases[ids[0] % databases.Count];
            long acquired, commitStart, transactionFinished, after;
            using (var lease = admission?.Enter(TimeSpan.FromSeconds(1)))
            {
                if (admission is not null && lease is null)
                {
                    worker.TimeoutWait.Add(Us(before, Stopwatch.GetTimestamp()));
                    continue;
                }
                Transaction tx;
                try { tx = db.BeginTransaction(); }
                catch (FolioException e) when (admission is null && e.Message == "The database is busy (timed out waiting for the write lock).")
                {
                    worker.TimeoutWait.Add(Us(before, Stopwatch.GetTimestamp()));
                    continue;
                }
                acquired = Stopwatch.GetTimestamp();
                using (tx)
                {
                    var collection = tx.GetCollection("items");
                    foreach (var filter in filters)
                    {
                        var result = collection.UpdateOne(filter, update);
                        if (result.ModifiedCount != 1) throw new InvalidOperationException("Update did not modify one document.");
                    }
                    commitStart = Stopwatch.GetTimestamp();
                    tx.Commit();
                }
                transactionFinished = Stopwatch.GetTimestamp();
            }
            after = Stopwatch.GetTimestamp();
            foreach (int id in ids) worker.Increments[id]++;
            worker.Wait.Add(Us(before, acquired));
            worker.Service.Add(Us(acquired, after));
            worker.Write.Add(Us(before, after));
            worker.Commit.Add(Us(commitStart, transactionFinished));
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
