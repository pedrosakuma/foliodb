using System.Text;

namespace FolioDb.Tests;

public class BorrowedReadTests
{
    [Fact]
    public void Warm_snapshot_point_reads_do_not_allocate_keys()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        DocValue[] ids = [42, long.MaxValue, 0.5, 12.345m, ObjectId.NewObjectId(), "a\0b", true,
            new byte[] { 1, 0, 2 }, DateTime.UnixEpoch, new Document { ["key"] = 1 }];
        var c = db.GetCollection("items");
        foreach (var id in ids) c.Insert(new Document { ["_id"] = id, ["n"] = 7L });
        using var snapshot = db.BeginSnapshot();
        var reader = snapshot.GetCollection("items");
        Func<DocumentView, long> callback = static d => d.TryGetValue("n", out var n) ? n.AsInt64 : -1;
        for (int i = 0; i < 100; i++)
            foreach (var id in ids) Assert.True(reader.TryReadById(id, callback, out _));
        long sum = 0;
        long allocated = Allocations.Measure(() =>
        {
            sum = 0;
            for (int i = 0; i < 100; i++)
                foreach (var id in ids)
                {
                    reader.TryReadById(id, callback, out long n);
                    sum += n;
                }
        });
        Assert.Equal(7000, sum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Temporary_key_is_consumed_before_nested_callbacks_reuse_the_encoder()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(new Document { ["_id"] = 42, ["n"] = 7 });
        using var snapshot = db.BeginSnapshot();
        var reader = snapshot.GetCollection("items");
        foreach (var id in new DocValue[] { 42, 42L, 42.0, 42m })
        {
            Assert.Equal(7, reader.FindById(id)!["n"].AsInt32);
            Assert.True(reader.TryReadById(id, d =>
            {
                // Forces the shared encoding buffer to grow while the outer document is still borrowed.
                Assert.False(reader.TryReadById(new string('z', 4096), static inner => inner.FieldCount, out _));
                Assert.True(reader.TryReadById(42, static inner => inner.FieldCount, out _));
                return d.TryGetValue("n", out var n) ? n.AsInt32 : -1;
            }, out int n));
            Assert.Equal(7, n);
        }
    }

    private static readonly DateTime When = new(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);
    private static readonly ObjectId Oid = ObjectId.NewObjectId();

    private static Document Sample(int payloadSize = 16) => new()
    {
        ["_id"] = 1,
        ["i32"] = 42,
        ["i64"] = 5_000_000_000L,
        ["dbl"] = 2.5,
        ["dec"] = 12.345m,
        ["flag"] = true,
        ["when"] = When,
        ["oid"] = Oid,
        ["nil"] = DocValue.Null,
        ["name"] = "ação 🚀",
        ["bin"] = new byte[] { 0, 1, 2, 255 },
        ["payload"] = new string('x', payloadSize),
        ["nested"] = new Document { ["city"] = "Lisboa", ["bytes"] = new byte[] { 9, 8 }, ["deep"] = new Document { ["n"] = 7 } },
        ["tags"] = new DocArray { "a", 2, new Document { ["k"] = "v" }, new byte[] { 3 } },
    };

    [Fact]
    public void Missing_id_or_collection_returns_false_without_invoking_callback()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        int calls = 0;
        Assert.False(db.GetCollection("none").TryReadById(1, _ => ++calls, out int r));
        Assert.Equal(0, r);
        var c = db.GetCollection("items");
        c.Insert(Sample());
        Assert.False(c.TryReadById(2, _ => ++calls, out _));
        Assert.False(c.TryReadById("1", _ => ++calls, out _)); // ids are typed: "1" != 1
        Assert.Equal(0, calls);
        Assert.True(c.TryReadById(1L, d => d.FieldCount, out int fields)); // numeric ids share one encoding
        Assert.Equal(Sample().Count, fields);
    }

    [Fact]
    public void Scalar_accessors_match_materialized_values()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(Sample());
        var doc = c.FindById(1)!;

        Assert.True(c.TryReadById(1, d =>
        {
            Assert.True(d.TryGetValue("i32", out var i32));
            Assert.Equal(DocType.Int32, i32.Type);
            Assert.Equal(42, i32.AsInt32);
            Assert.Equal(42L, i32.AsInt64);
            Assert.Equal(42.0, i32.AsDouble);
            Assert.Equal(42m, i32.AsDecimal);
            Assert.True(i32.IsNumber);

            Assert.True(d.TryGetValue("i64", out var i64));
            Assert.Equal(5_000_000_000L, i64.AsInt64);
            bool overflow = false;
            try { _ = i64.AsInt32; } catch (OverflowException) { overflow = true; }
            Assert.True(overflow);

            Assert.True(d.TryGetValue("dbl", out var dbl));
            Assert.Equal(2.5, dbl.AsDouble);
            Assert.Equal(2, dbl.AsInt32);
            Assert.True(d.TryGetValue("dec", out var dec));
            Assert.Equal(12.345m, dec.AsDecimal);
            Assert.True(d.TryGetValue("flag", out var flag));
            Assert.True(flag.AsBoolean);
            Assert.True(d.TryGetValue("when", out var when));
            Assert.Equal(When, when.AsDateTime);
            Assert.Equal(doc["when"].AsUnixMilliseconds, when.AsUnixMilliseconds);
            Assert.True(d.TryGetValue("oid", out var oid));
            Assert.Equal(Oid, oid.AsObjectId);
            Assert.True(d.TryGetValue("nil", out var nil));
            Assert.True(nil.IsNull);
            Assert.Equal(DocValue.Null, nil.ToDocValue());

            foreach (var f in d)
                Assert.Equal(doc[f.GetName()], f.Value.ToDocValue());
            return true;
        }, out _));
    }

    [Fact]
    public void Missing_fields_and_type_mismatches_are_explicit()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(Sample());
        Assert.True(c.TryReadById(1, d =>
        {
            Assert.False(d.TryGetValue("missing", out var missing));
            Assert.True(missing.IsNull);
            Assert.False(d.ContainsField("nested.city")); // no dotted paths
            Assert.False(d.TryGetValue(new string('a', 300), out _)); // longer than any storable name
            Assert.False(d.TryGetValue("I32", out _)); // ordinal, case-sensitive
            Assert.True(d.ContainsField("i32"));
            Assert.True(d.TryGetValue("i32"u8, out _));

            d.TryGetValue("name", out var name);
            d.TryGetValue("i32", out var number);
            Assert.False(number.StringEquals("42"));
            Assert.False(number.StringEquals("42"u8));
            int casts = 0;
            try { _ = name.AsInt32; } catch (InvalidCastException) { casts++; }
            try { _ = number.AsUtf8String; } catch (InvalidCastException) { casts++; }
            try { _ = number.AsBinary; } catch (InvalidCastException) { casts++; }
            try { _ = number.AsDocument; } catch (InvalidCastException) { casts++; }
            try { _ = number.AsArray; } catch (InvalidCastException) { casts++; }
            try { _ = number.GetString(); } catch (InvalidCastException) { casts++; }
            try { _ = name.AsBoolean; } catch (InvalidCastException) { casts++; }
            try { _ = missing.AsInt64; } catch (InvalidCastException) { casts++; }
            return casts;
        }, out int casts));
        Assert.Equal(8, casts);
    }

    [Fact]
    public void Strings_binaries_and_nested_views_are_borrowed_and_comparable()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        var sample = Sample();
        sample["bad"] = "x\uD800y"; // lone surrogate is stored as U+FFFD
        c.Insert(sample);
        var stored = c.FindById(1)!;

        Assert.True(c.TryReadById(1, d =>
        {
            d.TryGetValue("name", out var name);
            Assert.True(name.AsUtf8String.SequenceEqual(Encoding.UTF8.GetBytes("ação 🚀")));
            Assert.True(name.StringEquals("ação 🚀"));
            Assert.True(name.StringEquals("ação 🚀"u8));
            Assert.False(name.StringEquals("acao 🚀"));
            Assert.False(name.StringEquals("ação 🚀!"));
            Assert.False(name.StringEquals("ação"));
            Assert.False(name.StringEquals(""));
            Assert.Equal("ação 🚀", name.GetString());

            d.TryGetValue("bad", out var bad);
            Assert.Equal(stored["bad"].AsString == "x\uD800y", bad.StringEquals("x\uD800y"));
            Assert.Equal(stored["bad"].AsString == "x\uFFFDy", bad.StringEquals("x\uFFFDy"));

            d.TryGetValue("bin", out var bin);
            Assert.True(bin.AsBinary.SequenceEqual(new byte[] { 0, 1, 2, 255 }));

            d.TryGetValue("nested", out var nestedValue);
            var nested = nestedValue.AsDocument;
            Assert.True(nested.TryGetValue("city", out var city) && city.StringEquals("Lisboa"));
            Assert.True(nested.TryGetValue("bytes", out var bytes) && bytes.AsBinary.SequenceEqual(new byte[] { 9, 8 }));
            Assert.True(nested.TryGetValue("deep", out var deep) && deep.AsDocument.TryGetValue("n", out var n) && n.AsInt32 == 7);
            Assert.Equal(stored["nested"].AsDocument.ToJson(), nested.ToDocument().ToJson());

            d.TryGetValue("tags", out var tagsValue);
            var tags = tagsValue.AsArray;
            Assert.Equal(4, tags.Count);
            var types = new List<DocType>();
            foreach (var v in tags) types.Add(v.Type);
            Assert.Equal([DocType.String, DocType.Int32, DocType.Document, DocType.Binary], types);
            Assert.Equal(stored["tags"].AsArray.Count, tags.ToArray().Count);

            var names = new List<string>();
            foreach (var f in d)
            {
                names.Add(f.GetName());
                Assert.True(f.NameEquals(f.GetName()));
                Assert.True(f.Utf8Name.SequenceEqual(Encoding.UTF8.GetBytes(f.GetName())));
            }
            Assert.Equal(stored.Keys, names);
            Assert.Equal(stored.ToJson(), d.ToDocument().ToJson());
            return 0;
        }, out _));
    }

    [Fact]
    public void Stateful_overload_accepts_ref_struct_state_and_static_lambda()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(Sample());
        ReadOnlySpan<char> expected = "Lisboa";
        Assert.True(c.TryReadById(1, expected, static (d, text) =>
            d.TryGetValue("nested", out var n) && n.AsDocument.TryGetValue("city", out var city) && city.StringEquals(text), out bool match));
        Assert.True(match);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void Materialization_is_an_owned_copy(int payloadSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(Sample(payloadSize));
        Assert.True(c.TryReadById(1, static d => d.ToDocument(), out var copy));
        Assert.True(c.TryReadById(1, static d => d.TryGetValue("bin", out var v) ? v.ToDocValue() : default, out var bin));
        copy["nested"].AsDocument["bytes"].AsBinary[0] = 99;
        copy["tags"].AsArray[3].AsBinary[0] = 99;
        bin.AsBinary[0] = 99;
        Assert.Equal(Sample(payloadSize).ToJson(), c.FindById(1)!.ToJson());
        Assert.True(c.TryReadById(1, static d => d.TryGetValue("payload", out var p) ? p.AsUtf8String.Length : -1, out int len));
        Assert.Equal(payloadSize, len);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void Transaction_views_see_uncommitted_data_and_block_mutation_until_callback_returns(int payloadSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { Synchronous = SynchronousMode.Off, BusyTimeout = TimeSpan.FromMilliseconds(50) });
        var outside = db.GetCollection("items");
        outside.Insert(Sample(payloadSize));
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("items");
        c.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = new string('y', payloadSize) } });

        var errors = new List<string>();
        Assert.True(c.TryReadById(1, d =>
        {
            d.TryGetValue("payload", out var before);
            void Expect<TException>(string what, Action action) where TException : Exception
            {
                try { action(); errors.Add(what + ": no exception"); }
                catch (TException) { }
            }
            Expect<InvalidOperationException>("update", () => c.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = "z" } }));
            Expect<InvalidOperationException>("insert", () => c.Insert(new Document { ["_id"] = 2 }));
            Expect<InvalidOperationException>("delete", () => c.DeleteById(1));
            Expect<InvalidOperationException>("index", () => c.CreateIndex("i32"));
            Expect<InvalidOperationException>("drop", () => tx.DropCollection("items"));
            Expect<InvalidOperationException>("other collection", () => tx.GetCollection("other").Insert(new Document()));
            Expect<InvalidOperationException>("commit", tx.Commit);
            Expect<InvalidOperationException>("rollback", tx.Rollback);
            Expect<InvalidOperationException>("dispose", tx.Dispose);
            // Another writer or a checkpoint cannot get in while this transaction holds the write lock.
            Expect<FolioException>("auto-commit write", () => outside.Insert(new Document { ["_id"] = 3 }));
            if (db.Checkpoint()) errors.Add("checkpoint succeeded");

            // Reads, including nested borrowed reads, remain allowed.
            if (c.FindById(1)!["payload"].AsString != new string('y', payloadSize)) errors.Add("nested FindById");
            if (!c.TryReadById(1, static inner => inner.FieldCount, out int _)) errors.Add("nested TryReadById");
            if (c.Count() != 1) errors.Add("count");
            if (!d.TryGetValue("payload", out var after) || !after.AsUtf8String.SequenceEqual(before.AsUtf8String)
                || !after.StringEquals(new string('y', payloadSize)))
                errors.Add("view changed");
            return 0;
        }, out _));
        Assert.Empty(errors);

        // Blocked calls neither doomed nor completed the transaction.
        c.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = "done" } });
        Assert.True(c.TryReadById(1, static d => d.TryGetValue("payload", out var p) && p.StringEquals("done"), out bool done) && done);
        tx.Commit();
        Assert.Equal("done", outside.FindById(1)!["payload"].AsString);
        Assert.Throws<InvalidOperationException>(() => c.TryReadById(1, static d => 0, out _));
    }

    [Fact]
    public void Callback_exceptions_propagate_without_dooming_or_leaking_scopes()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { Synchronous = SynchronousMode.Off, AutoCheckpointFrames = 0 });
        var c = db.GetCollection("items");
        c.Insert(Sample());

        // Implicit scope: the read transaction is released, so a checkpoint can run afterwards.
        var ex = Assert.Throws<FormatException>(() => c.TryReadById<int>(1, static _ => throw new FormatException("boom"), out _));
        Assert.Equal("boom", ex.Message);
        Assert.True(db.Checkpoint());

        using (var tx = db.BeginTransaction())
        {
            var tc = tx.GetCollection("items");
            Assert.Throws<FormatException>(() => tc.TryReadById<int>(1, static _ => throw new FormatException(), out _));
            tc.Insert(new Document { ["_id"] = 2 }); // not doomed, not borrowed any more
            tx.Commit();
        }
        Assert.Equal(2, c.Count());

        using (var snap = db.BeginSnapshot())
        {
            var sc = snap.GetCollection("items");
            Assert.Throws<FormatException>(() => sc.TryReadById<int>(1, static _ => throw new FormatException(), out _));
            Assert.True(sc.TryReadById(2, static d => d.FieldCount, out int n) && n == 1);
        }
        Assert.True(db.Checkpoint());
    }

    [Fact]
    public void Doomed_or_completed_transactions_reject_borrowed_reads()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.GetCollection("items").Insert(Sample());
        using (var tx = db.BeginTransaction())
        {
            var c = tx.GetCollection("items");
            Assert.ThrowsAny<Exception>(() => c.InsertMany([new Document { ["_id"] = 5 }, new Document { ["_id"] = 1 }]));
            Assert.Throws<FolioException>(() => c.TryReadById(1, static d => 0, out _));
        }
        using (var tx = db.BeginTransaction())
        {
            var c = tx.GetCollection("items");
            tx.Rollback();
            Assert.Throws<InvalidOperationException>(() => c.TryReadById(1, static d => 0, out _));
        }
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void Snapshot_views_are_isolated_and_snapshot_cannot_be_disposed_during_callback(int payloadSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { Synchronous = SynchronousMode.Off, AutoCheckpointFrames = 0 });
        var c = db.GetCollection("items");
        c.Insert(Sample(payloadSize));
        db.Checkpoint();

        var snap = db.BeginSnapshot();
        var sc = snap.GetCollection("items");
        var errors = new List<string>();
        Assert.True(sc.TryReadById(1, d =>
        {
            d.TryGetValue("payload", out var before);
            // Concurrent-style writes commit new page versions; the snapshot's page images are never modified.
            c.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = new string('q', payloadSize) } });
            c.DeleteById(1);
            c.Insert(new Document { ["_id"] = 1, ["payload"] = "replaced" });
            if (db.Checkpoint()) errors.Add("checkpoint ran with an active reader");
            try { snap.Dispose(); errors.Add("dispose allowed"); }
            catch (InvalidOperationException) { }
            if (!sc.TryReadById(1, static inner => inner.TryGetValue("payload", out var p) && p.AsUtf8String.Length > 8, out bool nested) || !nested)
                errors.Add("snapshot read changed");
            if (!before.StringEquals(new string('x', payloadSize))) errors.Add("view changed");
            return 0;
        }, out _));
        Assert.Empty(errors);
        snap.Dispose();
        Assert.Throws<InvalidOperationException>(() => sc.TryReadById(1, static d => 0, out _));
        Assert.True(c.TryReadById(1, static d => d.TryGetValue("payload", out var p) && p.StringEquals("replaced"), out bool replaced) && replaced);
        Assert.True(db.Checkpoint());
        db.CheckIntegrity();
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    public void Implicit_scope_view_survives_writes_checkpoint_attempts_and_database_disposal(int payloadSize)
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { Synchronous = SynchronousMode.Off, AutoCheckpointFrames = 0 });
        var c = db.GetCollection("items");
        c.Insert(Sample(payloadSize));
        db.Checkpoint();
        var errors = new List<string>();
        Assert.True(c.TryReadById(1, d =>
        {
            d.TryGetValue("payload", out var payload);
            c.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = new string('q', payloadSize) } });
            if (db.Checkpoint()) errors.Add("checkpoint ran with an active reader");
            db.Dispose();
            if (!payload.StringEquals(new string('x', payloadSize))) errors.Add("view changed");
            return 0;
        }, out _));
        Assert.Empty(errors);
        Assert.Throws<ObjectDisposedException>(() => c.TryReadById(1, static d => 0, out _));
        using var reopened = tmp.Open();
        Assert.Equal(new string('q', payloadSize), reopened.GetCollection("items").FindById(1)!["payload"].AsString);
    }

    [Fact]
    public void Typed_collections_expose_borrowed_reads()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var orders = db.GetCollection<ReadmeOrder>("orders");
        var id = orders.Insert(new ReadmeOrder { Customer = "ana", Total = 1250 });
        Assert.True(orders.TryReadById(id, static d => d.TryGetValue("total_cents", out var t) ? t.AsInt64 : -1, out long total));
        Assert.Equal(1250, total);
        Assert.False(orders.TryReadById(ObjectId.NewObjectId(), static d => 0L, out _));
    }
}
