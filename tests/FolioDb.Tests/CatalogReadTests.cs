using FolioDb.Engine;

namespace FolioDb.Tests;

public class CatalogReadTests
{
    [Fact]
    public void Direct_catalog_read_roundtrips_simple_compound_and_descending_indexes()
    {
        var meta = new CollectionMeta { Name = "items", PrimaryRoot = 3 };
        meta.Indexes.Add(new IndexMeta { Name = "name_1", Fields = [new("name", false)], Unique = true, Root = 4 });
        meta.Indexes.Add(new IndexMeta { Name = "nested_-1", Fields = [new("nested.field", true)], Unique = false, Root = 5, MultiKey = true });
        meta.Indexes.Add(new IndexMeta { Name = "a_1_b_-1", Fields = [new("a", false), new("b", true)], Unique = false, Root = 6 });
        var expected = meta.ToDocument();
        var bytes = expected.ToBytes();
        var decoded = CollectionMeta.FromBytes(bytes);
        Array.Clear(bytes);
        Assert.Equal(expected.ToJson(), decoded.ToDocument().ToJson());
    }

    [Theory]
    [InlineData("{name:'c',primaryRoot:2}")]
    [InlineData("{name:'c',primaryRoot:2,indexes:[{name:'i',keys:{a:0},unique:false,root:3,multiKey:false}]}")]
    [InlineData("{name:'c',primaryRoot:2,indexes:[{name:'i',keys:{},unique:false,root:3,multiKey:false}]}")]
    [InlineData("{name:'c',primaryRoot:2,indexes:[{name:'i',keys:{a:1,_id:1},unique:false,root:3,multiKey:false}]}")]
    public void Invalid_catalog_still_raises_an_error(string json)
    {
        Assert.ThrowsAny<Exception>(() => CollectionMeta.FromBytes(Document.Parse(json).ToBytes()));
    }

    [Fact]
    public void Index_catalog_changes_do_not_leak_across_snapshots()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("items");
        c.Insert("{_id:1,a:2,b:3}");
        c.CreateIndex(new Document { ["a"] = 1, ["b"] = -1 });
        using (var snapshot = db.BeginSnapshot())
        {
            var sc = snapshot.GetCollection("items");
            Assert.Contains("IXSCAN", sc.Explain("{a:2,b:3}"));
            c.Drop();
            c.Insert("{_id:2,a:2,b:3}");
            Assert.Contains("COLLSCAN", c.Explain("{a:2,b:3}"));
            Assert.Equal(1, sc.FindOne("{a:2,b:3}")!["_id"].AsInt32);
            Assert.Equal(2, c.FindOne("{a:2,b:3}")!["_id"].AsInt32);
        }
        db.CheckIntegrity();
    }
}
