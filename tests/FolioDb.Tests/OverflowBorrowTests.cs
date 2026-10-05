namespace FolioDb.Tests;

public class OverflowBorrowTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Warm_reads_return_overflow_buffers_even_when_callbacks_throw(int scope, bool fail)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        db.GetCollection("docs").Insert(new Document { ["_id"] = 1, ["payload"] = new string('x', 8192) });
        using var snapshot = scope == 1 ? db.BeginSnapshot() : null;
        using var transaction = scope == 2 ? db.BeginTransaction() : null;
        var c = snapshot?.GetCollection("docs") ?? transaction?.GetCollection("docs") ?? db.GetCollection("docs");
        var failure = new InvalidOperationException("callback failure");
        Func<DocumentView, int> reader = d =>
        {
            if (fail) throw failure;
            return d.TryGetValue("payload", out var p) ? p.AsUtf8String.Length : -1;
        };
        int completed = 0;
        void Read()
        {
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    if (!c.TryReadById(1, reader, out int length) || length != 8192)
                        throw new InvalidDataException("Unexpected payload.");
                    completed++;
                }
                catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
                {
                    completed++;
                }
            }
        }
        Read();
        long allocated = Allocations.Measure(Read);
        Assert.True(allocated < 100 * 1024, $"Allocated {allocated} bytes: overflow buffers should be reused.");
        Assert.True(completed >= 200);
        Assert.True(c.TryReadById(1, static d => d.TryGetValue("payload", out var p) ? p.AsUtf8String.Length : -1, out int result));
        Assert.Equal(8192, result);
        transaction?.Commit();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Nested_reads_do_not_overwrite_outer_overflow_views(int scope)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var outside = db.GetCollection("docs");
        outside.Insert(new Document { ["_id"] = 1, ["payload"] = new string('x', 8192) });
        outside.Insert(new Document { ["_id"] = 2, ["payload"] = new string('y', 8192) });
        using var snapshot = scope == 1 ? db.BeginSnapshot() : null;
        using var transaction = scope == 2 ? db.BeginTransaction() : null;
        var c = snapshot?.GetCollection("docs") ?? transaction?.GetCollection("docs") ?? outside;
        for (int i = 0; i < 20; i++)
        {
            Assert.True(c.TryReadById(1, outer =>
            {
                Assert.True(outer.TryGetValue("payload", out var before));
                Assert.Equal(8192, before.AsUtf8String.Length);
                Assert.True(c.TryReadById(2, inner =>
                {
                    Assert.True(inner.TryGetValue("payload", out var payload));
                    Assert.True(payload.AsUtf8String.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(new string('y', 8192))));
                    return inner.ToDocument();
                }, out var copy));
                Assert.Equal(new string('y', 8192), copy["payload"].AsString);
                foreach (byte b in before.AsUtf8String) Assert.Equal((byte)'x', b);
                return outer.ToDocument();
            }, out var owned));
            Assert.Equal(new string('x', 8192), owned["payload"].AsString);
        }
    }
}
