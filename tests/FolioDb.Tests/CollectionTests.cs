namespace FolioDb.Tests;

public class CollectionTests : IDisposable
{
    private readonly TempDb _tmp = new();
    private readonly FolioDatabase _db;
    private readonly Collection _people;

    public CollectionTests()
    {
        _db = _tmp.Open();
        _people = _db.GetCollection("people");
        _people.InsertMany(new[]
        {
            Document.Parse("{ _id: 1, name: 'Ana',   age: 31, city: 'Lisboa', tags: ['dev', 'ops'], address: { zip: '1000' } }"),
            Document.Parse("{ _id: 2, name: 'Bruno', age: 25, city: 'Porto',  tags: ['dev'],        address: { zip: '4000' } }"),
            Document.Parse("{ _id: 3, name: 'Carla', age: 42, city: 'Lisboa', tags: [],             scores: [10, 20, 30] }"),
            Document.Parse("{ _id: 4, name: 'Duda',  age: 25, city: null,     tags: ['qa'],         items: [{ sku: 'a', qty: 2 }, { sku: 'b', qty: 9 }] }"),
            Document.Parse("{ _id: 5, name: 'Edu',   age: 60.5 }"),
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        _tmp.Dispose();
    }

    private int[] Ids(string filter, FindOptions? o = null) => _people.Find(filter, o).Select(d => d["_id"].AsInt32).ToArray();

    [Theory]
    [InlineData("{}", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("{ age: 25 }", new[] { 2, 4 })]
    [InlineData("{ age: { $gt: 30 } }", new[] { 1, 3, 5 })]
    [InlineData("{ age: { $gte: 25, $lt: 31 } }", new[] { 2, 4 })]
    [InlineData("{ age: { $ne: 25 } }", new[] { 1, 3, 5 })]
    [InlineData("{ age: { $in: [31, 42] } }", new[] { 1, 3 })]
    [InlineData("{ age: { $nin: [31, 42] } }", new[] { 2, 4, 5 })]
    [InlineData("{ city: null }", new[] { 4, 5 })]
    [InlineData("{ city: { $exists: false } }", new[] { 5 })]
    [InlineData("{ city: { $exists: true } }", new[] { 1, 2, 3, 4 })]
    [InlineData("{ tags: 'dev' }", new[] { 1, 2 })]
    [InlineData("{ tags: { $size: 0 } }", new[] { 3 })]
    [InlineData("{ tags: { $all: ['dev', 'ops'] } }", new[] { 1 })]
    [InlineData("{ tags: ['dev'] }", new[] { 2 })]
    [InlineData("{ 'address.zip': '4000' }", new[] { 2 })]
    [InlineData("{ 'items.sku': 'b' }", new[] { 4 })]
    [InlineData("{ items: { $elemMatch: { sku: 'a', qty: { $gt: 1 } } } }", new[] { 4 })]
    [InlineData("{ items: { $elemMatch: { sku: 'a', qty: { $gt: 5 } } } }", new int[0])]
    [InlineData("{ scores: { $elemMatch: { $gt: 15, $lt: 25 } } }", new[] { 3 })]
    [InlineData("{ 'scores.1': 20 }", new[] { 3 })]
    [InlineData("{ name: { $regex: '^[AB]' } }", new[] { 1, 2 })]
    [InlineData("{ name: { $regex: 'CARLA', $options: 'i' } }", new[] { 3 })]
    [InlineData("{ age: { $type: 'double' } }", new[] { 5 })]
    [InlineData("{ $or: [{ age: 25 }, { city: 'Lisboa' }] }", new[] { 1, 2, 3, 4 })]
    [InlineData("{ $and: [{ age: { $gt: 20 } }, { city: 'Lisboa' }] }", new[] { 1, 3 })]
    [InlineData("{ $nor: [{ age: 25 }, { city: 'Lisboa' }] }", new[] { 5 })]
    [InlineData("{ age: { $not: { $gt: 30 } } }", new[] { 2, 4 })]
    [InlineData("{ age: { $gt: 'a' } }", new int[0])] // type bracketing
    [InlineData("{ _id: 3 }", new[] { 3 })]
    [InlineData("{ _id: { $gte: 4 } }", new[] { 4, 5 })]
    public void Filters_match_mongo_semantics(string filter, int[] expected)
    {
        Assert.Equal(expected, Ids(filter).Order().ToArray());
    }

    [Theory]
    [InlineData("{ age: { $gt: 30 } }")]
    [InlineData("{ age: 25 }")]
    [InlineData("{ tags: 'dev' }")]
    [InlineData("{ city: 'Lisboa', age: { $lt: 40 } }")]
    [InlineData("{ age: { $in: [25, 60.5] } }")]
    [InlineData("{ tags: { $gte: 'd', $lt: 'p' } }")]
    [InlineData("{ age: { $gte: 25, $lte: 42 } }")]
    public void Indexed_queries_return_same_results_as_full_scans(string filter)
    {
        var before = Ids(filter).Order().ToArray();
        _people.CreateIndex("age");
        _people.CreateIndex("tags");
        _people.CreateIndex("city");
        Assert.DoesNotContain("COLLSCAN", _people.Explain(filter));
        Assert.Equal(before, Ids(filter).Order().ToArray());
        _db.CheckIntegrity();
    }

    [Fact]
    public void Explain_reports_plan_choice()
    {
        Assert.StartsWith("COLLSCAN", _people.Explain("{ age: 25 }"));
        Assert.Contains("_id", _people.Explain("{ _id: 1 }"));
        _people.CreateIndex("age");
        Assert.Contains("age", _people.Explain("{ age: 25 }"));
        Assert.Contains("age", _people.Explain("{ age: { $gt: 25 } }"));
        Assert.StartsWith("COLLSCAN", _people.Explain("{ age: null }"));
        Assert.StartsWith("COLLSCAN", _people.Explain("{ $or: [{ age: 1 }, { name: 'x' }] }"));
    }

    [Fact]
    public void Sort_skip_limit_projection()
    {
        Assert.Equal(new[] { 5, 3, 1, 2, 4 }, Ids("{}", new FindOptions { Sort = Document.Parse("{ age: -1, _id: 1 }") }));
        Assert.Equal(new[] { 1, 4 }, Ids("{}", new FindOptions { Sort = Document.Parse("{ name: 1 }"), Skip = 0, Limit = 5 }).Where(i => i is 1 or 4).ToArray());
        Assert.Equal(new[] { 3, 1 }, Ids("{}", new FindOptions { Sort = Document.Parse("{ age: -1 }"), Skip = 1, Limit = 2 }));
        // missing fields sort first (null) ascending
        Assert.Equal(new[] { 4, 5 }, Ids("{}", new FindOptions { Sort = Document.Parse("{ city: 1 }") })[..2]); // stable

        var projected = _people.FindOne("{ _id: 1 }", new FindOptions { Projection = Document.Parse("{ name: 1, 'address.zip': 1 }") })!;
        Assert.Equal(new[] { "_id", "name", "address" }, projected.Keys.ToArray());
        Assert.Equal("{\"_id\":1,\"name\":\"Ana\",\"address\":{\"zip\":\"1000\"}}", projected.ToJson());

        var excluded = _people.FindOne("{ _id: 1 }", new FindOptions { Projection = Document.Parse("{ tags: 0, address: 0, _id: 0 }") })!;
        Assert.Equal(new[] { "name", "age", "city" }, excluded.Keys.ToArray());
    }

    [Fact]
    public void Insert_generates_object_id_and_rejects_duplicates()
    {
        var doc = new Document { ["name"] = "Zé" };
        var id = _people.Insert(doc);
        Assert.Equal(DocType.ObjectId, id.Type);
        Assert.Equal(id, doc["_id"]);
        Assert.Equal("Zé", _people.FindById(id)!["name"].AsString);
        Assert.Throws<DuplicateKeyException>(() => _people.Insert("{ _id: 1 }"));
        Assert.Throws<DuplicateKeyException>(() => _people.Insert("{ _id: 1.0 }")); // numeric equality
        Assert.Throws<FolioException>(() => _people.Insert("{ _id: [1] }"));
    }

    [Fact]
    public void InsertMany_is_atomic()
    {
        Assert.Throws<DuplicateKeyException>(() => _people.InsertMany(new[] { Document.Parse("{ _id: 100 }"), Document.Parse("{ _id: 1 }") }));
        Assert.Null(_people.FindById(100));
    }

    [Fact]
    public void Unique_index_is_enforced_on_insert_update_and_creation()
    {
        _people.CreateIndex("name", unique: true);
        Assert.Throws<DuplicateKeyException>(() => _people.Insert("{ name: 'Ana' }"));
        Assert.Throws<DuplicateKeyException>(() => _people.UpdateOne("{ _id: 2 }", "{ $set: { name: 'Ana' } }"));
        Assert.Equal("Bruno", _people.FindById(2)!["name"].AsString);
        Assert.Equal(1, _people.UpdateOne("{ _id: 2 }", "{ $set: { name: 'Bruno' } }").MatchedCount); // same value is fine

        Assert.Throws<DuplicateKeyException>(() => _people.CreateIndex("age", unique: true));
        Assert.DoesNotContain(_people.GetIndexes(), i => i.Field == "age");
        _db.CheckIntegrity();
    }

    [Fact]
    public void Multikey_index_tracks_array_changes()
    {
        _people.CreateIndex("tags");
        Assert.True(_people.GetIndexes().Single(i => i.Field == "tags").MultiKey);
        _people.UpdateOne("{ _id: 1 }", "{ $pull: { tags: 'dev' }, $push: { tags: 'lead' } }");
        Assert.Equal(new[] { 2 }, Ids("{ tags: 'dev' }"));
        Assert.Equal(new[] { 1 }, Ids("{ tags: 'lead' }"));
        _people.DeleteOne("{ _id: 2 }");
        Assert.Empty(Ids("{ tags: 'dev' }"));
        _db.CheckIntegrity();
    }

    [Fact]
    public void Update_operators()
    {
        _people.UpdateOne("{ _id: 1 }", "{ $inc: { age: 1, visits: 1 }, $set: { 'address.city': 'X' }, $unset: { city: '' } }");
        var d = _people.FindById(1)!;
        Assert.Equal(32, d["age"].AsInt32);
        Assert.Equal(1, d["visits"].AsInt32);
        Assert.Equal("X", d["address"].AsDocument["city"].AsString);
        Assert.False(d.ContainsKey("city"));

        _people.UpdateOne("{ _id: 3 }", "{ $mul: { age: 2 }, $min: { low: 5 }, $max: { age: 10 }, $push: { scores: { $each: [40, 50] } }, $addToSet: { tags: 'x' } }");
        _people.UpdateOne("{ _id: 3 }", "{ $addToSet: { tags: 'x' }, $pop: { scores: -1 }, $rename: { low: 'lowest' } }");
        d = _people.FindById(3)!;
        Assert.Equal(84, d["age"].AsInt32);
        Assert.Equal(5, d["lowest"].AsInt32);
        Assert.Equal("[20,30,40,50]", DocJson.WriteValue(d["scores"]));
        Assert.Equal("[\"x\"]", DocJson.WriteValue(d["tags"]));

        _people.UpdateOne("{ _id: 5 }", "{ $currentDate: { seen: true } }");
        Assert.Equal(DocType.DateTime, _people.FindById(5)!["seen"].Type);

        Assert.Throws<FolioException>(() => _people.UpdateOne("{ _id: 1 }", "{ $set: { _id: 9 } }"));
        Assert.Throws<FolioException>(() => _people.UpdateOne("{ _id: 1 }", "{ $bogus: { a: 1 } }"));
        Assert.Throws<FolioException>(() => _people.UpdateOne("{ _id: 1 }", "{ $inc: { name: 1 } }"));
    }

    [Fact]
    public void UpdateMany_and_modified_count()
    {
        var r = _people.UpdateMany("{ age: 25 }", "{ $set: { junior: true } }");
        Assert.Equal(2, r.MatchedCount);
        Assert.Equal(2, r.ModifiedCount);
        r = _people.UpdateMany("{ age: 25 }", "{ $set: { junior: true } }");
        Assert.Equal(2, r.MatchedCount);
        Assert.Equal(0, r.ModifiedCount);
    }

    [Fact]
    public void Replace_and_upsert()
    {
        var r = _people.ReplaceOne(Document.Parse("{ _id: 2 }"), Document.Parse("{ name: 'B2' }"));
        Assert.Equal(1, r.ModifiedCount);
        Assert.Equal("{\"_id\":2,\"name\":\"B2\"}", _people.FindById(2)!.ToJson());

        r = _people.UpdateOne("{ name: 'Nova', age: { $eq: 7 }, x: { $gt: 1 } }", "{ $set: { city: 'Faro' } }", upsert: true);
        Assert.Equal(0, r.MatchedCount);
        Assert.NotNull(r.UpsertedId);
        var up = _people.FindById(r.UpsertedId!.Value)!;
        Assert.Equal("Nova", up["name"].AsString);
        Assert.Equal(7, up["age"].AsInt32);
        Assert.Equal("Faro", up["city"].AsString);
        Assert.False(up.ContainsKey("x"));

        r = _people.ReplaceOne(Document.Parse("{ _id: 77 }"), Document.Parse("{ v: 1 }"), upsert: true);
        Assert.Equal(77, r.UpsertedId!.Value.AsInt32);
    }

    [Fact]
    public void Delete_one_many_and_drop()
    {
        Assert.Equal(1, _people.DeleteOne("{ age: 25 }"));
        Assert.Equal(1, _people.DeleteMany("{ age: 25 }"));
        Assert.Equal(3, _people.Count());
        Assert.True(_people.DeleteById(1));
        Assert.False(_people.DeleteById(1));
        Assert.Equal(2, _people.DeleteMany((string?)null));
        Assert.Equal(0, _people.Count());

        Assert.True(_people.Drop());
        Assert.DoesNotContain("people", _db.GetCollectionNames());
        Assert.Empty(_people.Find());
        Assert.Equal(0, _people.Count());
        Assert.False(_people.Drop());
    }

    [Fact]
    public void Collections_are_isolated_and_listed()
    {
        _db.GetCollection("other").Insert("{ _id: 1, x: 'other' }");
        Assert.Equal(new[] { "other", "people" }, _db.GetCollectionNames().Order().ToArray());
        Assert.Equal("Ana", _people.FindById(1)!["name"].AsString);
        Assert.Throws<ArgumentException>(() => _db.GetCollection(""));
    }

    [Fact]
    public void Drop_index_and_integrity_after_many_mutations()
    {
        _people.CreateIndex("age");
        _people.CreateIndex("address.zip");
        var rnd = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            int id = rnd.Next(10, 400);
            switch (rnd.Next(3))
            {
                case 0: _people.ReplaceOne(new Document { ["_id"] = id }, new Document { ["age"] = rnd.Next(100), ["address"] = new Document { ["zip"] = rnd.Next(5).ToString() } }, upsert: true); break;
                case 1: _people.DeleteById(id); break;
                default: _people.UpdateOne(new Document { ["_id"] = id }, Document.Parse($"{{ $inc: {{ age: {rnd.Next(-3, 3)} }} }}")); break;
            }
        }
        _db.CheckIntegrity();
        var viaIndex = Ids("{ age: { $gte: 50 } }").Order().ToArray();
        Assert.True(_people.DropIndex("age"));
        Assert.False(_people.DropIndex("age"));
        Assert.Equal(viaIndex, Ids("{ age: { $gte: 50 } }").Order().ToArray());
        _db.CheckIntegrity();
    }

    [Fact]
    public void Large_documents_use_overflow_pages()
    {
        var big = new string('z', 3_000_000);
        _people.Insert(new Document { ["_id"] = 99, ["blob"] = big });
        Assert.Equal(big, _people.FindById(99)!["blob"].AsString);
        _people.DeleteById(99);
        _db.CheckIntegrity();
        Assert.Throws<FolioException>(() => _people.Insert(new Document { ["blob"] = new string('z', 17 * 1024 * 1024) }));
    }

    [Fact]
    public void Oversized_index_keys_are_rejected()
    {
        _people.CreateIndex("name");
        Assert.Throws<FolioException>(() => _people.Insert(new Document { ["name"] = new string('n', 5000) }));
        Assert.Throws<FolioException>(() => _people.Insert(new Document { ["_id"] = new string('n', 5000) }));
    }

    [Fact]
    public void Data_persists_across_reopen()
    {
        _people.CreateIndex("age");
        _db.Dispose();
        using var db = _tmp.Open();
        var c = db.GetCollection("people");
        Assert.Equal(5, c.Count());
        Assert.Contains("age", c.Explain("{ age: 25 }"));
        Assert.Equal(2, c.Count("{ age: 25 }"));
    }
}
