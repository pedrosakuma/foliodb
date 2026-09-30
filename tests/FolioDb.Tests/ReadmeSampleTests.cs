namespace FolioDb.Tests;

public enum ReadmeOrderStatus { Pending, Shipped }

[FolioDocument]
public partial record ReadmeOrder
{
    public ObjectId Id { get; init; }
    public required string Customer { get; init; }
    [FolioField("total_cents")] public long Total { get; set; }
    public List<ReadmeOrderLine> Lines { get; set; } = [];
    public ReadmeOrderStatus Status { get; set; }
    [FolioIgnore] public string? Cache { get; set; }
}

[FolioDocument]
public partial record ReadmeOrderLine(string Sku, int Qty);

/// <summary>Keeps the README code samples honest.</summary>
public class ReadmeSampleTests
{
    [Fact]
    public void QuickStart()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var users = db.GetCollection("users");

        users.CreateIndex("email", unique: true);
        users.Insert("""{ "name": "Ana", "email": "ana@example.com", "age": 31, "tags": ["admin"] }""");
        var adults = users.Find("""{ "age": { "$gte": 18 } }""",
            new FindOptions { Sort = Document.Parse("""{ "age": -1 }"""), Limit = 10 });
        Assert.Single(adults);

        users.UpdateOne("""{ "email": "ana@example.com" }""", """{ "$inc": { "age": 1 }, "$push": { "tags": "owner" } }""");
        Assert.StartsWith("IXSCAN email_1", users.Explain("""{ "email": "ana@example.com" }""").ToString());
        var ana = users.FindOne("{}")!;
        Assert.Equal(32, ana["age"].AsInt64);
        Assert.Equal(2, ana["tags"].AsArray.Count);
    }

    [Fact]
    public void TransactionsAndSnapshots()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.GetCollection("accounts").InsertMany([Document.Parse("{ _id: 1, balance: 100 }"), Document.Parse("{ _id: 2, balance: 0 }")]);

        using (var tx = db.BeginTransaction())
        {
            var accounts = tx.GetCollection("accounts");
            accounts.UpdateOne("""{ "_id": 1 }""", """{ "$inc": { "balance": -50 } }""");
            accounts.UpdateOne("""{ "_id": 2 }""", """{ "$inc": { "balance": 50 } }""");
            tx.Commit();
        }

        using var snap = db.BeginSnapshot();
        Assert.Equal(2, snap.GetCollection("accounts").Count());
        Assert.Equal(50, snap.GetCollection("accounts").FindById(2)!["balance"].AsInt64);
    }

    [Fact]
    public void BorrowedPointReads()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var people = db.GetCollection("people");
        people.Insert("""{ "_id": 1, "name": "Ana", "age": 31, "address": { "city": "Lisboa" } }""");

        bool found = people.TryReadById(1, static d =>
            d.TryGetValue("name", out var name) && name.StringEquals("Ana") && d.TryGetValue("age", out var age) ? age.AsInt32 : -1,
            out int anaAge);

        people.TryReadById(1, "Lisboa".AsSpan(), static (d, city) =>
            d.TryGetValue("address", out var a) && a.AsDocument.TryGetValue("city", out var c) && c.StringEquals(city),
            out bool inLisboa);

        Assert.True(found);
        Assert.Equal(31, anaAge);
        Assert.True(inLisboa);
        Assert.False(people.TryReadById(2, static d => 0, out _));
    }

    [Fact]
    public void MaterializedAndBorrowedQueries()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var people = db.GetCollection("people");
        people.InsertMany([Document.Parse("{_id:1,age:31}"), Document.Parse("{_id:2,age:12}")]);
        var adults = people.Find("{age:{$gte:18}}");
        long ageSum = 0;
        long visited = people.Visit("{age:{$gte:18}}", doc =>
        {
            if (!doc.TryGetValue("age", out var age)) throw new InvalidOperationException("Missing age.");
            ageSum += age.AsInt64;
            return true;
        });
        Assert.Equal(adults.Count, visited);
        Assert.Equal(adults.Sum(d => d["age"].AsInt64), ageSum);
    }

    [Fact]
    public void TypedDocuments()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var orders = db.GetCollection<ReadmeOrder>("orders");
        orders.Insert(new ReadmeOrder { Customer = "ana", Lines = [new("A1", 2)], Total = 990, Cache = "x" });
        ReadmeOrder? o = orders.FindOne("""{ "customer": "ana" }""");

        Assert.NotNull(o);
        Assert.NotEqual(default, o.Id);
        Assert.Equal(990, o.Total);
        Assert.Null(o.Cache);
        Assert.Equal(new ReadmeOrderLine("A1", 2), Assert.Single(o.Lines));
        Assert.Equal(1, db.GetCollection("orders").Count("""{ "total_cents": 990 }"""));
    }
}
