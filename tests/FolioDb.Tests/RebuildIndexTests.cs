using FolioDb.Engine;
using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class RebuildIndexTests
{
    [Fact]
    public void Sort_run_is_private_from_creation_and_removed_on_close()
    {
        string path;
        using (var run = IndexSort.CreateRun())
        {
            path = run.Name;
            run.WriteByte(42);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(run.SafeFileHandle);
                Assert.Equal((UnixFileMode)0, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            }
        }
        Assert.False(File.Exists(path));
    }

    private static string[] Bytes(IEnumerable<Document> docs) =>
        docs.Select(d => Convert.ToHexString(DocumentSerializer.Serialize(d))).Order().ToArray();

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void Rebuild_preserves_queries_hints_and_snapshot(int pageSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = pageSize, AutoCheckpointFrames = 0 });
        var indexed = db.GetCollection("indexed");
        var plain = db.GetCollection("plain");
        var pattern = Document.Parse("{a:1,b:-1}");
        indexed.CreateIndex("a");
        indexed.CreateIndex(pattern);
        indexed.CreateIndex("tags");
        var docs = Enumerable.Range(0, 350).Select(i => new Document
        {
            ["_id"] = i,
            ["a"] = i % 4 == 0 ? (DocValue)(decimal)(i % 13) : i % 4 == 1 ? (long)(i % 13) : (double)(i % 13),
            ["b"] = i % 3 == 0 ? (DocValue)new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i % 12) : new byte[] { (byte)(i % 12) },
            ["tags"] = new DocArray { i % 7, (long)(i % 7), i % 11 },
        }).ToList();
        docs.Add(Document.Parse("{_id:999,b:null}"));
        indexed.InsertMany(docs.Select(d => d.Clone()));
        plain.InsertMany(docs);
        for (int i = 0; i < 200; i++)
            indexed.DeleteById(i);
        for (int i = 0; i < 200; i++)
            plain.DeleteById(i);
        using var snapshot = db.BeginSnapshot();
        long oldMatches = snapshot.GetCollection("indexed").Count("{a:5}");
        var oldRoot = db.GetStorageDiagnostics().Trees.Single(t => t.Index == "a_1_b_-1").Tree.RootPage;
        using (var tx = db.BeginTransaction())
        {
            var c = tx.GetCollection("indexed");
            Assert.True(c.RebuildIndex(pattern));
            Assert.True(c.RebuildIndex("a"));
            Assert.True(c.RebuildIndex("tags"));
            Assert.Throws<InvalidOperationException>(() => snapshot.GetCollection("indexed").RebuildIndex("a"));
            Assert.Equal(151, snapshot.GetCollection("indexed").Count());
            tx.Commit();
        }
        Assert.NotEqual(oldRoot, db.GetStorageDiagnostics().Trees.Single(t => t.Index == "a_1_b_-1").Tree.RootPage);
        Assert.Equal(151, snapshot.GetCollection("indexed").Count());
        Assert.Equal(oldMatches, snapshot.GetCollection("indexed").Count("{a:5}"));
        indexed.Insert("{_id:5000,a:5,b:null,tags:[3]}");
        Assert.Equal(oldMatches, snapshot.GetCollection("indexed").Count("{a:5}"));
        Assert.Equal(oldMatches + 1, indexed.Count("{a:5}"));
        Assert.Null(snapshot.GetCollection("indexed").FindById(5000));
        indexed.DeleteById(5000);
        Assert.False(db.Checkpoint());
        foreach (var query in new[] { "{a:5}", "{a:{$gte:4,$lt:8}}", "{a:5,b:{$gte:null}}", "{tags:3}", "{a:null}" })
        {
            Assert.Equal(plain.Count(query), indexed.Count(query));
            Assert.Equal(Bytes(plain.Find(query)), Bytes(indexed.Find(query)));
            Assert.Equal(Bytes(plain.Find(query, new FindOptions { Projection = Document.Parse("{a:1,b:1}") })),
                Bytes(indexed.Find(query, new FindOptions { Projection = Document.Parse("{a:1,b:1}") })));
        }
        db.CheckIntegrity();
        snapshot.Dispose();
        Assert.True(db.Checkpoint());
        Assert.True(indexed.RebuildIndex(pattern));
        db.CheckIntegrity();
    }

    [Fact]
    public void Empty_missing_and_rollback_are_explicit()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        Assert.False(c.RebuildIndex("missing"));
        Assert.False(c.RebuildIndex("_id_"));
        Assert.False(c.RebuildIndex(Document.Parse("{_id:1}")));
        c.CreateIndex("x");
        Assert.True(c.RebuildIndex("x"));
        Assert.False(c.RebuildIndex(Document.Parse("{x:-1}")));
        c.Insert("{_id:1,x:2}");
        uint publishedRoot = db.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage;
        using (var tx = db.BeginTransaction())
        {
            var scoped = tx.GetCollection("c");
            scoped.TryReadById(1, _ =>
            {
                Assert.Throws<InvalidOperationException>(() => scoped.RebuildIndex("x"));
                return 0;
            }, out _);
            Assert.True(scoped.RebuildIndex("x_1"));
            Assert.NotEqual(publishedRoot, tx.Engine.GetCollection("c")!.Indexes[0].Root);
            Assert.Equal(publishedRoot, db.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage);
            Assert.Equal(1, scoped.Count("{x:2}"));
        }
        Assert.Equal(publishedRoot, db.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage);
        Assert.Equal(1, c.Count("{x:2}"));
        c.Insert("{_id:2,x:3}");
        var disposed = db.BeginTransaction();
        var disposedCollection = disposed.GetCollection("c");
        disposed.Dispose();
        Assert.Throws<InvalidOperationException>(() => disposedCollection.RebuildIndex("x"));
        db.CheckIntegrity();
    }

    [Fact]
    public void Unique_multikey_rebuild_detects_duplicates_and_dooms_transaction()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        var pattern = Document.Parse("{a:1,b:-1}");
        c.CreateIndex(pattern, unique: true);
        c.Insert("{_id:1,a:[1,1,2],b:[3,4]}");
        c.Insert("{_id:2,a:1,b:5}");
        Assert.True(c.RebuildIndex(pattern));
        Assert.True(c.GetIndexes().Single(i => i.Name == "a_1_b_-1").Unique);
        Assert.Throws<DuplicateKeyException>(() => c.Insert("{_id:3,a:1,b:3}"));
        // Corrupt the logical uniqueness invariant via a test-only primary write, then verify rebuild never publishes.
        using (var tx = db.BeginTransaction())
        {
            var meta = tx.Engine.GetCollection("c")!;
            var primary = new BTree(tx.Engine.Storage, meta.PrimaryRoot);
            primary.Insert(KeyEncoder.Encode(4), DocumentSerializer.Serialize(Document.Parse("{_id:4,a:1,b:3}")), overwrite: false);
            var oldRoot = meta.Indexes[0].Root;
            Assert.Throws<DuplicateKeyException>(() => tx.GetCollection("c").RebuildIndex(pattern));
            Assert.Throws<FolioException>(() => tx.Commit());
            Assert.Equal(oldRoot, meta.Indexes[0].Root);
        }
        Assert.Equal(2, c.Count());
        db.CheckIntegrity();
    }

    [Theory]
    [InlineData(40)]
    [InlineData(4000)]
    public void Torn_rebuild_commit_keeps_old_catalog(int tornBytes)
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { PageSize = 1024, AutoCheckpointFrames = 0 });
        var c = db.GetCollection("c");
        c.CreateIndex("x");
        for (int i = 0; i < 120; i++) c.Insert(new Document { ["_id"] = i, ["x"] = i % 11 });
        Assert.True(db.Checkpoint());
        var oldRoot = db.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage;
        db.Pager.TestTornWriteBytes = tornBytes;
        Assert.ThrowsAny<Exception>(() => c.RebuildIndex("x"));
        db.SimulateCrash();
        using var recovered = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        Assert.Equal(oldRoot, recovered.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage);
        Assert.Equal(11, recovered.GetCollection("c").Count("{x:0}"));
        recovered.CheckIntegrity();
    }

    [Fact]
    public void Bulk_builder_handles_multiple_levels_overflow_and_subsequent_writes()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = 1024, AutoCheckpointFrames = 0 });
        uint root;
        using (var tx = db.BeginWrite())
        {
            var builder = new BTree.BulkBuilder(tx.Storage);
            for (int i = 0; i < 3000; i++)
                builder.Add(KeyEncoder.Encode(i), new byte[i % 101 == 0 ? 800 : i % 80]);
            root = builder.Finish();
            var tree = new BTree(tx.Storage, root);
            Assert.Equal(3000, tree.Verify());
            Assert.True(tree.Diagnose().Height >= 3);
            tree.Delete(KeyEncoder.Encode(100));
            tree.Insert(KeyEncoder.Encode(3000), [1, 2, 3], overwrite: false);
            Assert.Equal(3000, tree.Verify());
            tx.Commit();
        }
        using var read = db.BeginRead();
        Assert.Equal(3000, new BTree(read.Storage, root).Verify());
    }

    [Fact]
    public void External_sort_spills_merges_and_removes_temporary_runs()
    {
        using var sorted = new IndexSort();
        for (int i = 0; i < 65000; i++)
        {
            var key = new byte[140];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, 65000 - i);
            sorted.Add(key, 4, [1]);
        }
        Assert.True(IndexSort.LastMetrics.Runs > 0);
        int expected = 1;
        foreach (var entry in sorted.Sorted())
            Assert.Equal(expected++, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(entry.Key));
        Assert.Equal(65001, expected);
        Assert.True(IndexSort.LastMetrics.ScratchBytes > 0);
    }

    [Fact]
    public void Committed_rebuild_recovers_and_reuses_freed_pages()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { PageSize = 1024, AutoCheckpointFrames = 0 });
        var c = db.GetCollection("c");
        c.CreateIndex("x");
        for (int i = 0; i < 350; i++) c.Insert(new Document { ["_id"] = i, ["x"] = i % 31 });
        Assert.True(c.RebuildIndex("x"));
        uint rebuiltRoot = db.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage;
        long free = db.GetStats().FreePages;
        Assert.True(free > 0);
        db.SimulateCrash();
        using var recovered = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        Assert.Equal(rebuiltRoot, recovered.GetStorageDiagnostics().Trees.Single(t => t.Index == "x_1").Tree.RootPage);
        Assert.True(recovered.GetStats().FreePages > 0);
        recovered.GetCollection("c").Insert("{_id:999,x:7}");
        Assert.Equal(13, recovered.GetCollection("c").Count("{x:7}"));
        recovered.CheckIntegrity();
    }
}
