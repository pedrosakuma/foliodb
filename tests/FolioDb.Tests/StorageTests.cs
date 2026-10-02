using FolioDb.Storage;

namespace FolioDb.Tests;

public class StorageTests
{
    private static byte[] Key(int i) => KeyEncoder.Encode(i);

    [Theory]
    [InlineData(1024, false)]
    [InlineData(1024, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public void BTree_reads_inline_and_overflow_boundaries(int pageSize, bool checkpoint)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = pageSize, AutoCheckpointFrames = 0 });
        int[] sizes = [0, 16, pageSize / 2, pageSize - 5, pageSize - 4, pageSize - 3, pageSize * 2 + 5];
        var values = sizes.Select(n => Enumerable.Range(0, n).Select(i => (byte)(i % 251)).ToArray()).ToArray();
        uint root;
        using (var tx = db.BeginWrite())
        {
            var tree = new BTree(tx.Storage, root = BTree.Create(tx.Storage));
            for (int i = 0; i < values.Length; i++) tree.Insert(Key(i), values[i], overwrite: false);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.True(tree.TryGet(Key(i), out var value));
                Assert.True(value.SequenceEqual(values[i]));
            }
            tx.Commit();
        }
        if (checkpoint) db.Checkpoint();
        using var read = db.BeginRead();
        var reader = new BTree(read.Storage, root);
        var cursor = reader.CreateCursor();
        for (int i = 0; i < values.Length; i++)
        {
            Assert.True(reader.TryGet(Key(i), out var value));
            Assert.True(value.SequenceEqual(values[i]));
            Assert.True(cursor.SeekExact(Key(i)));
            Assert.True(cursor.Value.SequenceEqual(values[i]));
        }
        Assert.False(reader.TryGet(Key(values.Length), out _));

        var key = Key(4); // Exactly one full overflow page.
        for (int i = 0; i < 10; i++) reader.TryGet(key, out _);
        long allocated = Allocations.Measure(() =>
        {
            for (int i = 0; i < 100; i++) reader.TryGet(key, out _);
        });
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(1024, 20_000, false)]
    [InlineData(1024, 20_000, true)]
    [InlineData(4096, 20_000, false)]
    [InlineData(4096, 20_000, true)]
    public void BTree_random_insert_delete_matches_reference_model(int pageSize, int ops, bool rebalance)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            PageSize = pageSize,
            Synchronous = SynchronousMode.Off,
            DeleteRebalance = rebalance ? BTreeDeleteRebalanceMode.LeafByteOccupancy : BTreeDeleteRebalanceMode.None,
        });
        var rnd = new Random(pageSize);
        var model = new SortedDictionary<int, byte[]>();
        uint root;
        using (var tx = db.BeginWrite())
        {
            root = BTree.Create(tx.Storage);
            tx.Commit();
        }

        for (int batch = 0; batch < 10; batch++)
        {
            using var tx = db.BeginWrite();
            var tree = new BTree(tx.Storage, root);
            for (int i = 0; i < ops / 10; i++)
            {
                int k = rnd.Next(5000);
                if (rnd.Next(3) == 0)
                {
                    Assert.Equal(model.Remove(k), tree.Delete(Key(k)));
                }
                else
                {
                    // Mix of tiny, medium and overflow-sized values.
                    var value = new byte[rnd.Next(4) == 0 ? rnd.Next(pageSize, pageSize * 3) : rnd.Next(0, 64)];
                    rnd.NextBytes(value);
                    tree.Insert(Key(k), value, overwrite: true);
                    model[k] = value;
                }
            }
            Assert.Equal(model.Count, tree.Verify());
            tx.Commit();
        }

        using (var tx = db.BeginRead())
        {
            var tree = new BTree(tx.Storage, root);
            var cur = tree.CreateCursor();
            var expected = model.GetEnumerator();
            for (bool ok = cur.SeekFirst(); ok; ok = cur.MoveNext())
            {
                Assert.True(expected.MoveNext());
                Assert.Equal(Key(expected.Current.Key), cur.Key.ToArray());
                Assert.Equal(expected.Current.Value, cur.Value.ToArray());
            }
            Assert.False(expected.MoveNext());

            Assert.True(cur.Seek(Key(2500)));
            Assert.Equal(Key(model.Keys.First(k => k >= 2500)), cur.Key.ToArray());

            // SeekExact reuses the cached path: ascending, descending, repeated and random probes must match the model.
            var seek = tree.CreateCursor();
            var probes = Enumerable.Range(-5, 5010).Concat(Enumerable.Range(0, 5000).Reverse())
                .Concat(Enumerable.Range(0, 5000).Select(_ => rnd.Next(-10, 5010))).Concat([7, 7, 7]);
            foreach (int k in probes)
            {
                bool found = seek.SeekExact(Key(k));
                Assert.Equal(model.TryGetValue(k, out var expectedValue), found);
                if (found)
                {
                    Assert.Equal(Key(k), seek.Key.ToArray());
                    Assert.Equal(expectedValue, seek.Value.ToArray());
                }
            }
        }

        // Deleting everything returns pages to the freelist, and they are reused.
        long pagesBefore;
        using (var tx = db.BeginWrite())
        {
            var tree = new BTree(tx.Storage, root);
            foreach (var k in model.Keys) Assert.True(tree.Delete(Key(k)));
            Assert.Equal(0, tree.Verify());
            pagesBefore = tx.Storage.PageCount;
            Assert.True(tx.Storage.FreePageCount > 0);
            tx.Commit();
        }
        using (var tx = db.BeginWrite())
        {
            var tree = new BTree(tx.Storage, root);
            for (int i = 0; i < 500; i++) tree.Insert(Key(i), new byte[100], overwrite: false);
            Assert.Equal(pagesBefore, tx.Storage.PageCount);
            tx.Commit();
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    public void Leaf_byte_rebalance_handles_variable_keys_at_every_page_size(int pageSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            PageSize = pageSize,
            Synchronous = SynchronousMode.Off,
            DeleteRebalance = BTreeDeleteRebalanceMode.LeafByteOccupancy,
        });
        var expected = new SortedDictionary<byte[], byte[]>(Comparer<byte[]>.Create(
            static (x, y) => x.AsSpan().SequenceCompareTo(y)));
        uint root;
        using (var tx = db.BeginWrite())
        {
            var tree = new BTree(tx.Storage, root = BTree.Create(tx.Storage));
            for (int i = 0; i < 600; i++)
            {
                var key = VariableKey(i, 1 + i * 37 % Math.Min(180, BTree.MaxKeySize(pageSize) - 4));
                var value = Enumerable.Repeat((byte)(i % 251), i * 29 % (pageSize + 97)).ToArray();
                tree.Insert(key, value, overwrite: false);
                expected.Add(key, value);
            }
            Assert.Equal(expected.Count, tree.Verify());
            Assert.Equal(root, tree.Diagnose().RootPage);
            tx.Commit();
        }

        using (var tx = db.BeginWrite())
        {
            var tree = new BTree(tx.Storage, root);
            foreach (var key in expected.Keys.Where((_, index) => index % 3 != 0).ToArray())
            {
                Assert.True(tree.Delete(key));
                expected.Remove(key);
            }
            Assert.Equal(expected.Count, tree.Verify());
            Assert.Equal(root, tree.Diagnose().RootPage);
            tx.Commit();
        }

        using var read = db.BeginRead();
        var reader = new BTree(read.Storage, root);
        var cursor = reader.CreateCursor();
        using var expectedItems = expected.GetEnumerator();
        for (bool ok = cursor.SeekFirst(); ok; ok = cursor.MoveNext())
        {
            Assert.True(expectedItems.MoveNext());
            Assert.Equal(expectedItems.Current.Key, cursor.Key.ToArray());
            Assert.Equal(expectedItems.Current.Value, cursor.Value.ToArray());
        }
        Assert.False(expectedItems.MoveNext());
        Assert.Equal(expected.Count, reader.Verify());
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Ascending_inserts_fill_leaves_while_other_patterns_stay_correct(int pageSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = pageSize, Synchronous = SynchronousMode.Off });
        uint ascending, descending;
        var rnd = new Random(pageSize);
        var model = new SortedDictionary<int, byte[]>();
        using (var tx = db.BeginWrite())
        {
            var asc = new BTree(tx.Storage, ascending = BTree.Create(tx.Storage));
            var desc = new BTree(tx.Storage, descending = BTree.Create(tx.Storage));
            for (int i = 0; i < 5000; i++)
            {
                var value = new byte[rnd.Next(10, 60)];
                rnd.NextBytes(value);
                Assert.True(asc.Insert(Key(i), value, overwrite: false));
                Assert.True(desc.Insert(Key(5000 - i), value, overwrite: false));
                model[i] = value;
            }
            Assert.Equal(5000, asc.Verify());
            Assert.Equal(5000, desc.Verify());

            var stats = asc.Diagnose();
            Assert.True(stats.LeafFreeBytes < stats.LeafPages * (long)pageSize / 5,
                $"ascending leaves are {stats.LeafFreeBytes * 100 / (stats.LeafPages * pageSize)}% free");

            // Interleave appends with inserts below the maximum key: packed leaves then split normally.
            for (int i = 0; i < 4000; i++)
            {
                int k = i % 2 == 0 ? 5000 + i : rnd.Next(5000);
                var value = new byte[rnd.Next(10, 60)];
                rnd.NextBytes(value);
                asc.Insert(Key(k), value, overwrite: true);
                model[k] = value;
            }
            Assert.Equal(model.Count, asc.Verify());
            tx.Commit();
        }

        using var read = db.BeginRead();
        var cursor = new BTree(read.Storage, ascending).CreateCursor();
        using var expected = model.GetEnumerator();
        for (bool ok = cursor.SeekFirst(); ok; ok = cursor.MoveNext())
        {
            Assert.True(expected.MoveNext());
            Assert.Equal(Key(expected.Current.Key), cursor.Key.ToArray());
            Assert.Equal(expected.Current.Value, cursor.Value.ToArray());
        }
        Assert.False(expected.MoveNext());
        Assert.Equal(5000, new BTree(read.Storage, descending).Verify());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overwrites_and_deleting_everything_leave_only_the_root(bool rebalance)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            DeleteRebalance = rebalance ? BTreeDeleteRebalanceMode.LeafByteOccupancy : BTreeDeleteRebalanceMode.None,
        });
        using var tx = db.BeginWrite();
        var tree = new BTree(tx.Storage, BTree.Create(tx.Storage));
        uint used = tx.Storage.PageCount - tx.Storage.FreePageCount;

        for (int i = 0; i < 3000; i++) tree.Insert(Key(i), new byte[60], overwrite: false);
        tree.Insert(Key(7), new byte[3 * tx.Storage.PageSize], overwrite: true);
        tree.Insert(Key(7), [1, 2, 3], overwrite: true);
        Assert.True(tree.TryGet(Key(7), out var v));
        Assert.Equal([1, 2, 3], v.ToArray());

        var order = Enumerable.Range(5, 2995).OrderBy(i => (i * 7919) % 3000).ToList();
        foreach (int i in order) Assert.True(tree.Delete(Key(i)));
        // Keys 0-4 share one leaf: once every other leaf is gone the root must have collapsed into it.
        var shape = tree.Diagnose();
        Assert.Equal(5, shape.EntryCount);
        Assert.Equal(0, shape.InteriorPages);
        for (int i = 0; i < 5; i++) Assert.True(tree.Delete(Key(i)));
        Assert.Equal(0, tree.Verify());
        Assert.Equal(used, tx.Storage.PageCount - tx.Storage.FreePageCount);
    }

    [Fact]
    public void Insert_without_overwrite_reports_existing_key()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        using var tx = db.BeginWrite();
        var tree = new BTree(tx.Storage, BTree.Create(tx.Storage));
        Assert.True(tree.Insert(Key(1), [1], overwrite: false));
        Assert.False(tree.Insert(Key(1), [2], overwrite: false));
        Assert.True(tree.TryGet(Key(1), out var v));
        Assert.Equal(1, v[0]);
    }

    [Fact]
    public void Committed_data_survives_crash_via_wal_recovery()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Full });
        var col = db.GetCollection("c");
        for (int i = 0; i < 300; i++) col.Insert(new Document { ["_id"] = i, ["v"] = new string('x', i) });
        Assert.True(db.GetStats().WalFrames > 0);
        db.SimulateCrash();

        using var reopened = tmp.Open();
        var c2 = reopened.GetCollection("c");
        Assert.Equal(300, c2.Count());
        Assert.Equal(new string('x', 299), c2.FindById(299)!["v"].AsString);
        Assert.Equal(0, reopened.GetStats().WalFrames); // recovered and checkpointed on open
        reopened.CheckIntegrity();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(24)]
    [InlineData(4000)]
    [InlineData(4120)]
    [InlineData(9000)]
    public void Torn_wal_write_is_discarded_on_recovery(int tornBytes)
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var col = db.GetCollection("c");
        col.CreateIndex("k");
        for (int i = 0; i < 50; i++) col.Insert(new Document { ["_id"] = i, ["k"] = i % 7 });

        db.Pager.TestTornWriteBytes = tornBytes;
        Assert.Throws<IOException>(() => col.InsertMany(Enumerable.Range(1000, 200).Select(i => new Document { ["_id"] = i, ["k"] = i })));
        db.SimulateCrash();

        using var reopened = tmp.Open();
        Assert.Equal(50, reopened.GetCollection("c").Count());
        Assert.Equal(7, reopened.GetCollection("c").Count("{ k: 3 }"));
        reopened.CheckIntegrity();
    }

    [Fact]
    public void Corrupted_wal_frame_truncates_recovery_at_last_valid_commit()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var col = db.GetCollection("c");
        col.Insert(new Document { ["_id"] = 1 });
        long walAfterFirst = new FileInfo(tmp.Path + "-wal").Length;
        col.Insert(new Document { ["_id"] = 2 });
        db.SimulateCrash();

        // Flip one byte inside the second transaction's frames.
        using (var fs = new FileStream(tmp.Path + "-wal", FileMode.Open))
        {
            fs.Position = walAfterFirst + 100;
            int b = fs.ReadByte();
            fs.Position = walAfterFirst + 100;
            fs.WriteByte((byte)~b);
        }

        using var reopened = tmp.Open();
        var c = reopened.GetCollection("c");
        Assert.NotNull(c.FindById(1));
        Assert.Null(c.FindById(2));
    }

    [Fact]
    public void Torn_rebalancing_delete_commit_is_discarded_on_recovery()
    {
        using var tmp = new TempDb();
        var options = new FolioOptions
        {
            PageSize = 1024,
            AutoCheckpointFrames = 0,
            DeleteRebalance = BTreeDeleteRebalanceMode.LeafByteOccupancy,
        };
        var db = tmp.Open(options);
        var col = db.GetCollection("c");
        col.CreateIndex(Document.Parse("{ group: 1, score: -1 }"));
        col.CreateIndex("tags");
        col.InsertMany(Enumerable.Range(0, 500).Select(i => new Document
        {
            ["_id"] = i,
            ["group"] = i % 17,
            ["score"] = i,
            ["tags"] = new DocArray { $"a-{i % 11}", $"b-{i % 13}" },
            ["payload"] = new string('x', 20 + i % 180),
        }));
        Assert.True(db.Checkpoint());

        db.Pager.TestTornWriteBytes = 4000;
        Assert.Throws<IOException>(() => col.DeleteMany("{ score: { $lt: 350 } }"));
        db.SimulateCrash();

        using var reopened = tmp.Open(options);
        Assert.Equal(500, reopened.GetCollection("c").Count());
        reopened.CheckIntegrity();
    }

    [Fact]
    public void Checkpoint_moves_wal_into_main_file_and_resets_it()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var col = db.GetCollection("c");
        for (int i = 0; i < 100; i++) col.Insert(new Document { ["i"] = i });
        Assert.True(db.GetStats().WalFrames > 0);
        Assert.True(db.Checkpoint());
        var stats = db.GetStats();
        Assert.Equal(0, stats.WalFrames);
        Assert.True(stats.DatabaseFileBytes >= stats.PageCount * stats.PageSize);
        Assert.Equal(100, col.Count());
    }

    [Fact]
    public void Checkpoint_is_deferred_while_a_reader_is_active()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var col = db.GetCollection("c");
        col.Insert(new Document { ["i"] = 1 });
        using (var snap = db.BeginSnapshot())
        {
            Assert.False(db.Checkpoint());
            Assert.Equal(1, snap.GetCollection("c").Count());
        }
        Assert.True(db.Checkpoint());
    }

    [Fact]
    public void Auto_checkpoint_bounds_wal_size()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 50 });
        var col = db.GetCollection("c");
        for (int i = 0; i < 500; i++) col.Insert(new Document { ["i"] = i });
        Assert.True(db.GetStats().WalFrames < 60);
        Assert.Equal(500, col.Count());
    }

    [Fact]
    public void Database_file_is_exclusively_locked()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Assert.Throws<FolioException>(() => FolioDatabase.Open(tmp.Path));
    }

    [Fact]
    public void Invalid_files_are_rejected()
    {
        using var tmp = new TempDb();
        File.WriteAllBytes(tmp.Path, new byte[8192]);
        Assert.Throws<CorruptDatabaseException>(() => FolioDatabase.Open(tmp.Path));
    }

    [Fact]
    public void Page_size_is_persisted()
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open(new FolioOptions { PageSize = 16384 }))
            db.GetCollection("c").Insert(new Document { ["a"] = 1 });
        using (var db = tmp.Open(new FolioOptions { PageSize = 1024 }))
        {
            Assert.Equal(16384, db.GetStats().PageSize);
            Assert.Equal(1, db.GetCollection("c").Count());
        }
    }

    private static byte[] VariableKey(int value, int suffixLength)
    {
        var key = new byte[4 + suffixLength];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, value);
        key.AsSpan(4).Fill((byte)(value % 251));
        return key;
    }
}
