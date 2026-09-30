namespace FolioDb.Tests;

public class VisitTests
{
    private static void Seed(Collection c)
    {
        c.InsertMany(Enumerable.Range(0, 30).Select(i => new Document
        {
            ["_id"] = i, ["group"] = i % 3, ["n"] = (long)i,
            ["tags"] = new DocArray { "a", "b" },
            ["payload"] = new string('x', i % 2 == 0 ? 1024 : 8192),
            ["bytes"] = new byte[] { 1, 2 },
        }));
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{group:1}")]
    [InlineData(false, "{group:1}")]
    [InlineData(true, "{group:{$gte:1},n:{$lt:12}}")]
    [InlineData(true, "{tags:{$in:['a','b']}}")]
    [InlineData(true, "{_id:{$in:[2,3,7]}}")]
    [InlineData(true, "{_id:7}")]
    [InlineData(true, "{group:100}")]
    public void Visit_matches_Find_without_changing_materialized_API(bool indexed, string filter)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        if (indexed)
        {
            c.CreateIndex("group");
            c.CreateIndex("tags");
        }
        var expected = c.Find(filter);
        var actual = new List<Document>();
        long count = c.Visit(filter, d => { actual.Add(d.ToDocument()); return true; });
        Assert.Equal(expected.Count, count);
        Assert.Equal(expected.Select(d => d.ToJson()), actual.Select(d => d.ToJson()));
        if (actual.Count > 0)
        {
            actual[0]["bytes"].AsBinary[0] = 99;
            Assert.Equal(1, c.FindById(actual[0]["_id"])!["bytes"].AsBinary[0]);
        }
    }

    [Fact]
    public void Stops_on_false_and_reports_invoked_count()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        int calls = 0;
        Assert.Equal(3, c.Visit(new Document(), _ => ++calls < 3));
        Assert.Equal(3, calls);
        Assert.Equal(1, c.Visit("{}", _ => false));
        Assert.Equal(0, db.GetCollection("missing").Visit("{}", _ => throw new Exception("Should not run")));
        Assert.Throws<ArgumentNullException>(() => c.Visit("{}", null!));
        Assert.Throws<ArgumentNullException>(() => c.Visit(new Document(), null!));
    }

    [Fact]
    public void Transaction_guards_mutation_and_supports_nested_reads()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("items");
        Seed(c);
        int visits = 0;
        Assert.Equal(30, c.Visit("{}", doc =>
        {
            Assert.Throws<InvalidOperationException>(() => c.DeleteMany());
            Assert.Throws<InvalidOperationException>(() => c.Insert("{_id:100}"));
            Assert.Throws<InvalidOperationException>(() => tx.GetCollection("other").Insert("{_id:1}"));
            Assert.Throws<InvalidOperationException>(() => tx.DropCollection("items"));
            Assert.Throws<InvalidOperationException>(() => c.CreateIndex("n"));
            Assert.Throws<InvalidOperationException>(tx.Commit);
            Assert.Throws<InvalidOperationException>(tx.Rollback);
            Assert.Throws<InvalidOperationException>(tx.Dispose);
            Assert.True(doc.TryGetValue("_id", out var id));
            Assert.Equal(visits++, id.AsInt32);
            Assert.Equal(1, c.Visit("{_id:0}", _ => false));
            Assert.True(c.TryReadById(0, static d => d.FieldCount, out _));
            Assert.Equal(30, c.Count());
            return true;
        }));
        c.Insert("{_id:100}");
        tx.Commit();
        Assert.Equal(31, db.GetCollection("items").Count());
        Assert.Throws<InvalidOperationException>(() => c.Visit("{}", _ => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_has_one_snapshot_despite_writes_between_callbacks(bool explicitSnapshot)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        using var snapshot = explicitSnapshot ? db.BeginSnapshot() : null;
        var reader = snapshot?.GetCollection("items") ?? c;
        int calls = 0;
        Assert.Equal(30, reader.Visit("{}", d =>
        {
            if (snapshot is not null) Assert.Throws<InvalidOperationException>(snapshot.Dispose);
            Assert.False(db.Checkpoint());
            if (calls++ == 0)
            {
                c.UpdateMany("{}", "{$set:{n:1000}}");
                c.Insert("{_id:100,n:1000}");
            }
            Assert.True(d.TryGetValue("n", out var n));
            Assert.True(n.AsInt64 < 30);
            return true;
        }));
        Assert.Equal(31, c.Count("{n:1000}"));
        if (snapshot is not null)
        {
            Assert.Equal(30, reader.Visit("{}", _ => true));
            snapshot.Dispose();
            Assert.Throws<InvalidOperationException>(() => reader.Visit("{}", _ => true));
        }
    }

    [Fact]
    public void Disposed_snapshot_rejects_even_a_cached_missing_collection()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var snapshot = db.BeginSnapshot();
        var c = snapshot.GetCollection("missing");
        Assert.Equal(0, c.Visit("{}", _ => true));
        snapshot.Dispose();
        Assert.Throws<InvalidOperationException>(() => c.Visit("{}", _ => true));
    }

    [Fact]
    public void Compound_index_and_typed_wrapper_preserve_results()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var orders = db.GetCollection<ReadmeOrder>("orders");
        orders.Insert(new ReadmeOrder { Customer = "ana", Total = 10 });
        orders.Insert(new ReadmeOrder { Customer = "ana", Total = 20 });
        orders.Untyped.CreateIndex(new Document { ["customer"] = 1, ["total_cents"] = -1 });
        var filter = Document.Parse("{customer:'ana',total_cents:{$gte:10}}");
        long total = 0;
        Assert.Equal(2, orders.Visit(filter, d =>
        {
            Assert.True(d.TryGetValue("total_cents", out var n));
            total += n.AsInt64;
            return true;
        }));
        Assert.Equal(orders.Find(filter).Sum(o => o.Total), total);
        Assert.Equal(1, orders.Visit("{}", _ => false));
    }

    [Fact]
    public void Callback_exceptions_release_guards_without_dooming_transaction()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        var failure = new FormatException("visitor failed");
        Assert.Same(failure, Assert.Throws<FormatException>(() => c.Visit("{}", _ => throw failure)));
        Assert.True(db.Checkpoint());
        using (var snapshot = db.BeginSnapshot())
        {
            Assert.Same(failure, Assert.Throws<FormatException>(() => snapshot.GetCollection("items").Visit("{}", _ => throw failure)));
        }
        using var tx = db.BeginTransaction();
        var tc = tx.GetCollection("items");
        Assert.Same(failure, Assert.Throws<FormatException>(() => tc.Visit("{}", _ => throw failure)));
        tc.Insert("{_id:100}");
        tx.Commit();
    }
}
