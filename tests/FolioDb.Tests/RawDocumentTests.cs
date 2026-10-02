namespace FolioDb.Tests;

public class RawDocumentTests
{
    private static Document AllTypes() => new()
    {
        ["d"] = 1.5,
        ["s"] = "héllo",
        ["o"] = new Document { ["x"] = 1 },
        ["a"] = new DocArray { 1, "two", 3.0, DocValue.Null, true },
        ["b"] = DocValue.FromBinary([1, 2, 3]),
        ["id"] = DocValue.FromObjectId(ObjectId.NewObjectId()),
        ["t"] = true,
        ["dt"] = DocValue.FromUnixMilliseconds(1234567),
        ["n"] = DocValue.Null,
        ["i"] = 42,
        ["l"] = 1L << 40,
        ["m"] = 12.345m,
    };

    [Fact]
    public void Enumerators_return_every_type_with_its_payload()
    {
        var doc = AllTypes();
        var bytes = DocumentSerializer.Serialize(doc);
        int i = 0;
        foreach (var f in new RawDocument(bytes))
        {
            var (name, expected) = doc.ElementAt(i++);
            Assert.Equal(name, System.Text.Encoding.UTF8.GetString(f.Name));
            Assert.Equal(expected.Type, f.Value.Type);
            Assert.Equal(expected, f.Value.ToDocValue());
        }
        Assert.Equal(doc.Count, i);

        Assert.True(new RawDocument(bytes).TryGetField("a"u8, out var arr));
        var items = new List<DocValue>();
        foreach (var v in arr.AsArray) items.Add(v.ToDocValue());
        Assert.Equal(doc["a"].AsArray.ToList(), items);
        Assert.True(new RawDocument(bytes).TryGetField("n"u8, out var n));
        Assert.Equal(0, n.Data.Length);
    }

    [Fact]
    public void Truncated_values_throw_instead_of_reading_past_the_document()
    {
        foreach (var (name, value) in AllTypes())
        {
            var full = DocumentSerializer.Serialize(new Document { [name] = value });
            byte[] array = [.. full[..4], (byte)value.Type, .. full[(4 + 2 + name.Length)..]];
            ExpectTruncation(name, full, 4 + 2 + name.Length, array: false);
            ExpectTruncation(name, array, 5, array: true);
        }
    }

    private static void ExpectTruncation(string name, byte[] full, int valueStart, bool array)
    {
        // An array cut down to its type byte reads that byte as the terminator (unchanged lenient behavior).
        int payload = full.Length - valueStart - 1 - (array ? 1 : 0);
        for (int cut = 1; cut <= payload; cut++)
        {
            // Terminator and the last `cut` payload bytes removed: the value runs past the end of the buffer.
            var bytes = full[..^(cut + 1)];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), bytes.Length);
            // MoveNext alone must validate: header walks (Count, TryGetField) never touch Current.
            var ex = array
                ? Record.Exception(() => { var e = new RawArray(bytes).GetEnumerator(); while (e.MoveNext()) { } })
                : Record.Exception(() => { var e = new RawDocument(bytes).GetEnumerator(); while (e.MoveNext()) { } });
            Assert.True(ex is ArgumentOutOfRangeException or CorruptDatabaseException, $"{name} array={array} cut {cut}: {ex?.GetType().Name ?? "no exception"}");
        }
    }

    [Theory]
    [InlineData(0x02, -1, "Negative length.")]
    [InlineData(0x05, -7, "Negative length.")]
    [InlineData(0x03, 4, "Invalid nested length.")]
    [InlineData(0x04, 0, "Invalid nested length.")]
    public void Invalid_length_prefixes_are_reported(byte type, int length, string message)
    {
        byte[] doc = [0, 0, 0, 0, type, 1, (byte)'v', .. BitConverter.GetBytes(length), 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BitConverter.TryWriteBytes(doc.AsSpan(0, 4), doc.Length);
        var ex = Assert.Throws<CorruptDatabaseException>(() => { foreach (var _ in new RawDocument(doc)) { } });
        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData(0x06)]
    [InlineData(0x0B)]
    [InlineData(0x11)]
    [InlineData(0x14)]
    [InlineData(0xFF)]
    public void Unknown_types_are_reported_in_documents_and_arrays(byte type)
    {
        byte[] doc = [0, 0, 0, 0, type, 1, (byte)'v', 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BitConverter.TryWriteBytes(doc.AsSpan(0, 4), doc.Length);
        var ex = Assert.Throws<CorruptDatabaseException>(() => { foreach (var _ in new RawDocument(doc)) { } });
        Assert.Equal($"Unknown value type 0x{type:X2}.", ex.Message);

        byte[] arr = [0, 0, 0, 0, type, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BitConverter.TryWriteBytes(arr.AsSpan(0, 4), arr.Length);
        ex = Assert.Throws<CorruptDatabaseException>(() => { foreach (var _ in new RawArray(arr)) { } });
        Assert.Equal($"Unknown value type 0x{type:X2}.", ex.Message);
    }

    [Fact]
    public void A_truncated_name_is_reported_before_the_value_type()
    {
        byte[] doc = [7, 0, 0, 0, 0xFF, 255, 0];
        Assert.Throws<ArgumentOutOfRangeException>(() => { var e = new RawDocument(doc).GetEnumerator(); while (e.MoveNext()) { } });
    }
}
