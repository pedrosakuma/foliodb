namespace FolioDb.Tests;

public sealed class ProjectionTests : IDisposable
{
    private readonly TempDb _tmp = new();
    private readonly FolioDatabase _db;
    private readonly Collection _c;

    public ProjectionTests()
    {
        _db = FolioDatabase.Open(_tmp.Path);
        _c = _db.GetCollection("c");
        _c.Insert("""
            { _id: 1, end: { cidade: 'SP', cep: '01000', geo: { lat: 1, lng: 2 } },
              itens: [{ nome: 'a', qtd: 1 }, { nome: 'b', qtd: 2 }, 5, [{ nome: 'c' }]],
              tags: ['x', 'y', 'z'], "0": 'zero', anos: { "2024": 1, "2025": 2 } }
            """);
    }

    public void Dispose() { _db.Dispose(); _tmp.Dispose(); }

    private string Project(string projection) =>
        _c.FindOne("{ _id: 1 }", new FindOptions { Projection = Document.Parse(projection) })!.ToJson();

    [Theory]
    // sub-documents
    [InlineData("{ 'end.cidade': 1 }", """{"_id":1,"end":{"cidade":"SP"}}""")]
    [InlineData("{ 'end.geo.lat': 1, _id: 0 }", """{"end":{"geo":{"lat":1}}}""")]
    [InlineData("{ 'end.x': 1 }", """{"_id":1,"end":{}}""")]
    [InlineData("{ 'end.cidade.x': 1 }", """{"_id":1,"end":{}}""")]
    [InlineData("{ 'end.cep': 0, itens: 0, tags: 0, anos: 0, '0': 0 }", """{"_id":1,"end":{"cidade":"SP","geo":{"lat":1,"lng":2}}}""")]
    // field names under an array apply to every element (scalars dropped on inclusion, kept on exclusion)
    [InlineData("{ 'itens.nome': 1 }", """{"_id":1,"itens":[{"nome":"a"},{"nome":"b"},[{"nome":"c"}]]}""")]
    [InlineData("{ 'itens.nome': 0, end: 0, tags: 0, anos: 0, '0': 0, _id: 0 }", """{"itens":[{"qtd":1},{"qtd":2},5,[{}]]}""")]
    // numeric segments under an array are positions; the result stays an array
    [InlineData("{ 'itens.1': 1, _id: 0 }", """{"itens":[{"nome":"b","qtd":2}]}""")]
    [InlineData("{ 'itens.0.nome': 1, _id: 0 }", """{"itens":[{"nome":"a"}]}""")]
    [InlineData("{ 'tags.0': 1, 'tags.2': 1, _id: 0 }", """{"tags":["x","z"]}""")]
    [InlineData("{ 'tags.9': 1, _id: 0 }", """{"tags":[]}""")]
    [InlineData("{ 'tags.1': 0, end: 0, itens: 0, anos: 0, '0': 0, _id: 0 }", """{"tags":["x","z"]}""")]
    [InlineData("{ 'itens.0.nome': 0, end: 0, tags: 0, anos: 0, '0': 0, _id: 0 }", """{"itens":[{"qtd":1},{"nome":"b","qtd":2},5,[{"nome":"c"}]]}""")]
    // numeric segments on a document are plain field names
    [InlineData("{ 'anos.2024': 1, _id: 0 }", """{"anos":{"2024":1}}""")]
    [InlineData("{ '0': 1 }", """{"_id":1,"0":"zero"}""")]
    // output follows document order, _id first
    [InlineData("{ tags: 1, 'end.cep': 1 }", """{"_id":1,"end":{"cep":"01000"},"tags":["x","y","z"]}""")]
    [InlineData("{ _id: 1 }", """{"_id":1}""")]
    public void NestedPaths(string projection, string expected) => Assert.Equal(expected, Project(projection));

    [Theory]
    [InlineData("{ end: 1, 'end.cep': 1 }")]
    [InlineData("{ 'end.cep': 1, end: 1 }")]
    [InlineData("{ 'end.geo': 0, 'end.geo.lat': 0 }")]
    [InlineData("{ 'itens.0': 1, 'itens.nome': 1 }")]
    [InlineData("{ 'tags.1': 1, 'tags.01': 1 }")]
    [InlineData("{ _id: 1, '_id.x': 1 }")]
    [InlineData("{ 'a..b': 1 }")]
    [InlineData("{ 'a.$': 1 }")]
    [InlineData("{ a: 1, b: 0 }")]
    public void AmbiguousOrInvalidSpecsAreRejected(string projection) =>
        Assert.Throws<FolioException>(() => Project(projection));

    [Fact]
    public void NumericTopLevelNamesAreDistinctFields()
    {
        var c = _db.GetCollection("num");
        c.Insert("""{ _id: 1, "1": 'one', "01": 'zero-one' }""");
        Assert.Equal("""{"_id":1,"1":"one","01":"zero-one"}""", c.FindOne((Document?)null, new FindOptions { Projection = Document.Parse("""{ "1": 1, "01": 1 }""") })!.ToJson());
    }

    [Fact]
    public void WideAndDeepSpecsAreHandled()
    {
        var wide = new Document();
        for (int i = 0; i < 5000; i++) wide["f" + i] = 1;
        wide["tags"] = 1;
        Assert.Equal("""{"_id":1,"tags":["x","y","z"]}""", _c.FindOne("{ _id: 1 }", new FindOptions { Projection = wide })!.ToJson());
        var deep = new Document { [string.Join('.', Enumerable.Repeat("a", 100_000))] = 1 };
        Assert.Throws<FolioException>(() => _c.FindOne("{ _id: 1 }", new FindOptions { Projection = deep }));
    }

    [Fact]
    public void IdSubPathsReplaceWholeIdInclusion()
    {
        var c = _db.GetCollection("cid");
        c.Insert("{ _id: { a: 1, b: 2 }, v: 3 }");
        Assert.Equal("""{"_id":{"a":1},"v":3}""", c.FindOne((Document?)null, new FindOptions { Projection = Document.Parse("{ '_id.a': 1, v: 1 }") })!.ToJson());
        Assert.Equal("""{"_id":{"b":2},"v":3}""", c.FindOne((Document?)null, new FindOptions { Projection = Document.Parse("{ '_id.a': 0 }") })!.ToJson());
    }

    [Fact]
    public void DuplicateFieldsMatchFilterSemantics()
    {
        // Filters and indexes see the first occurrence; inclusion keeps only it and exclusion removes every occurrence.
        var doc = new Document { ["_id"] = 1 };
        doc.AddUnchecked("a", 1);
        doc.AddUnchecked("b", 2);
        doc.AddUnchecked("a", 3);
        var c = _db.GetCollection("dups");
        c.Insert(doc);
        Assert.Equal("""{"_id":1,"a":1}""", c.FindOne("{ a: 1 }", new FindOptions { Projection = Document.Parse("{ a: 1 }") })!.ToJson());
        Assert.Equal("""{"_id":1,"b":2}""", c.FindOne("{ a: 1 }", new FindOptions { Projection = Document.Parse("{ a: 0 }") })!.ToJson());
    }
}
