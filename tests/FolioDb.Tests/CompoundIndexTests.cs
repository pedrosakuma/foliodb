using System.Buffers.Binary;

namespace FolioDb.Tests;

public sealed class CompoundIndexTests
{
    private static string[] Bytes(IEnumerable<Document> docs) =>
        docs.Select(d => Convert.ToHexString(DocumentSerializer.Serialize(d))).Order().ToArray();

    [Theory]
    [InlineData("{ a: 1, b: 1 }")]
    [InlineData("{ a: 1, b: -1 }")]
    [InlineData("{ a: -1, b: 1 }")]
    [InlineData("{ a: -1, b: -1 }")]
    [InlineData("{ a: -1 }")]
    public void DifferentialQueriesAndPersistence(string pattern)
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open())
        {
            var indexed = db.GetCollection("indexed");
            var plain = db.GetCollection("plain");
            var docs = Enumerable.Range(0, 100).Select(i => new Document
            {
                ["_id"] = (long)i,
                ["a"] = i % 5,
                ["b"] = (i % 4) switch { 0 => (DocValue)(i % 13), 1 => (long)(i % 13), 2 => (double)(i % 13), _ => (decimal)(i % 13) },
                ["payload"] = "not indexed",
            }).ToList();
            docs.AddRange(new[]
            {
                Document.Parse("{_id:101,a:1}"), Document.Parse("{_id:102,b:2}"),
                Document.Parse("{_id:103,a:null,b:2}"), Document.Parse("{_id:104,a:1,b:null}"),
                Document.Parse("{_id:105}"), Document.Parse("{_id:106,b:2,a:1}"),
                Document.Parse("{_id:107,a:'x',b:'y'}"), Document.Parse("{_id:108,a:true,b:false}"),
            });
            indexed.InsertMany(docs.Select(d => d.Clone()));
            plain.InsertMany(docs);
            indexed.CreateIndex(Document.Parse(pattern));
            Assert.Contains("IXSCAN", indexed.Explain("{a:1,b:{$gte:2,$lt:8}}"));
            foreach (var query in new[]
            {
                "{a:1}", "{a:1,b:2}", "{a:1,b:{$gt:2,$lt:8}}", "{a:1,b:{$gte:2,$lte:8}}",
                "{a:1,b:{$lt:8}}", "{a:1,b:{$gt:2}}", "{a:1,b:{$lt:2,$gt:8}}",
                "{a:{$lt:3}}", "{a:{$gt:1}}", "{a:{$gte:1,$lte:3}}",
                "{a:null}", "{b:2}", "{a:1,b:null}", "{a:'x'}", "{a:true}",
                "{a:{$in:[1,3]}}", "{a:1,payload:{$exists:true}}",
                "{$and:[{a:1},{a:2}]}", "{a:1,b:{$gt:2,$lt:'z'}}",
            })
            foreach (var projection in new[] { null, "{a:1,b:1}", "{a:1,_id:0}", "{b:1}", "{_id:1}", "{payload:1}" })
            {
                var options = new FindOptions { Projection = projection is null ? null : Document.Parse(projection) };
                Assert.Equal(plain.Count(query), indexed.Count(query));
                Assert.Equal(Bytes(plain.Find(query, options)), Bytes(indexed.Find(query, options)));
            }
            indexed.UpdateOne("{_id:1}", "{$set:{b:55}}");
            indexed.UpdateOne("{_id:2}", "{$unset:{a:1}}");
            indexed.DeleteOne("{_id:3}");
            db.CheckIntegrity();
        }
        using (var db = tmp.Open())
        {
            var indexed = db.GetCollection("indexed");
            Assert.Equal(Document.Parse(pattern).ToJson(), indexed.GetIndexes()[1].Keys.ToJson());
            Assert.Equal(55, indexed.FindById(1)!["b"].AsInt32);
            Assert.Null(indexed.FindById(3));
            db.CheckIntegrity();
        }
    }

    [Fact]
    public void MultikeyCartesianProductAndPositionalPaths()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var indexed = db.GetCollection("i");
        var plain = db.GetCollection("p");
        var docs = new[]
        {
            Document.Parse("{_id:1,a:[0,10],b:['x','y'],items:[{x:1,y:9},{x:9,y:1}]}"),
            Document.Parse("{_id:2,a:[2,2],b:['y'],items:[{x:2,y:2}]}"),
            Document.Parse("{_id:3,a:[],b:[]}")
        };
        indexed.CreateIndex(Document.Parse("{a:1,b:-1}"));
        indexed.CreateIndex(Document.Parse("{'items.x':1,'items.y':-1}"));
        indexed.CreateIndex(Document.Parse("{'items.0.x':-1}"));
        indexed.InsertMany(docs.Select(d => d.Clone()));
        plain.InsertMany(docs);
        foreach (var q in new[] { "{a:{$gt:1,$lt:5}}", "{a:10,b:'y'}", "{'items.x':1,'items.y':1}", "{'items.0.x':1}", "{a:2}" })
        {
            Assert.Equal(plain.Count(q), indexed.Count(q));
            Assert.Equal(Bytes(plain.Find(q)), Bytes(indexed.Find(q)));
            Assert.DoesNotContain("covered", indexed.Explain(q));
        }
        indexed.DeleteOne("{_id:1}");
        db.CheckIntegrity();
    }

    [Fact]
    public void UniqueTuplesAndFailedWritesAreAtomic()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.CreateIndex(Document.Parse("{a:1,b:-1}"), unique: true);
        c.Insert("{_id:1,a:1,b:2}");
        c.Insert("{_id:2,a:1,b:3}");
        Assert.Throws<DuplicateKeyException>(() => c.Insert("{_id:3,a:1,b:2}"));
        Assert.Throws<DuplicateKeyException>(() => c.UpdateOne("{_id:2}", "{$set:{b:2}}"));
        Assert.Equal(3, c.FindById(2)!["b"].AsInt32);
        using (var tx = db.BeginTransaction())
        {
            var t = tx.GetCollection("c");
            Assert.Throws<DuplicateKeyException>(() => t.Insert("{_id:3,a:1,b:2}"));
            t.Insert("{_id:4,a:2,b:2}");
            tx.Commit();
        }
        Assert.Equal(3, c.Count());
        c.Insert("{_id:5,a:5}");
        Assert.Throws<DuplicateKeyException>(() => c.Insert("{_id:6,a:5,b:null}"));
        db.CheckIntegrity();
    }

    [Fact]
    public void ReorderingAndTypeChangesRefreshHints()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.CreateIndex(Document.Parse("{a:1,b:-1}"));
        c.Insert("{_id:1,a:1,b:2}");
        var replacement = Document.Parse("{_id:1,b:2,a:1}");
        c.ReplaceOne(Document.Parse("{_id:1}"), replacement);
        Assert.Equal(Bytes([replacement]), Bytes(c.Find("{a:1}", new FindOptions { Projection = Document.Parse("{a:1,b:1}") })));
        c.UpdateOne(Document.Parse("{_id:1}"), new Document { ["$set"] = new Document { ["b"] = 2.00m } });
        Assert.Equal(DocType.Decimal, c.FindOne("{a:1}", new FindOptions { Projection = Document.Parse("{b:1}") })!["b"].Type);
        db.CheckIntegrity();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{a:0,b:1}")]
    [InlineData("{a:1.5,b:1}")]
    [InlineData("{a:1,b:'desc'}")]
    [InlineData("{_id:1,b:1}")]
    [InlineData("{'.a':1,b:1}")]
    public void InvalidPatterns(string pattern)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Assert.Throws<FolioException>(() => db.GetCollection("c").CreateIndex(Document.Parse(pattern)));
    }

    [Fact]
    public void ExpansionIsBoundedAndDropUsesExactName()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        var name = c.CreateIndex(Document.Parse("{a:1,b:1}"));
        var array = new DocArray();
        for (int i = 0; i < 32; i++) array.Add(i);
        Assert.Throws<FolioException>(() => c.Insert(new Document { ["a"] = array, ["b"] = array }));
        Assert.Equal(0, c.Count());
        Assert.False(c.DropIndex("a"));
        Assert.True(c.DropIndex(name));
        db.CheckIntegrity();
    }

    [Fact]
    public void NestedAndVariableLengthDescendingComponents()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        var raw = db.GetCollection("raw");
        var docs = new[]
        {
            Document.Parse("{_id:1,sub:{x:'a',y:1},z:{a:2}}"),
            Document.Parse("{_id:2,sub:{x:'ab',y:2},z:{a:3}}"),
            Document.Parse("{_id:3,sub:{y:3,x:'a'},z:{a:2}}"),
            new Document { ["_id"] = 4, ["sub"] = new Document { ["x"] = "a\0b", ["y"] = 4 }, ["z"] = new Document { ["a"] = 2 } },
        };
        c.InsertMany(docs.Select(d => d.Clone()));
        raw.InsertMany(docs);
        c.CreateIndex(Document.Parse("{'sub.x':-1,'sub.y':1,z:-1}"));
        foreach (var query in new[] { "{'sub.x':'a'}", "{'sub.x':{$gte:'a',$lt:'b'}}", "{'sub.x':'a','sub.y':1,z:{a:2}}" })
        {
            Assert.Equal(raw.Count(query), c.Count(query));
            var options = new FindOptions { Projection = Document.Parse("{'sub.x':1,'sub.y':1,z:1}") };
            Assert.Equal(Bytes(raw.Find(query, options)), Bytes(c.Find(query, options)));
        }
        db.CheckIntegrity();
    }

    [Fact]
    public void NamingCollisionsAndNormalizedPatternDrop()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        var compound = c.CreateIndex(Document.Parse("{a:1,b:1}"));
        var single = c.CreateIndex("a_1_b");
        Assert.NotEqual(compound, single);
        Assert.Equal(single, c.CreateIndex("a_1_b"));
        Assert.True(c.DropIndex(Document.Parse("{a:NumberLong(1),b:NumberDecimal('1')}")));
        Assert.False(c.DropIndex(Document.Parse("{a:1.0,b:1.0}")));
        Assert.True(c.DropIndex(single));
    }

    [Fact]
    public void VersionTwoIsReadableAndWritesUpgradeHeader()
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open())
        {
            var c = db.GetCollection("c");
            c.CreateIndex("a");
            c.Insert("{_id:1,a:2}");
        }
        using (var file = File.OpenWrite(tmp.Path))
        {
            file.Position = 20;
            Span<byte> version = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(version, 2);
            file.Write(version);
        }
        using (var db = tmp.Open())
        {
            Assert.Equal(1, db.GetCollection("c").Count("{a:2}"));
            db.GetCollection("c").CreateIndex(Document.Parse("{a:1,b:-1}"));
        }
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(tmp.Path).AsSpan(20)));
    }

    [Fact]
    public void AnyCommitUpgradesAVersionTwoHeader()
    {
        using var tmp = new TempDb();
        using (var db = tmp.Open())
        {
            db.GetCollection("c").Insert("{_id:1,a:2}");
            db.Checkpoint();
        }
        using (var file = File.OpenWrite(tmp.Path))
        {
            file.Position = 20;
            Span<byte> version = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(version, 2);
            file.Write(version);
        }
        using (var db = tmp.Open())
            db.GetCollection("c").UpdateOne(Document.Parse("{_id:1}"), Document.Parse("{$set:{a:3}}"));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(tmp.Path).AsSpan(20)));
    }
}
