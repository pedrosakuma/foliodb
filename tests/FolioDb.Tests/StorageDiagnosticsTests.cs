using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class StorageDiagnosticsTests
{
    private static byte[] Key(int value) => KeyEncoder.Encode(value);

    [Fact]
    public void Empty_tree_space_accounting_is_exact()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = 1024 });
        using var tx = db.BeginWrite();
        var tree = new BTree(tx.Storage, BTree.Create(tx.Storage));

        var stats = tree.Diagnose();

        Assert.Equal(1, stats.Height);
        Assert.Equal(0, stats.EntryCount);
        Assert.Equal(1, stats.LeafPages);
        Assert.Equal(0, stats.InteriorPages);
        Assert.Equal(12, stats.LeafLiveBytes);
        Assert.Equal(0, stats.LeafCellBytes);
        Assert.Equal(0, stats.LeafFragmentedBytes);
        Assert.Equal(1012, stats.LeafFreeBytes);
        Assert.Equal(1, stats.AllocatedPageRuns);
        Assert.Equal(1, stats.AllocatedPageSpan);
        Assert.Equal(0, stats.MaximumPageGap);
    }

    [Fact]
    public void Delete_reports_internal_fragmentation_and_preserves_page_accounting()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = 1024 });
        using var tx = db.BeginWrite();
        var tree = new BTree(tx.Storage, BTree.Create(tx.Storage));
        for (int i = 0; i < 30; i++) tree.Insert(Key(i), new byte[12], overwrite: false);

        Assert.True(tree.Delete(Key(10)));
        var stats = tree.Diagnose();

        Assert.Equal(29, stats.EntryCount);
        Assert.True(stats.LeafFragmentedBytes > 0);
        Assert.Equal(1024 * stats.LeafPages,
            stats.LeafLiveBytes + stats.LeafFragmentedBytes + stats.LeafFreeBytes);
        Assert.Equal(1024 * stats.InteriorPages,
            stats.InteriorLiveBytes + stats.InteriorFragmentedBytes + stats.InteriorFreeBytes);
    }

    [Fact]
    public void Overflow_pages_report_payload_metadata_and_tail_space()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = 1024 });
        using var tx = db.BeginWrite();
        var tree = new BTree(tx.Storage, BTree.Create(tx.Storage));
        tree.Insert(Key(1), new byte[2500], overwrite: false);

        var stats = tree.Diagnose();

        Assert.Equal(3, stats.OverflowPages);
        Assert.Equal(2500, stats.OverflowPayloadBytes);
        Assert.Equal(2512, stats.OverflowLiveBytes);
        Assert.Equal(560, stats.OverflowFreeBytes);
        Assert.Equal(1024 * stats.OverflowPages, stats.OverflowLiveBytes + stats.OverflowFreeBytes);
    }

    [Fact]
    public void Database_diagnostics_identify_catalog_primary_and_secondary_trees()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { PageSize = 1024 });
        var collection = db.GetCollection("items");
        collection.CreateIndex("city");
        collection.CreateIndex(Document.Parse("{ bucket: 1, score: -1 }"));
        collection.CreateIndex("tags");
        collection.Insert(new Document
        {
            ["_id"] = 1,
            ["city"] = "a",
            ["bucket"] = 2,
            ["score"] = 3,
            ["tags"] = new DocArray { "x", "y" },
        });

        var diagnostics = db.GetStorageDiagnostics();

        Assert.Equal(5, diagnostics.Trees.Count);
        Assert.Single(diagnostics.Trees, t => t.Kind == StorageTreeKind.Catalog);
        Assert.Single(diagnostics.Trees, t => t.Kind == StorageTreeKind.Primary && t.Collection == "items");
        Assert.Equal(3, diagnostics.Trees.Count(t => t.Kind == StorageTreeKind.Secondary));
        Assert.True(diagnostics.Trees.Single(t => t.Index == "tags_1").MultiKey);
        Assert.All(diagnostics.Trees, tree =>
        {
            Assert.True(tree.Tree.Height >= 1);
            Assert.True(tree.Tree.LeafPages >= 1);
        });
    }
}
