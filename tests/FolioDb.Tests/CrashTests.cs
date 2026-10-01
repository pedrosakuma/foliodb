using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FolioDb.Tests;

/// <summary>Kills a real writer process (SIGKILL) in the middle of work and verifies no committed data is lost.</summary>
public partial class CrashTests
{
    [GeneratedRegex(@"^committed (\d+)$")]
    private static partial Regex CommittedLine();

    [GeneratedRegex(@"^ack (\d+) (\d+)$")]
    private static partial Regex AckLine();

    private static string SmokeAssembly()
    {
        // tests/FolioDb.Tests/bin/<Config>/net10.0/ -> tests/FolioDb.AotSmoke/bin/<Config>/net10.0/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string tfm = baseDir.Name, config = baseDir.Parent!.Name;
        var testsDir = baseDir.Parent!.Parent!.Parent!.Parent!;
        return Path.Combine(testsDir.FullName, "FolioDb.AotSmoke", "bin", config, tfm, "FolioDb.AotSmoke.dll");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(900)]
    public void Killed_writer_process_loses_no_committed_transactions(int killAfter)
    {
        var dll = SmokeAssembly();
        Assert.True(File.Exists(dll), $"Missing {dll}");
        using var tmp = new TempDb();
        long lastCommitted = 0;

        for (int round = 0; round < 2; round++)
        {
            var psi = new ProcessStartInfo("dotnet", [dll, "crash-writer", tmp.Path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var target = lastCommitted + killAfter;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                var line = proc.StandardOutput.ReadLine();
                if (line is null) break;
                var m = CommittedLine().Match(line);
                if (m.Success) lastCommitted = long.Parse(m.Groups[1].Value);
                if (lastCommitted >= target) break;
            }
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit();
            // Lines already in the pipe were acknowledged before the kill.
            for (string? line; (line = proc.StandardOutput.ReadLine()) is not null;)
                if (CommittedLine().Match(line) is { Success: true } m) lastCommitted = long.Parse(m.Groups[1].Value);
            Assert.True(lastCommitted >= target, $"writer did not reach {target} (stderr: {proc.StandardError.ReadToEnd()})");

            using var db = FolioDatabase.Open(tmp.Path);
            long count = db.GetCollection("events").Count();
            // Every acknowledged commit must be present; at most one more (committed but not yet printed).
            Assert.InRange(count, lastCommitted, lastCommitted + 1);
            db.CheckIntegrity();
            lastCommitted = count;
        }
    }

    [Fact]
    public void Killed_concurrent_writers_lose_no_acknowledged_group_commit()
    {
        const int Threads = 4;
        var dll = SmokeAssembly();
        Assert.True(File.Exists(dll), $"Missing {dll}");
        using var tmp = new TempDb();
        var acked = new long[Threads];
        Array.Fill(acked, -1);

        for (int round = 0; round < 2; round++)
        {
            var psi = new ProcessStartInfo("dotnet", [dll, "crash-writers", tmp.Path, Threads.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            long target = acked.Sum() + 400, total = 0;
            var sw = Stopwatch.StartNew();
            void Track(string line)
            {
                if (AckLine().Match(line) is not { Success: true } m) return;
                int t = int.Parse(m.Groups[1].Value);
                acked[t] = Math.Max(acked[t], long.Parse(m.Groups[2].Value));
            }
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                var line = proc.StandardOutput.ReadLine();
                if (line is null) break;
                Track(line);
                total = acked.Sum();
                if (total >= target) break;
            }
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit();
            for (string? line; (line = proc.StandardOutput.ReadLine()) is not null;) Track(line);
            Assert.True(acked.Sum() >= target, $"writers did not reach {target} (stderr: {proc.StandardError.ReadToEnd()})");

            using var db = FolioDatabase.Open(tmp.Path);
            var events = db.GetCollection("events");
            for (int t = 0; t < Threads; t++)
            {
                long lo = t * 1_000_000_000L;
                long present = events.Count($"{{ _id: {{ $gte: {lo}, $lt: {lo + 1_000_000_000L} }} }}");
                // Every acknowledged id (0..acked) is durable; at most one more per thread was committed unacknowledged.
                Assert.InRange(present, acked[t] + 1, acked[t] + 2);
                for (long k = 0; k <= acked[t]; k += Math.Max(1, acked[t] / 50))
                    Assert.NotNull(events.FindById(lo + k));
                Assert.NotNull(events.FindById(lo + acked[t]));
                acked[t] = present - 1;
            }
            db.CheckIntegrity();
        }
    }
}
