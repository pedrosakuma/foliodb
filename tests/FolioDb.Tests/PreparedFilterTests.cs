namespace FolioDb.Tests;

public class PreparedFilterTests
{
    private static void Seed(Collection c)
    {
        c.InsertMany(Enumerable.Range(0, 30).Select(i => new Document
        {
            ["_id"] = i, ["group"] = i % 3, ["n"] = (long)i,
            ["name"] = "item-" + i, ["nested"] = new Document { ["n"] = i },
            ["tags"] = new DocArray { "a", "b" }, ["nums"] = new DocArray { i, i + 1 },
        }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{_id:7}")]
    [InlineData("{group:1}")]
    [InlineData("{group:{$gte:1},n:{$lt:12}}")]
    [InlineData("{tags:{$in:['a','b']}}")]
    [InlineData("{nums:{$elemMatch:{$gte:3,$lt:5}}}")]
    [InlineData("{name:{$regex:'^item-[12]$'}}")]
    [InlineData("{'nested.n':{$in:[2,9]}}")]
    [InlineData("{$or:[{n:3},{n:8}]}")]
    [InlineData("{group:100}")]
    public void Prepared_matches_existing_APIs_before_and_after_index_changes(string json)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        var source = Document.Parse(json);
        var fromDocument = PreparedFilter.FromDocument(source);
        var fromJson = PreparedFilter.Parse(json);
        var options = new FindOptions { Sort = new Document { ["n"] = -1 }, Skip = 1, Limit = 5,
            Projection = new Document { ["n"] = 1 } };
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var filter in new[] { fromDocument, fromJson })
            {
                Assert.Equal(c.Find(source, options).Select(d => d.ToJson()), c.Find(filter, options).Select(d => d.ToJson()));
                Assert.Equal(c.FindOne(source, options)?.ToJson(), c.FindOne(filter, options)?.ToJson());
                Assert.Equal(c.Count(source), c.Count(filter));
                Assert.Equal(c.Explain(source), c.Explain(filter));
                var ids = new List<int>();
                long visited = c.Visit(filter, d =>
                {
                    d.TryGetValue("_id", out var id);
                    ids.Add(id.AsInt32);
                    return true;
                });
                Assert.Equal(c.Count(source), visited);
                Assert.Equal(c.Find(source).Select(d => d["_id"].AsInt32), ids);
            }
            if (pass == 0)
            {
                c.CreateIndex("group");
                c.CreateIndex("tags");
                c.CreateIndex(new Document { ["n"] = 1, ["group"] = -1 });
            }
        }
    }

    [Fact]
    public void Prepared_filter_owns_nested_array_and_binary_constants()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert(new Document { ["_id"] = 1, ["x"] = new Document { ["b"] = new byte[] { 1, 2 } } });
        c.Insert("{_id:2,x:4}");
        var bytes = new byte[] { 1, 2 };
        var nested = new Document { ["b"] = bytes };
        var values = new DocArray { nested, 4 };
        var source = new Document { ["x"] = new Document { ["$in"] = values } };
        var prepared = PreparedFilter.FromDocument(source);
        bytes[0] = 99;
        nested["b"] = "changed";
        values[1] = 999;
        source["x"] = 999;
        Assert.Equal(new[] { 1, 2 }, c.Find(prepared).Select(d => d["_id"].AsInt32));
        c.CreateIndex("x");
        Assert.Equal(2, c.Count(prepared));
    }

    [Fact]
    public void Plan_tracks_current_snapshot_and_transaction_metadata()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        var prepared = PreparedFilter.Parse("{group:1}");
        using (var snapshot = db.BeginSnapshot())
        {
            var sc = snapshot.GetCollection("items");
            Assert.StartsWith("COLLSCAN", sc.Explain(prepared));
            c.CreateIndex("group");
            Assert.StartsWith("IXSCAN", c.Explain(prepared));
            Assert.StartsWith("COLLSCAN", sc.Explain(prepared));
            c.DeleteMany("{group:1}");
            Assert.Equal(10, sc.Count(prepared));
            Assert.Equal(0, c.Count(prepared));
        }
        using (var tx = db.BeginTransaction())
        {
            var tc = tx.GetCollection("items");
            tc.Insert("{_id:100,group:1}");
            Assert.Equal(1, tc.Count(prepared));
            tc.DropIndex("group");
            Assert.StartsWith("COLLSCAN", tc.Explain(prepared));
        }
        Assert.StartsWith("IXSCAN", c.Explain(prepared));
        Assert.Equal(0, c.Count(prepared));
    }

    [Fact]
    public async Task Prepared_filter_can_be_shared_by_independent_concurrent_reads()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        Seed(c);
        var prepared = PreparedFilter.Parse("{group:{$in:[1,2]},name:{$regex:'^item-'},nums:{$elemMatch:{$gte:1}}}");
        int expected = c.Find(prepared).Count;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(expected, c.Count(prepared));
                Assert.Equal(expected, c.Visit(prepared, static _ => true));
            }
        }, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void Typed_materialized_and_borrowed_APIs_accept_the_same_filter()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var orders = db.GetCollection<ReadmeOrder>("orders");
        orders.Insert(new ReadmeOrder { Customer = "ana", Total = 10 });
        var filter = PreparedFilter.Parse("{customer:'ana'}");
        Assert.Single(orders.Find(filter));
        Assert.Equal(10, orders.FindOne(filter)!.Total);
        Assert.Equal(1, orders.Count(filter));
        Assert.Equal(1, orders.Visit(filter, static _ => false));
    }

    [Fact]
    public void Validation_occurs_at_preparation_and_null_prepared_arguments_are_rejected()
    {
        Assert.Throws<FolioException>(() => PreparedFilter.Parse("{$unknown:1}"));
        Assert.Throws<FolioException>(() => PreparedFilter.FromDocument(Document.Parse("{a:{$in:4}}")));
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert("{_id:1}");
        Assert.Equal(1, c.Count(PreparedFilter.Parse(null)));
        Assert.Equal(1, c.Count(PreparedFilter.FromDocument(null)));
        Assert.Throws<ArgumentNullException>(() => c.Find((PreparedFilter)null!));
        Assert.Throws<ArgumentNullException>(() => c.FindOne((PreparedFilter)null!));
        Assert.Throws<ArgumentNullException>(() => c.Count((PreparedFilter)null!));
        Assert.Throws<ArgumentNullException>(() => c.Explain((PreparedFilter)null!));
        Assert.Throws<ArgumentNullException>(() => c.Visit((PreparedFilter)null!, static _ => true));
        Assert.Throws<ArgumentNullException>(() => c.Visit(PreparedFilter.Parse("{}"), null!));
        Assert.Equal(0, db.GetCollection("missing").Count(PreparedFilter.Parse("{}")));
    }
}
