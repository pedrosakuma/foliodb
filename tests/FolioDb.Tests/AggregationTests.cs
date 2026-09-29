namespace FolioDb.Tests;

public sealed class AggregationTests : IDisposable
{
    private readonly TempDb _tmp = new();
    private readonly FolioDatabase _db;
    private readonly Collection _c;

    public AggregationTests()
    {
        _db = _tmp.Open();
        _c = _db.GetCollection("sales");
        _c.InsertMany(new[]
        {
            Document.Parse("{_id:1,city:'SP',amount:10,items:[{sku:'a',n:2},{sku:'b',n:1}]}"),
            Document.Parse("{_id:2,city:'SP',amount:20,items:[{sku:'a',n:3}]}"),
            Document.Parse("{_id:3,city:'RJ',amount:NumberDecimal('5.5'),items:[]}"),
            Document.Parse("{_id:4,city:'RJ',amount:'not numeric'}"),
            Document.Parse("{_id:5,city:'RJ',amount:null,items:null}"),
        });
        _c.CreateIndex(Document.Parse("{city:1,amount:-1}"));
    }

    public void Dispose() { _db.Dispose(); _tmp.Dispose(); }
    private string[] Run(string pipeline) => _c.Aggregate(pipeline).Select(d => d.ToJson()).ToArray();

    [Fact]
    public void AllAccumulatorsAndNumericTypes()
    {
        var rows = _c.Aggregate("""
            [
              {$sort:{_id:1}},
              {$group:{_id:'$city', sum:{$sum:'$amount'}, avg:{$avg:'$amount'},
                       min:{$min:'$amount'}, max:{$max:'$amount'}, n:{$count:{}},
                       first:{$first:'$_id'}, last:{$last:'$_id'}, ids:{$push:'$_id'}}},
              {$sort:{_id:1}}
            ]
            """);
        Assert.Equal(2, rows.Count);
        Assert.Equal("RJ", rows[0]["_id"].AsString);
        Assert.Equal(5.5m, rows[0]["sum"].AsDecimal);
        Assert.Equal(DocType.Decimal, rows[0]["avg"].Type);
        Assert.Equal(5.5m, rows[0]["avg"].AsDecimal);
        Assert.Equal(3L, rows[0]["n"].AsInt64);
        Assert.Equal(5.5m, rows[0]["min"].AsDecimal);
        Assert.Equal("not numeric", rows[0]["max"].AsString);
        Assert.Equal(3, rows[0]["first"].AsInt32);
        Assert.Equal(5, rows[0]["last"].AsInt32);
        Assert.Equal(new[] { 3, 4, 5 }, rows[0]["ids"].AsArray.Select(v => v.AsInt32));
        Assert.Equal(30, rows[1]["sum"].AsInt32);
        Assert.Equal(15.0, rows[1]["avg"].AsDouble);
    }

    [Fact]
    public void IndexedMatchProjectWindowAndCount()
    {
        Assert.Contains("IXSCAN", _c.Explain("{city:'SP',amount:{$gte:10}}"));
        Assert.Equal(new[] { """{"amount":10}""" }, Run("""
            [{$match:{city:'SP',amount:{$gte:10}}},{$sort:{amount:-1}},{$skip:1},{$limit:1},
             {$project:{amount:1,_id:0}}]
            """));
        Assert.Equal(new[] { """{"n":2}""" }, Run("[{$match:{city:'SP'}},{$count:'n'}]"));
        Assert.Equal(new[] { """{"n":1}""" }, Run("[{$project:{city:1}},{$match:{city:'RJ'}},{$limit:1},{$count:'n'}]"));
        Assert.Empty(Run("[{$limit:0},{$count:'n'}]"));
        Assert.Equal(5, Run("[]").Length);
        Assert.Equal(5, Run("[{$skip:0}]").Length);
    }

    [Fact]
    public void UnwindNestedGroupAndProjectionOperators()
    {
        Assert.Equal(new[] { """{"_id":"a","n":5}""", """{"_id":"b","n":1}""" },
            Run("[{$unwind:'$items'},{$group:{_id:'$items.sku',n:{$sum:'$items.n'}}},{$sort:{_id:1}}]"));
        Assert.Equal(new[] { """{"_id":"a","n":5}""" },
            Run("[{$project:{items:{$slice:1}}},{$unwind:'$items'},{$group:{_id:'$items.sku',n:{$sum:'$items.n'}}}]"));
        Assert.Equal(new[] { """{"_id":"b"}""" },
            Run("[{$project:{items:{$elemMatch:{sku:'b'}}}},{$unwind:'$items'},{$group:{_id:'$items.sku'}}]"));
        _c.Insert("{_id:6,items:'scalar'}");
        Assert.Equal(4, Run("[{$unwind:'$items'}]").Length);
        Assert.Equal(6, _c.Count());
    }

    [Fact]
    public void ExpressionsSupportObjectKeysLiteralAndNumericEquality()
    {
        var c = _db.GetCollection("keys");
        c.InsertMany(new[]
        {
            new Document { ["_id"] = 1, ["v"] = 5 },
            new Document { ["_id"] = 2, ["v"] = 5L },
            new Document { ["_id"] = 3, ["v"] = 5.00m },
            new Document { ["_id"] = 4, ["v"] = 5.0 },
        });
        var result = Assert.Single(c.Aggregate("[{$group:{_id:{k:'$v',literal:{$literal:'$v'}},n:{$sum:1},arr:{$first:['$v',true]}}}]"));
        Assert.Equal(4, result["n"].AsInt32);
        Assert.Equal("""{"k":5,"literal":"$v"}""", result["_id"].AsDocument.ToJson());
        Assert.Equal(DocType.Int32, result["_id"].AsDocument["k"].Type);
        Assert.Equal(2, result["arr"].AsArray.Count);
    }

    [Fact]
    public void MissingValuesAndEmptyGroups()
    {
        var row = Assert.Single(_c.Aggregate("""
            [{$group:{_id:'$absent',s:{$sum:'$absent'},a:{$avg:'$absent'},
                      lo:{$min:'$absent'},hi:{$max:'$absent'},f:{$first:'$absent'},l:{$last:'$absent'}}}]
            """));
        Assert.Equal("""{"_id":null,"s":0,"a":null,"lo":null,"hi":null,"f":null,"l":null}""", row.ToJson());
        Assert.Empty(_db.GetCollection("empty").Aggregate("[{$group:{_id:null,n:{$sum:1}}}]"));
    }

    [Fact]
    public void StableSortControlsFirstLastAndPush()
    {
        var rows = _c.Aggregate("[{$sort:{_id:-1}},{$sort:{city:1}},{$group:{_id:'$city',ids:{$push:'$_id'}}},{$sort:{_id:1}}]");
        Assert.Equal(new[] { 5, 4, 3 }, rows[0]["ids"].AsArray.Select(v => v.AsInt32));
        Assert.Equal(new[] { 2, 1 }, rows[1]["ids"].AsArray.Select(v => v.AsInt32));
    }

    [Fact]
    public void SnapshotAndTransactionVisibility()
    {
        using var snap = _db.BeginSnapshot();
        Assert.Equal(5, snap.GetCollection("sales").Aggregate("[]").Count);
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("sales");
            c.Insert("{_id:6}");
            Assert.Equal(6L, Assert.Single(c.Aggregate("[{$count:'n'}]"))["n"].AsInt64);
            Assert.Equal(5, snap.GetCollection("sales").Aggregate("[]").Count);
            tx.Commit();
        }
        Assert.Equal(5, snap.GetCollection("sales").Aggregate("[]").Count);
        Assert.Equal(6, _c.Aggregate("[]").Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("[{$match:{},$limit:1}]")]
    [InlineData("[{$unknown:1}]")]
    [InlineData("[{$match:1}]")]
    [InlineData("[{$project:1}]")]
    [InlineData("[{$project:{a:'$b'}}]")]
    [InlineData("[{$skip:-1}]")]
    [InlineData("[{$limit:1.5}]")]
    [InlineData("[{$limit:2147483648}]")]
    [InlineData("[{$count:''}]")]
    [InlineData("[{$count:'a.b'}]")]
    [InlineData("[{$sort:{a:0}}]")]
    [InlineData("[{$sort:{a:'x'}}]")]
    [InlineData("[{$unwind:'items'}]")]
    [InlineData("[{$unwind:'$items..x'}]")]
    [InlineData("[{$unwind:{path:'$items'}}]")]
    [InlineData("[{$group:{n:{$sum:1}}}]")]
    [InlineData("[{$group:{_id:0,n:{$count:1}}}]")]
    [InlineData("[{$group:{_id:0,n:{$foo:1}}}]")]
    [InlineData("[{$group:{_id:'$$ROOT'}}]")]
    [InlineData("[{$group:{_id:{$add:[1,2]}}}]")]
    public void InvalidPipelinesFailEvenOnMissingCollections(string pipeline) =>
        Assert.Throws<FolioException>(() => _db.GetCollection("absent").Aggregate(pipeline));

    [Fact]
    public void NumericOverflowAndInvalidLimitsAreExplicit()
    {
        var c = _db.GetCollection("numbers");
        c.Insert(new Document { ["n"] = long.MaxValue });
        c.Insert(new Document { ["n"] = 1 });
        Assert.Throws<FolioException>(() => c.Aggregate("[{$group:{_id:null,n:{$sum:'$n'}}}]"));
        foreach (DocValue value in new DocValue[] { double.NaN, double.PositiveInfinity, decimal.MaxValue })
            Assert.Throws<FolioException>(() => c.Aggregate([new Document { ["$limit"] = value }]));
        c.DeleteMany("{}");
        c.Insert(new Document { ["n"] = int.MaxValue });
        c.Insert(new Document { ["n"] = 1 });
        Assert.Equal(2147483648L, Assert.Single(c.Aggregate("[{$group:{_id:null,n:{$sum:'$n'}}}]"))["n"].AsInt64);
    }

    [Fact]
    public void UnwindResultsDoNotAliasEachOtherOrStoredRows()
    {
        var rows = _c.Aggregate("[{$match:{_id:1}},{$unwind:'$items'}]");
        rows[0]["items"].AsDocument["sku"] = "changed";
        Assert.Equal("b", rows[1]["items"].AsDocument["sku"].AsString);
        Assert.Equal("a", _c.FindById(1)!["items"].AsArray[0].AsDocument["sku"].AsString);
        _db.CheckIntegrity();
    }

    [Fact]
    public void BinaryResultsAreDeepCopies()
    {
        var c = _db.GetCollection("binary");
        c.Insert(new Document { ["items"] = new DocArray { 1, 2 }, ["bin"] = new byte[] { 1, 2 } });
        var rows = c.Aggregate("[{$unwind:'$items'}]");
        rows[0]["bin"].AsBinary[0] = 99;
        Assert.Equal(1, rows[1]["bin"].AsBinary[0]);

        byte[] literal = [3, 4];
        var group = new Document
        {
            ["_id"] = DocValue.Null,
            ["first"] = new Document { ["$first"] = "$bin" },
            ["last"] = new Document { ["$last"] = "$bin" },
            ["constant"] = new Document { ["$push"] = literal },
        };
        var result = Assert.Single(c.Aggregate([new Document { ["$unwind"] = "$items" }, new Document { ["$group"] = group }]));
        result["first"].AsBinary[0] = 50;
        Assert.Equal(1, result["last"].AsBinary[0]);
        result["constant"].AsArray[0].AsBinary[0] = 80;
        Assert.Equal(3, result["constant"].AsArray[1].AsBinary[0]);
        Assert.Equal(3, literal[0]);
        Assert.Equal(1, c.FindOne()!["bin"].AsBinary[0]);
    }
}
