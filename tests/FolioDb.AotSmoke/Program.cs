using System.Diagnostics;
using FolioDb;

// Native AOT smoke test: exercises the whole stack (storage, WAL, indexes, queries, generated mappers)
// without reflection. Exit code 0 = success.
//
//   FolioDb.AotSmoke                     run the end-to-end scenario
//   FolioDb.AotSmoke crash-writer <path> insert forever, printing "committed N" after each durable commit

if (args is ["crash-writer", var crashPath])
{
    using var db = FolioDatabase.Open(crashPath, new FolioOptions { AutoCheckpointFrames = 200 });
    var col = db.GetCollection("events");
    long start = col.Count();
    for (long n = start; ; n++)
    {
        col.Insert(new Document { ["_id"] = n, ["payload"] = new string('x', (int)(n % 300)) });
        Console.WriteLine($"committed {n + 1}");
    }
}

var path = Path.Combine(Path.GetTempPath(), $"folio-smoke-{Environment.ProcessId}.folio");
try
{
    Run(path);
    Console.WriteLine("AOT smoke test passed.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"AOT smoke test FAILED: {e}");
    return 1;
}
finally
{
    File.Delete(path);
    File.Delete(path + "-wal");
}

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Check failed: " + what);
}

static void Run(string path)
{
    var sw = Stopwatch.StartNew();
    using (var db = FolioDatabase.Open(path, new FolioOptions { WriterAdmission = WriterAdmissionMode.Fifo }))
    {
        // Untyped API with JSON filters.
        var users = db.GetCollection("users");
        var concurrent = db.GetCollection("fifo");
        concurrent.Insert("{_id:1,n:0}");
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 20; i++) concurrent.UpdateOne("{_id:1}", "{$inc:{n:1}}");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray());
        Check(concurrent.FindById(1)!["n"].AsInt32 == 80, "FIFO concurrent updates");
        concurrent.Drop();
        var overflow = db.GetCollection("overflow");
        overflow.Insert(new Document { ["_id"] = 1, ["payload"] = new string('x', 1024) });
        Check(overflow.FindById(1)!["payload"].AsString == new string('x', 1024), "single-page overflow read");
        // Borrowed point reads: no Document/string materialization, scope guarded during the callback.
        overflow.Insert(new Document { ["_id"] = 2, ["n"] = 7L, ["name"] = "ação", ["nested"] = new Document { ["b"] = new byte[] { 1, 2 } } });
        Check(overflow.TryReadById(2, static d =>
            d.TryGetValue("n", out var n) && d.TryGetValue("name", out var name) && d.TryGetValue("nested", out var nested)
                ? n.AsInt64 + (name.StringEquals("ação") ? 1 : 0) + nested.AsDocument.FieldCount
                : -1, out long borrowed) && borrowed == 9, "borrowed scalar read");
        Check(overflow.TryReadById(1, "xxx".AsSpan(), static (d, prefix) =>
            d.TryGetValue("payload", out var p) && p.AsUtf8String.Length == 1024 && p.GetString().StartsWith(prefix), out bool prefixed) && prefixed, "borrowed overflow read");
        Check(!overflow.TryReadById(3, static d => 0, out _), "borrowed missing id");
        using (var tx = db.BeginTransaction())
        {
            var tc = tx.GetCollection("overflow");
            bool blocked = false;
            tc.TryReadById(2, d =>
            {
                try { tc.DeleteById(2); }
                catch (InvalidOperationException) { blocked = true; }
                return 0;
            }, out _);
            Check(blocked && tc.DeleteById(2), "borrowed transaction guard");
            tx.Commit();
        }
        overflow.Drop();
        users.CreateIndex("email", unique: true);
        users.CreateIndex("age");
        for (int i = 0; i < 2000; i++)
            users.Insert(new Document { ["email"] = $"u{i}@x.io", ["age"] = i % 90, ["tags"] = new DocArray { "a", i % 2 == 0 ? "even" : "odd" } });

        Check(users.Count() == 2000, "count");
        long ageSum = 0;
        long visited = users.Visit("{age:{$lt:10}}", d =>
        {
            if (!d.TryGetValue("age", out var age)) throw new Exception("Missing age.");
            ageSum += age.AsInt32;
            return true;
        });
        Check(visited == users.Count("{age:{$lt:10}}") &&
            ageSum == users.Find("{age:{$lt:10}}").Sum(d => d["age"].AsInt32), "borrowed indexed query");
        var youngFilter = PreparedFilter.Parse("{age:{$lt:10}}");
        Check(users.Count(youngFilter) == visited && users.Find(youngFilter).Count == visited &&
            users.Visit(youngFilter, static _ => true) == visited, "prepared filter reuse");
        Check(users.Count("{ age: { $gte: 30, $lt: 40 } }") == 2000 / 90 * 10 + Math.Min(2000 % 90, 40) - Math.Min(2000 % 90, 30), "range count");
        Check(users.Explain("{ email: 'u5@x.io' }").Contains("email"), "index plan");
        Check(users.FindOne("{ email: 'u5@x.io' }")?["age"].AsInt32 == 5, "findOne");
        Check(users.Find("{ tags: 'even' }", new FindOptions { Sort = Document.Parse("{ age: -1 }"), Limit = 3 })[0]["age"].AsInt32 == 88, "sort");

        var upd = users.UpdateMany("{ age: { $lt: 10 } }", "{ $inc: { age: 100 }, $set: { young: true } }");
        Check(upd.ModifiedCount == users.Count("{ young: true }"), "updateMany");
        try
        {
            users.Insert("{ email: 'u1@x.io' }");
            Check(false, "unique violation expected");
        }
        catch (DuplicateKeyException) { }

        using (var tx = db.BeginTransaction())
        {
            tx.GetCollection("users").DeleteMany("{ age: { $gte: 100 } }");
            tx.Rollback();
        }
        Check(users.Count("{ age: { $gte: 100 } }") > 0, "rollback");

        // Typed API via the source generator.
        var orders = db.GetCollection<Order>("orders");
        orders.CreateIndex("customer");
        var id = orders.Insert(new Order
        {
            Customer = "ana",
            Total = 12.5m,
            Status = OrderStatus.Paid,
            CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Lines = [new OrderLine("pen", 2), new OrderLine("ink", 1)],
            Notes = null,
        });
        var back = orders.FindById(id)!;
        Check(back.Customer == "ana" && back.Total == 12.5m && back.Status == OrderStatus.Paid, "typed scalars");
        Check(back.Lines.Count == 2 && back.Lines[1].Sku == "ink" && back.Lines[0].Qty == 2, "typed nested list");
        Check(back.CreatedAt == new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), "typed date");
        Check(back.Id == id.AsObjectId, "typed id");
        Check(orders.Find("{ 'lines.sku': 'pen' }").Count == 1, "typed query");
        users.CreateIndex(Document.Parse("{age:1,email:-1}"));
        Check(users.Count("{age:30,email:{$gte:'u'}}") == users.Find("{age:30}").Count, "compound count");
        Check(users.Find("{age:30}", new FindOptions { Projection = Document.Parse("{age:1,email:1}") }).Count > 0, "compound projection");
        Check(users.FindOne("{email:'u5@x.io'}", new FindOptions { Projection = Document.Parse("{tags:{$slice:1}}") })!["tags"].AsArray.Count == 1, "slice projection");
        var groups = users.Aggregate("[{$match:{age:30}},{$group:{_id:'$age',n:{$count:{}},avg:{$avg:'$age'}}}]");
        Check(groups.Count == 1 && groups[0]["n"].AsInt64 == users.Count("{age:30}") && groups[0]["avg"].AsDouble == 30, "aggregation");
        Check(orders.Aggregate("[{$unwind:'$lines'},{$group:{_id:'$lines.sku',n:{$sum:'$lines.qty'}}}]").Count == 2, "typed aggregation");
        db.CheckIntegrity();
    }

    // Reopen: durability through checkpoint on close.
    using (var db = FolioDatabase.Open(path))
    {
        Check(db.GetCollection("users").Count() == 2000, "reopen count");
        Check(db.GetCollection("users").GetIndexes().Any(i => i.Keys.Count == 2), "reopen compound");
        Check(db.GetCollection<Order>("orders").FindOne("{ customer: 'ana' }")?.Lines.Count == 2, "reopen typed");
        db.CheckIntegrity();
    }
    Console.WriteLine($"Scenario completed in {sw.ElapsedMilliseconds} ms.");
}

[FolioDocument]
public partial class Order
{
    public ObjectId Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public string? Notes { get; set; }
    public int LineCount => Lines.Count;
}

[FolioDocument]
public partial record OrderLine(string Sku, int Qty);

public enum OrderStatus { Pending, Paid, Shipped }
