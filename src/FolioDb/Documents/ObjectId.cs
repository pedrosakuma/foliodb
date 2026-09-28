using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FolioDb;

/// <summary>12-byte identifier: 4-byte big-endian timestamp, 5-byte per-process random, 3-byte counter.</summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    private static readonly long s_random = CreateProcessRandom();
    private static int s_counter = RandomNumberGenerator.GetInt32(0, 0xFFFFFF);

    private readonly uint _a;
    private readonly uint _b;
    private readonly uint _c;

    public const int Size = 12;

    public ObjectId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size) throw new ArgumentException("ObjectId requires 12 bytes.", nameof(bytes));
        _a = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        _b = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        _c = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
    }

    public static ObjectId NewObjectId()
    {
        Span<byte> buf = stackalloc byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        long r = s_random;
        buf[4] = (byte)(r >> 32);
        buf[5] = (byte)(r >> 24);
        buf[6] = (byte)(r >> 16);
        buf[7] = (byte)(r >> 8);
        buf[8] = (byte)r;
        int c = Interlocked.Increment(ref s_counter) & 0xFFFFFF;
        buf[9] = (byte)(c >> 16);
        buf[10] = (byte)(c >> 8);
        buf[11] = (byte)c;
        return new ObjectId(buf);
    }

    private static long CreateProcessRandom()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return BinaryPrimitives.ReadInt64LittleEndian(b) & 0xFF_FFFF_FFFF;
    }

    public DateTime CreationTime => DateTime.UnixEpoch.AddSeconds(_a);

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], _b);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], _c);
    }

    public static ObjectId Parse(ReadOnlySpan<char> hex)
    {
        if (!TryParse(hex, out var id)) throw new FormatException($"Invalid ObjectId '{hex}'.");
        return id;
    }

    public static bool TryParse(ReadOnlySpan<char> hex, out ObjectId id)
    {
        id = default;
        if (hex.Length != 24) return false;
        Span<byte> buf = stackalloc byte[Size];
        if (Convert.FromHexString(hex, buf, out _, out int written) != System.Buffers.OperationStatus.Done || written != Size)
            return false;
        id = new ObjectId(buf);
        return true;
    }

    public override string ToString()
    {
        Span<byte> buf = stackalloc byte[Size];
        WriteTo(buf);
        return Convert.ToHexStringLower(buf);
    }

    public bool Equals(ObjectId other) => _a == other._a && _b == other._b && _c == other._c;
    public override bool Equals(object? obj) => obj is ObjectId o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c);
    public int CompareTo(ObjectId other)
    {
        int c = _a.CompareTo(other._a);
        if (c != 0) return c;
        c = _b.CompareTo(other._b);
        return c != 0 ? c : _c.CompareTo(other._c);
    }
    public static bool operator ==(ObjectId l, ObjectId r) => l.Equals(r);
    public static bool operator !=(ObjectId l, ObjectId r) => !l.Equals(r);
}
