using FolioDb.Engine;

namespace FolioDb.Tests;

public class DocumentTests
{
    [Fact]
    public void Binary_round_trip_preserves_all_types_and_order()
    {
        var oid = ObjectId.NewObjectId();
        var date = new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);
        var doc = new Document
        {
            ["_id"] = oid,
            ["i"] = 42,
            ["l"] = 1L << 40,
            ["d"] = 3.25,
            ["s"] = "olá \0 mundo",
            ["b"] = true,
            ["n"] = DocValue.Null,
            ["dt"] = date,
            ["bin"] = new byte[] { 1, 2, 0, 3 },
            ["arr"] = new DocArray { 1, "two", new Document { ["x"] = 1 }, new DocArray() },
            ["sub"] = new Document { ["a"] = new Document { ["b"] = -1 } },
        };
        var back = Document.FromBytes(doc.ToBytes());
        Assert.Equal(doc.Keys, back.Keys);
        Assert.Equal(DocType.Int32, back["i"].Type);
        Assert.Equal(DocType.Int64, back["l"].Type);
        Assert.Equal(1L << 40, back["l"].AsInt64);
        Assert.Equal("olá \0 mundo", back["s"].AsString);
        Assert.Equal(date, back["dt"].AsDateTime);
        Assert.Equal(oid, back["_id"].AsObjectId);
        Assert.True(back["n"].IsNull);
        Assert.Equal(new byte[] { 1, 2, 0, 3 }, back["bin"].AsBinary);
        Assert.Equal(doc.ToJson(), back.ToJson());
    }

    [Fact]
    public void Raw_document_reads_fields_without_materializing()
    {
        var bytes = Document.Parse("{ a: 1, b: { c: 'x' }, d: [1, 2, 3] }").ToBytes();
        var raw = new RawDocument(bytes);
        Assert.True(raw.TryGetField("b"u8, out var b));
        Assert.Equal(DocType.Document, b.Type);
        Assert.False(raw.TryGetField("zzz"u8, out _));
        Assert.Equal(3, raw.ToDocument().Count);
    }

    [Fact]
    public void Json_parser_accepts_shell_syntax_and_extended_types()
    {
        var doc = Document.Parse("{ name: 'Ana', \"age\": 30, score: 1.5, big: 12345678901, tags: ['a', \"b\",], " +
                                 "id: { $oid: '65a1b2c3d4e5f60718293a4b' }, at: { $date: '2024-01-02T03:04:05.000Z' }, ok: true, x: null, neg: -2e3 }");
        Assert.Equal("Ana", doc["name"].AsString);
        Assert.Equal(DocType.Int32, doc["age"].Type);
        Assert.Equal(DocType.Double, doc["score"].Type);
        Assert.Equal(DocType.Int64, doc["big"].Type);
        Assert.Equal(2, doc["tags"].AsArray.Count);
        Assert.Equal(DocType.ObjectId, doc["id"].Type);
        Assert.Equal("65a1b2c3d4e5f60718293a4b", doc["id"].AsObjectId.ToString());
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), doc["at"].AsDateTime);
        Assert.Equal(-2000.0, doc["neg"].AsDouble);

        var again = Document.Parse(doc.ToJson());
        Assert.Equal(doc.ToJson(), again.ToJson());
    }

    [Theory]
    [InlineData("{ a: }")]
    [InlineData("{ a: 1")]
    [InlineData("[1, 2]")]
    [InlineData("{ a: 1 } trailing")]
    public void Json_parser_rejects_malformed_input(string json) =>
        Assert.ThrowsAny<Exception>(() => Document.Parse(json));

    [Fact]
    public void Dotted_paths_get_set_remove()
    {
        var doc = new Document();
        doc.SetPath("a.b.c", 1);
        Assert.True(doc.TryGetPath("a.b.c", out var v));
        Assert.Equal(1, v.AsInt32);
        Assert.True(doc.RemovePath("a.b.c"));
        Assert.False(doc.TryGetPath("a.b.c", out _));
    }

    [Fact]
    public void Max_nesting_depth_is_enforced()
    {
        var root = new Document();
        var cur = root;
        for (int i = 0; i < 150; i++)
        {
            var next = new Document();
            cur["x"] = next;
            cur = next;
        }
        Assert.Throws<FolioException>(() => root.ToBytes());
    }

    [Fact]
    public void ObjectIds_are_unique_and_increasing()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => ObjectId.NewObjectId()).ToList();
        Assert.Equal(1000, ids.Distinct().Count());
        for (int i = 1; i < ids.Count; i++) Assert.True(ids[i - 1].CompareTo(ids[i]) < 0);
        Assert.Equal(ids[5], ObjectId.Parse(ids[5].ToString()));
    }

    [Fact]
    public void Index_key_extraction_expands_arrays()
    {
        var bytes = Document.Parse("{ tags: ['a', 'b', 'a'], sub: [{ k: 1 }, { k: 2 }], e: [] }").ToBytes();
        Assert.Equal(2, CollectionEngine.ExtractIndexKeys(bytes, "tags", out bool multi).Count);
        Assert.True(multi);
        Assert.Equal(2, CollectionEngine.ExtractIndexKeys(bytes, "sub.k", out _).Count);
        Assert.Single(CollectionEngine.ExtractIndexKeys(bytes, "e", out _));
        Assert.Empty(CollectionEngine.ExtractIndexKeys(bytes, "missing", out _));
    }
}
