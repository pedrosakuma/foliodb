using FolioDb.Storage;

namespace FolioDb.Tests;

public class CleanPageMemoTests
{
    private static void Fill(FolioDatabase db)
    {
        var c = db.GetCollection("docs");
        c.InsertMany(Enumerable.Range(0, 3000).Select(i => new Document { ["_id"] = i, ["pad"] = new string('x', 100) }));
    }

    private static void ReadAll(Engine.EngineTx tx)
    {
        var meta = tx.GetCollection("docs");
        for (int i = 0; i < 3000; i += 7) Assert.NotNull(Engine.CollectionEngine.FindById(tx, meta, i));
    }

    [Fact]
    public void Read_only_transactions_memoize_only_interior_pages()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Fill(db);
        using var tx = db.BeginRead();
        ReadAll(tx);
        var memo = tx.Storage.MemoizedPages;
        Assert.NotEmpty(memo);
        Assert.All(memo, p => Assert.Equal(BTree.InteriorType, p[0]));
        ReadAll(tx);
    }

    [Fact]
    public void Write_transactions_also_memoize_leaves()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        Fill(db);
        using var tx = db.BeginWrite();
        ReadAll(tx);
        Assert.Contains(tx.Storage.MemoizedPages, p => p[0] == BTree.LeafType);
    }
}
