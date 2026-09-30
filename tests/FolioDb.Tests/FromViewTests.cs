using System.Buffers.Binary;

namespace FolioDb.Tests;

[FolioDocument]
public partial class Wide
{
    public byte B { get; set; }
    public sbyte SB { get; set; }
    public short S { get; set; }
    public ushort US { get; set; }
    public int I { get; set; }
    public uint UI { get; set; }
    public long L { get; set; }
    public ulong UL { get; set; }
    public float F { get; set; }
    public double D { get; set; }
    public decimal M { get; set; }
    public char C { get; set; }
    public bool Flag { get; set; }
    public DateTime When { get; set; }
    public DateOnly Day { get; set; }
    public DateTimeOffset At { get; set; }
    public TimeSpan Span { get; set; }
    public Guid G { get; set; }
    public ObjectId Oid { get; set; }
    public string? Text { get; set; } = "init";
    public byte[]? Blob { get; set; }
    public DocArray? Raw { get; set; }
    public DocValue Any { get; set; }
    public Document? Bag { get; set; }
    public Color? MaybeColor { get; set; }
    public long? MaybeLong { get; set; }
    public HashSet<string>? Set { get; set; }
    public IReadOnlyList<int>? RoList { get; set; }
    public List<List<int>>? Grid { get; set; }
    public int[][]? Jagged { get; set; }
    public int?[]? Holes { get; set; }
    public Dictionary<string, Address>? Places { get; set; }
    public IDictionary<string, List<string?>>? Groups { get; set; }
    public List<Point>? Points { get; set; }
    public Point Origin { get; set; }
    public Point? MaybeOrigin { get; set; }
    public Outer.Nested? Inner { get; set; }
    public Manual? Custom { get; set; }
}

/// <summary>Hand-written mapper without FromView: exercises the interface's materializing default.</summary>
public sealed class Manual : IFolioDocument<Manual>
{
    public string? V { get; set; }
    public static Document ToDocument(Manual value) => new() { ["v"] = value.V };
    public static Manual FromDocument(Document document) => new() { V = document.TryGetValue("v", out var v) && !v.IsNull ? v.AsString : null };
}

public class FromViewTests
{
    private static Manual FromViewDefault(byte[] bytes) => ViaInterface<Manual>(bytes);
    private static T ViaInterface<T>(byte[] bytes) where T : IFolioDocument<T> => T.FromView(View(bytes));

    private static DocumentView View(byte[] bytes) => new(new RawDocument(bytes));

    /// <summary>FromView must yield exactly what FromDocument yields from the materialized document (or the same exception type).</summary>
    private static void Same<T>(byte[] bytes) where T : IFolioDocument<T>
    {
        Exception? viewError = null, docError = null;
        T? viaView = default, viaDoc = default;
        try { viaView = ViaInterface<T>(bytes); } catch (Exception e) { viewError = e; }
        try { viaDoc = T.FromDocument(Document.FromBytes(bytes)); } catch (Exception e) { docError = e; }
        Assert.Equal(docError?.GetType(), viewError?.GetType());
        if (docError is null) Assert.Equal(T.ToDocument(viaDoc!).ToJson(), T.ToDocument(viaView!).ToJson());
    }

    private static void Same<T>(Document doc) where T : IFolioDocument<T> => Same<T>(doc.ToBytes());

    /// <summary>Concatenates the fields of both documents into one, keeping duplicated names (first occurrence first).</summary>
    private static byte[] Merge(Document first, Document second)
    {
        byte[] a = first.ToBytes(), b = second.ToBytes();
        var merged = new byte[a.Length + b.Length - 5];
        BinaryPrimitives.WriteInt32LittleEndian(merged, merged.Length);
        a.AsSpan(4, a.Length - 5).CopyTo(merged.AsSpan(4));
        b.AsSpan(4, b.Length - 5).CopyTo(merged.AsSpan(a.Length - 1));
        return merged;
    }

    private static Wide FullWide() => new()
    {
        B = 200, SB = -5, S = -300, US = 60000, I = -7, UI = 4_000_000_000, L = long.MinValue, UL = ulong.MaxValue,
        F = 1.5f, D = Math.PI, M = 79228162514264337593543950335m, C = 'ç', Flag = true,
        When = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc), Day = new DateOnly(1999, 12, 31),
        At = new DateTimeOffset(2021, 5, 6, 7, 8, 9, TimeSpan.Zero), Span = TimeSpan.FromMinutes(-90),
        G = Guid.NewGuid(), Oid = ObjectId.NewObjectId(), Text = "olá", Blob = [0, 255, 7],
        Raw = new DocArray { 1, "two", DocValue.Null }, Any = 42L, Bag = Document.Parse("{ a: { b: [1, 2] } }"),
        MaybeColor = Color.Green, MaybeLong = null, Set = ["x", "y"], RoList = [3, 2, 1],
        Grid = [[1, 2], [], [3]], Jagged = [[4], [5, 6]], Holes = [1, null, 3],
        Places = new() { ["home"] = new Address("R", "Lisboa") { Zip = "1000" }, ["work"] = new Address("S", "Porto") },
        Groups = new Dictionary<string, List<string?>> { ["g"] = ["a", null], ["empty"] = [] },
        Points = [new Point { X = 1, Y = 2 }], Origin = new Point { Ref = ObjectId.NewObjectId(), X = -1 },
        MaybeOrigin = new Point { Y = 9 }, Inner = new Outer.Nested { Value = "in" }, Custom = new Manual { V = "m" },
    };

    [Fact]
    public void FromView_matches_FromDocument_for_every_member_kind()
    {
        var w = FullWide();
        Same<Wide>(Wide.ToDocument(w));
        var back = Wide.FromView(View(Wide.ToDocument(w).ToBytes()));
        Assert.Equal(w.UL, back.UL);
        Assert.Equal(w.M, back.M);
        Assert.Equal(w.C, back.C);
        Assert.Equal(w.G, back.G);
        Assert.Equal(w.Places, back.Places);
        Assert.Equal(w.Groups!["g"], back.Groups!["g"]);
        Assert.Equal(w.Jagged, back.Jagged);
        Assert.Equal("m", back.Custom!.V);

        Same<Wide>(Wide.ToDocument(new Wide { Text = null, Any = DocValue.Null }));
        Same<Person>(Person.ToDocument(new Person { Id = 1, Name = "Ana", Home = new Address("R", "L"), Past = [new Address("a", "b")] }));
    }

    [Fact]
    public void Missing_and_null_fields_match_FromDocument()
    {
        Same<Wide>(new Document());
        Same<Person>(new Document());
        Same<Address>(new Document());
        Same<Address>(Document.Parse("{ street: null, zip: 'z' }"));
        Same<Point>(new Document());
        Same<Wide>(Document.Parse("{ text: null, blob: null, raw: null, bag: null, maybeColor: null, set: null, grid: [[1], null], places: null, groups: { g: null }, points: null, maybeOrigin: null, inner: null, custom: null }"));
        Assert.Equal("init", Wide.FromView(View(new Document().ToBytes())).Text);
    }

    [Fact]
    public void Numeric_and_string_conversions_match_FromDocument()
    {
        Same<Wide>(Document.Parse("{ b: 3.0, sB: 2, s: 5, uS: 7.0, i: 9, uI: 12, l: 5.0, f: 2, d: 3, m: 4.5, flag: true }"));
        Same<Wide>(new Document { ["uL"] = "18446744073709551615", ["m"] = "1.25e2", ["l"] = DocValue.FromDecimal(12m), ["d"] = DocValue.FromDecimal(0.1m) });
        Same<Wide>(new Document { ["uL"] = DocValue.FromDecimal(ulong.MaxValue), ["i"] = 3L });
        var g = Guid.NewGuid();
        Same<Wide>(new Document { ["g"] = DocValue.FromBinary(g.ToByteArray()) });
        Assert.Equal(g, Wide.FromView(View(new Document { ["g"] = DocValue.FromBinary(g.ToByteArray()) }.ToBytes())).G);
        Same<Wide>(new Document { ["g"] = g.ToString("N") });
        Same<Wide>(new Document { ["c"] = "é" });
    }

    [Theory]
    [InlineData("{ b: 300 }")]
    [InlineData("{ b: -1 }")]
    [InlineData("{ i: 'x' }")]
    [InlineData("{ i: 3000000000 }")]
    [InlineData("{ uI: -1 }")]
    [InlineData("{ uL: -1 }")]
    [InlineData("{ uL: 'nope' }")]
    [InlineData("{ m: 'nope' }")]
    [InlineData("{ c: 'xy' }")]
    [InlineData("{ c: '' }")]
    [InlineData("{ c: '😀' }")]
    [InlineData("{ c: 1 }")]
    [InlineData("{ g: 'nope' }")]
    [InlineData("{ g: 5 }")]
    [InlineData("{ flag: 1 }")]
    [InlineData("{ text: 5 }")]
    [InlineData("{ set: 'x' }")]
    [InlineData("{ grid: [5] }")]
    [InlineData("{ places: [1] }")]
    [InlineData("{ origin: null }")]
    [InlineData("{ inner: 5 }")]
    [InlineData("{ maybeColor: 'red' }")]
    public void Conversion_failures_match_FromDocument(string json) => Same<Wide>(Document.Parse(json));

    [Fact]
    public void Duplicated_field_names_take_the_first_occurrence()
    {
        var bytes = Merge(Document.Parse("{ name: 'first', yrs: null, tags: ['a'] }"), Document.Parse("{ name: 'second', yrs: 5, tags: 5, _id: 3 }"));
        Same<Person>(bytes);
        var p = Person.FromView(View(bytes));
        Assert.Equal("first", p.Name);
        Assert.Null(p.Age);
        Assert.Equal(["a"], p.Tags);
        Assert.Equal(3, p.Id);
    }

    [Fact]
    public void Unmapped_fields_are_skipped()
    {
        var doc = Person.ToDocument(new Person { Id = 1, Name = "Ana" });
        doc["unknown"] = Document.Parse("{ deep: [1, { x: 'y' }] }");
        doc["other"] = "z";
        Same<Person>(doc);
    }

    [Fact]
    public void Hand_written_mapper_falls_back_to_FromDocument()
    {
        Assert.Equal("q", FromViewDefault(new Document { ["v"] = "q" }.ToBytes()).V);
    }

    [Fact]
    public void Typed_reads_match_untyped_find_then_map()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var people = db.GetCollection<Person>("people");
        people.CreateIndex("home.city");
        for (int i = 1; i <= 30; i++)
            people.Insert(new Person { Id = i, Name = "p" + i, Age = i % 7, Home = new Address("R", i % 3 == 0 ? "Porto" : "Lisboa"), Tags = ["t" + i] });

        string Json(IEnumerable<Person> ps) => string.Join("|", ps.Select(p => Person.ToDocument(p).ToJson()));
        string JsonDocs(IEnumerable<Document> ds) => Json(ds.Select(Person.FromDocument));

        foreach (var filter in new[] { "{}", "{ 'home.city': 'Porto' }", "{ yrs: { $gt: 3 } }", "{ _id: { $gte: 10, $lt: 20 } }", "{ name: 'none' }" })
        {
            foreach (var options in new FindOptions?[]
            {
                null, new() { Limit = 3 }, new() { Skip = 2, Limit = 4 }, new() { Skip = 100 }, new() { Skip = -1, Limit = 0 }, new() { Limit = -5 },
                new() { Sort = Document.Parse("{ yrs: -1 }"), Limit = 5 }, new() { Projection = Document.Parse("{ name: 1 }") },
            })
            {
                Assert.Equal(JsonDocs(people.Untyped.Find(filter, options)), Json(people.Find(filter, options)));
                Assert.Equal(JsonDocs(people.Untyped.Find(Document.Parse(filter), options)), Json(people.Find(Document.Parse(filter), options)));
                var prepared = PreparedFilter.Parse(filter);
                Assert.Equal(JsonDocs(people.Untyped.Find(prepared, options)), Json(people.Find(prepared, options)));
                var one = people.Untyped.FindOne(filter, options);
                Assert.Equal(one is null ? "" : Json([Person.FromDocument(one)]), people.FindOne(filter, options) is { } p ? Json([p]) : "");
            }
        }

        Assert.Equal("p5", people.FindById(5)!.Name);
        Assert.Null(people.FindById(99));
        Assert.Null(db.GetCollection<Person>("missing").FindById(1));
        Assert.Empty(db.GetCollection<Person>("missing").Find("{}"));

        using var tx = db.BeginTransaction();
        var txPeople = tx.GetCollection<Person>("people");
        txPeople.Insert(new Person { Id = 100, Name = "pending" });
        Assert.Equal("pending", txPeople.FindById(100)!.Name);
        Assert.Equal("pending", txPeople.FindOne("{ _id: 100 }")!.Name);
        tx.Rollback();
        Assert.Null(people.FindById(100));
    }
}
