namespace FolioDb.Tests;

[FolioDocument]
public partial class KeyedByPoint
{
    [FolioId] public Point Key { get; set; }
    public int N { get; set; }
}

[FolioDocument]
public partial class Reentrant
{
    public string? Name { get; set; }

    /// <summary>Serializes another entity on the same thread while this one is being written.</summary>
    public int Nested => DocumentWriter.Serialize(new Outer.Nested { Value = Name }, out _).Length;
}

/// <summary>Hand-written WriteTo implementations, including invalid ones.</summary>
public sealed class Scripted : IFolioDocument<Scripted>
{
    public Action<DocumentWriter> Script { get; init; } = static _ => { };
    public static Document ToDocument(Scripted value) => throw new NotSupportedException();
    public static Scripted FromDocument(Document document) => new();
    public static void WriteTo(Scripted value, DocumentWriter writer) => value.Script(writer);
}

public class WriteToTests
{
    private static string Json(DocValue v) => DocJson.WriteValue(v);

    /// <summary>WriteTo must store exactly the bytes Insert(ToDocument(x)) stores, with the same _id.</summary>
    private static void Same<T>(T entity) where T : IFolioDocument<T>
    {
        var bytes = DocumentWriter.Serialize(entity, out var id);
        var doc = T.ToDocument(entity);
        if (doc.TryGetValue("_id", out var expectedId))
        {
            Assert.Equal(Json(expectedId), Json(id));
            Assert.Equal(DocumentSerializer.Serialize(doc), bytes);
        }
        else
        {
            Assert.Equal(DocType.ObjectId, id.Type);
            Assert.Equal(DocumentSerializer.SerializeWithId(doc, id), bytes);
        }
    }

    private static Wide FullWide() => new()
    {
        B = 200, SB = -5, S = -300, US = 60000, I = -7, UI = 4_000_000_000, L = long.MinValue, UL = ulong.MaxValue,
        F = 1.5f, D = Math.PI, M = 79228162514264337593543950335m, C = 'ç', Flag = true,
        When = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Local), Day = new DateOnly(1999, 12, 31),
        At = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.FromHours(-3)), Span = TimeSpan.FromMinutes(-90),
        G = Guid.NewGuid(), Oid = ObjectId.NewObjectId(), Text = "olá", Blob = [0, 255, 7],
        Raw = new DocArray { 1, "two", DocValue.Null, Document.Parse("{ z: [1] }") }, Any = 42L,
        Bag = Document.Parse("{ a: { b: [1, 2] }, _id: 3 }"),
        MaybeColor = Color.Green, MaybeLong = null, Set = ["x", "y"], RoList = [3, 2, 1],
        Grid = [[1, 2], [], [3]], Jagged = [[4], [5, 6], null!], Holes = [1, null, 3],
        Places = new() { ["home"] = new Address("R", "Lisboa") { Zip = "1000" }, ["work"] = new Address("S", "Porto"), ["none"] = null! },
        Groups = new Dictionary<string, List<string?>> { ["g"] = ["a", null], ["empty"] = [], ["nil"] = null! },
        Points = [new Point { X = 1, Y = 2 }, new Point { Ref = ObjectId.NewObjectId() }],
        Origin = new Point { Ref = ObjectId.NewObjectId(), X = -1 },
        MaybeOrigin = new Point { Y = 9 }, Inner = new Outer.Nested { Value = "in" }, Custom = new Manual { V = "m" },
    };

    [Fact]
    public void WriteTo_stores_the_same_bytes_as_ToDocument_for_every_member_kind()
    {
        Same(FullWide());
        Same(new Wide());
        Same(new Wide { Text = null, Any = DocValue.Null, MaybeLong = 5, MaybeColor = null });
        Same(new Person { Id = 1, Name = "Ana", Age = 30, Home = new Address("R", "L"), Past = [new Address("a", "b")], Metrics = new() { ["m"] = 1.5 } });
        Same(new Person());
        Same(new Point { X = 1 });
        Same(new Point { Ref = ObjectId.NewObjectId(), Y = 2 });
        Same(new Address("s", "c"));
        Same(new Outer.Nested { Value = "v" });
        Same(new KeyedByPoint { Key = new Point { X = 3, Y = 4 }, N = 1 });
        Same(new Manual { V = "default WriteTo" });
        Same(new Reentrant { Name = "r" });
    }

    [Fact]
    public void Errors_match_ToDocument()
    {
        var longName = new string('n', 300);
        var bad = new Wide { Places = new() { [longName] = new Address("s", "c") } };
        Assert.Throws<FolioException>(() => DocumentSerializer.Serialize(Wide.ToDocument(bad)));
        Assert.Throws<FolioException>(() => DocumentWriter.Serialize(bad, out _));

        var deep = new Document();
        var cur = deep;
        for (int i = 0; i < 120; i++) cur = (cur["d"] = new Document()).AsDocument;
        var tooDeep = new Wide { Bag = deep };
        Assert.Throws<FolioException>(() => DocumentSerializer.Serialize(Wide.ToDocument(tooDeep)));
        Assert.Throws<FolioException>(() => DocumentWriter.Serialize(tooDeep, out _));

        Assert.Throws<ArgumentNullException>(() => DocumentWriter.Serialize<Person>(null!, out _));

        // The thread's writer is reset after a failure.
        Same(FullWide());
    }

    [Fact]
    public void Writer_rejects_invalid_hand_written_sequences_and_recovers()
    {
        Assert.Throws<FolioException>(() => DocumentWriter.Serialize(new Scripted
        {
            Script = w => { w.Write("_id"u8, 1); w.Write("_id", 2); },
        }, out _));
        Assert.Throws<InvalidOperationException>(() => DocumentWriter.Serialize(new Scripted { Script = w => w.BeginArray("a"u8) }, out _));
        Assert.Throws<InvalidOperationException>(() => DocumentWriter.Serialize(new Scripted { Script = w => w.End() }, out _));
        Assert.Throws<InvalidOperationException>(() => DocumentWriter.Serialize(new Scripted { Script = w => w.WriteItem(1) }, out _));
        Assert.Throws<InvalidOperationException>(() => DocumentWriter.Serialize(new Scripted
        {
            Script = w => { w.BeginArray("a"u8); w.Write("x"u8, 1); },
        }, out _));

        var bytes = DocumentWriter.Serialize(new Scripted
        {
            Script = w =>
            {
                w.Write("a"u8, 1);
                w.BeginDocument("_id"u8);
                w.Write("k"u8, "v");
                w.BeginArray("xs"u8);
                w.WriteItem(1);
                w.BeginDocumentItem();
                w.End();
                w.End();
                w.End();
            },
        }, out var id);
        Assert.Equal("{\"k\":\"v\",\"xs\":[1,{}]}", Json(id));
        Assert.Equal("{\"a\":1,\"_id\":{\"k\":\"v\",\"xs\":[1,{}]}}", Document.FromBytes(bytes).ToJson());
    }

    [Fact]
    public void Typed_inserts_store_what_untyped_inserts_store()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var typed = db.GetCollection<Wide>("typed");
        var untyped = db.GetCollection("untyped");
        var entities = new[] { FullWide(), new Wide(), new Wide { I = 3 } };

        var ids = new List<DocValue> { typed.Insert(entities[0]) };
        ids.AddRange(typed.InsertMany(entities.Skip(1)));
        for (int i = 0; i < entities.Length; i++)
        {
            Assert.Equal(DocType.ObjectId, ids[i].Type);
            var expected = Wide.ToDocument(entities[i]);
            expected.InsertFirst("_id", ids[i]);
            untyped.Insert(expected);
            Assert.Equal(untyped.FindById(ids[i])!.ToJson(), typed.Untyped.FindById(ids[i])!.ToJson());
        }

        var keyed = db.GetCollection<KeyedByPoint>("keyed");
        var key = keyed.Insert(new KeyedByPoint { Key = new Point { X = 1 }, N = 7 });
        Assert.Equal(7, keyed.FindById(key)!.N);
        Assert.Throws<DuplicateKeyException>(() => keyed.Insert(new KeyedByPoint { Key = new Point { X = 1 }, N = 8 }));

        var scripted = db.GetCollection<Scripted>("scripted");
        Assert.Throws<FolioException>(() => scripted.Insert(new Scripted
        {
            Script = w => { w.BeginArray("_id"u8); w.WriteItem(1); w.End(); },
        }));
        Assert.Equal(0, scripted.Count());

        using (var tx = db.BeginTransaction())
        {
            var people = tx.GetCollection<Person>("people");
            people.InsertMany([new Person { Id = 1, Name = "a" }, new Person { Id = 2, Name = "b" }]);
            Assert.Throws<DuplicateKeyException>(() => people.Insert(new Person { Id = 1 }));
            tx.Commit();
        }
        Assert.Equal(2, db.GetCollection<Person>("people").Count());
        Assert.Throws<DuplicateKeyException>(() => db.GetCollection<Person>("people").InsertMany([new Person { Id = 3 }, new Person { Id = 2 }]));
        Assert.Equal(2, db.GetCollection<Person>("people").Count());
        db.CheckIntegrity();
    }
}
