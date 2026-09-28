namespace FolioDb.Tests;

[FolioDocument]
public partial class NumericHolder
{
    public int Id { get; set; }
    public decimal Price { get; set; }
    public ulong Big { get; set; }
    public ulong? MaybeBig { get; set; }
}

public class RegressionTests
{
    [Fact]
    public void RenameIntoIdIsRejected()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.Insert("{ _id: 1, a: 2 }");
        Assert.Throws<FolioException>(() => c.UpdateOne("{ _id: 1 }", "{ $rename: { a: '_id' } }"));
        Assert.Throws<FolioException>(() => c.UpdateOne("{ _id: 1 }", "{ $rename: { a: '_id.x' } }"));
        Assert.Equal(2, c.FindById(1)!["a"].AsInt64);
        Assert.Null(c.FindById(2));
    }

    [Fact]
    public void DecimalAndUInt64RoundTripLosslessly()
    {
        var h = new NumericHolder { Id = 1, Price = 1234567890123456789012345678m, Big = ulong.MaxValue, MaybeBig = 42 };
        var back = NumericHolder.FromDocument(NumericHolder.ToDocument(h));
        Assert.Equal(h.Price, back.Price);
        Assert.Equal(ulong.MaxValue, back.Big);
        Assert.Equal(42UL, back.MaybeBig);

        var scaled = NumericHolder.FromDocument(NumericHolder.ToDocument(new NumericHolder { Price = 1.50m }));
        Assert.Equal("1.50", scaled.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DisposeWhileWriterActiveTimesOutWithoutClosing()
    {
        using var tmp = new TempDb();
        var db = FolioDatabase.Open(tmp.Path, new FolioOptions { Synchronous = SynchronousMode.Off, BusyTimeout = TimeSpan.FromMilliseconds(50) });
        var tx = db.BeginTransaction();
        tx.GetCollection("c").Insert("{ _id: 1 }");

        Assert.Throws<FolioException>(db.Dispose);

        tx.Commit();
        Assert.Equal(1, db.GetCollection("c").Count());
        db.Dispose();
        Assert.Throws<ObjectDisposedException>(() => db.GetCollection("c").Count());
    }
}
