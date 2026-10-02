using System.Security.Cryptography;

namespace FolioDb.Tests;

#pragma warning disable CA1416, xUnit1051

public sealed class VacuumTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void Vacuum_copies_snapshot_types_indexes_and_empty_collections(int pageSize)
    {
        using var source = new TempDb();
        string output = source.Path + ".vacuum";
        try
        {
            using var db = source.Open(new FolioOptions { PageSize = pageSize, AutoCheckpointFrames = 0 });
            var docs = db.GetCollection("docs");
            var empty = db.GetCollection("empty");
            empty.CreateIndex(Document.Parse("{a:1,b:-1}"), unique: true);
            docs.CreateIndex("email", unique: true);
            docs.CreateIndex(Document.Parse("{n:1,email:-1}"));
            docs.CreateIndex("tags");
            docs.Insert(new Document
            {
                ["_id"] = ObjectId.NewObjectId(),
                ["email"] = "one@example.test",
                ["n"] = 0.1m,
                ["tags"] = new DocArray { "a", "b", "a" },
                ["binary"] = new byte[] { 0, 255 },
                ["nested"] = new Document { ["when"] = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
                ["overflow"] = new string('x', pageSize * 3),
            });
            docs.Insert("{_id:2,email:'two@example.test',n:NumberDecimal('10.00'),tags:['b','c']}");
            for (int i = 3; i < 180; i++)
                docs.Insert(new Document { ["_id"] = i, ["email"] = $"u{i}@example.test", ["n"] = (long)(i % 9),
                    ["tags"] = new DocArray { i % 4, i % 7 }, ["payload"] = new string('p', pageSize / 2) });
            for (int i = 3; i < 120; i++) docs.DeleteById(i);
            db.Checkpoint();

            var expectedDocs = Bytes(docs.Find()).Order().ToArray();
            var expectedIndexes = docs.GetIndexes().OrderBy(i => i.Name).Select(IndexShape).ToArray();
            var expectedEmptyIndexes = empty.GetIndexes().OrderBy(i => i.Name).Select(IndexShape).ToArray();
            db.VacuumInto(output);

            using var compact = FolioDatabase.Open(output, new FolioOptions { AutoCheckpointFrames = 0 });
            compact.CheckIntegrity();
            Assert.Equal(pageSize, compact.GetStats().PageSize);
            Assert.Equal(expectedDocs, Bytes(compact.GetCollection("docs").Find()).Order().ToArray());
            Assert.Equal(expectedIndexes, compact.GetCollection("docs").GetIndexes().OrderBy(i => i.Name).Select(IndexShape));
            Assert.Empty(compact.GetCollection("empty").Find());
            Assert.Equal(expectedEmptyIndexes, compact.GetCollection("empty").GetIndexes().OrderBy(i => i.Name).Select(IndexShape));
            Assert.Equal(docs.Count("{tags:'b'}"), compact.GetCollection("docs").Count("{tags:'b'}"));
            Assert.Equal(docs.Count("{n:{$gte:0.1,$lt:11}}"), compact.GetCollection("docs").Count("{n:{$gte:0.1,$lt:11}}"));
            Assert.True(compact.GetStats().FreePages < db.GetStats().FreePages);
        }
        finally
        {
            File.Delete(output);
            File.Delete(output + "-wal");
            File.Delete(output + "-wal2");
        }
    }

    [Fact]
    public void Vacuum_uses_start_snapshot_and_allows_source_writes()
    {
        using var source = new TempDb();
        string output = source.Path + ".vacuum";
        try
        {
            using var db = source.Open(new FolioOptions { AutoCheckpointFrames = 0 });
            var c = db.GetCollection("c");
            c.Insert("{_id:1,value:'before'}");
            db.TestVacuumStage = stage =>
            {
                if (stage == VacuumStage.SnapshotStarted) c.Insert("{_id:2,value:'after'}");
            };
            db.VacuumInto(output);
            db.TestVacuumStage = null;

            using var compact = FolioDatabase.Open(output);
            Assert.Equal(2, c.Count());
            Assert.Equal(1, compact.GetCollection("c").Count());
            Assert.NotNull(compact.GetCollection("c").FindById(1));
            Assert.Null(compact.GetCollection("c").FindById(2));
        }
        finally
        {
            File.Delete(output);
            File.Delete(output + "-wal");
            File.Delete(output + "-wal2");
        }
    }

    [Fact]
    public void Vacuum_failure_cancellation_and_destination_collisions_leave_source_and_foreign_file_untouched()
    {
        using var source = new TempDb();
        string output = source.Path + ".vacuum";
        var db = source.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 1, ["payload"] = new string('x', 5000) });
        db.Checkpoint();
        db.Dispose();
        byte[] mainBefore = SHA256.HashData(File.ReadAllBytes(source.Path));
        byte[] walBefore = SHA256.HashData(File.ReadAllBytes(source.Path + "-wal"));
        db = source.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        c = db.GetCollection("c");

        using (var canceled = new CancellationTokenSource())
        {
            db.TestVacuumStage = stage =>
            {
                if (stage == VacuumStage.CopyCompleted) canceled.Cancel();
            };
            Assert.Throws<OperationCanceledException>(() => db.VacuumInto(output, canceled.Token));
            db.TestVacuumStage = null;
        }
        Assert.False(File.Exists(output));

        byte[] foreign = [7, 8, 9];
        File.WriteAllBytes(output, foreign);
        Assert.Throws<IOException>(() => db.VacuumInto(output));
        Assert.Equal(foreign, File.ReadAllBytes(output));
        Assert.Throws<ArgumentException>(() => db.VacuumInto(source.Path));
        Assert.Throws<ArgumentException>(() => db.VacuumInto(source.Path + "-wal"));
        Assert.Equal(1, c.Count());
        File.Delete(output);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(output, Path.Combine(Path.GetDirectoryName(output)!, "missing-target"));
            Assert.Throws<IOException>(() => db.VacuumInto(output));
            Assert.NotNull(new FileInfo(output).LinkTarget);
            File.Delete(output);
        }
        db.Dispose();
        Assert.Equal(mainBefore, SHA256.HashData(File.ReadAllBytes(source.Path)));
        Assert.Equal(walBefore, SHA256.HashData(File.ReadAllBytes(source.Path + "-wal")));
    }

    [Fact]
    public void Vacuum_staging_files_are_private_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var source = new TempDb();
        using var db = source.Open();
        db.GetCollection("c").Insert("{_id:1}");
        db.TestVacuumStage = stage =>
        {
            if (stage != VacuumStage.SnapshotStarted) return;
            string dir = Path.GetDirectoryName(source.Path)!;
            string stagePath = Directory.EnumerateFiles(dir, ".*.vacuum-*.tmp").Single();
            var mode = File.GetUnixFileMode(stagePath);
            Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            throw new OperationCanceledException();
        };
        Assert.Throws<OperationCanceledException>(() => db.VacuumInto(source.Path + ".vacuum"));
        db.TestVacuumStage = null;
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(source.Path)!, ".*.vacuum-*.tmp"));
    }

    private static string[] Bytes(IEnumerable<Document> docs) =>
        docs.Select(d => Convert.ToHexString(DocumentSerializer.Serialize(d))).ToArray();

    private static string IndexShape(IndexInfo index) =>
        $"{index.Name}|{index.Unique}|{index.MultiKey}|{DocJson.WriteValue(index.Keys)}";
}
