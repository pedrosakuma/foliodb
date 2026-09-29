using System.Buffers.Binary;
using System.Text;

namespace FolioDb.Storage;

/// <summary>Database header stored in page 0. Modified transactionally like any other page.</summary>
internal struct DbHeader
{
    public static ReadOnlySpan<byte> Magic => "FolioDb format 1"u8; // exactly 16 bytes
    public const int FormatVersion = 2;

    public int PageSize;
    public uint PageCount;
    public uint FreeListHead;
    public uint FreePageCount;
    public uint CatalogRoot;
    public ulong ChangeCounter;

    public static DbHeader Read(ReadOnlySpan<byte> page)
    {
        if (!page[..16].SequenceEqual(Magic)) throw new CorruptDatabaseException("Not a FolioDb database (bad magic).");
        int version = BinaryPrimitives.ReadInt32LittleEndian(page[20..]);
        if (version != FormatVersion) throw new CorruptDatabaseException($"Unsupported format version {version}.");
        return new DbHeader
        {
            PageSize = BinaryPrimitives.ReadInt32LittleEndian(page[16..]),
            PageCount = BinaryPrimitives.ReadUInt32LittleEndian(page[24..]),
            FreeListHead = BinaryPrimitives.ReadUInt32LittleEndian(page[28..]),
            FreePageCount = BinaryPrimitives.ReadUInt32LittleEndian(page[32..]),
            CatalogRoot = BinaryPrimitives.ReadUInt32LittleEndian(page[36..]),
            ChangeCounter = BinaryPrimitives.ReadUInt64LittleEndian(page[40..]),
        };
    }

    public readonly void Write(Span<byte> page)
    {
        Magic.CopyTo(page);
        BinaryPrimitives.WriteInt32LittleEndian(page[16..], PageSize);
        BinaryPrimitives.WriteInt32LittleEndian(page[20..], FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(page[24..], PageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], FreeListHead);
        BinaryPrimitives.WriteUInt32LittleEndian(page[32..], FreePageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(page[36..], CatalogRoot);
        BinaryPrimitives.WriteUInt64LittleEndian(page[40..], ChangeCounter);
    }

    /// <summary>Reads the page size from the first bytes of an existing database file.</summary>
    public static int ReadPageSize(ReadOnlySpan<byte> first64)
    {
        if (!first64[..16].SequenceEqual(Magic))
            throw new CorruptDatabaseException($"Not a FolioDb database (found '{Encoding.ASCII.GetString(first64[..16])}').");
        int version = BinaryPrimitives.ReadInt32LittleEndian(first64[20..]);
        if (version != FormatVersion) throw new CorruptDatabaseException($"Unsupported format version {version}.");
        return BinaryPrimitives.ReadInt32LittleEndian(first64[16..]);
    }
}
