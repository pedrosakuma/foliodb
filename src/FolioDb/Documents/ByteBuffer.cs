using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace FolioDb;

/// <summary>Growable byte buffer backed by <see cref="ArrayPool{T}"/> that supports back-patching (length prefixes).</summary>
internal sealed class ByteBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _length;

    public ByteBuffer(int initialCapacity = 256) => _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);

    public int Length => _length;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);
    public byte[] ToArray() => WrittenSpan.ToArray();
    public void Clear() => _length = 0;
    public void Truncate(int length) => _length = length;

    public void Advance(int count) => _length += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsMemory(_length);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(Math.Max(sizeHint, 1));
        return _buffer.AsSpan(_length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int size)
    {
        if (_buffer.Length - _length < size) Grow(size);
    }

    private void Grow(int size)
    {
        var n = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + size));
        _buffer.AsSpan(0, _length).CopyTo(n);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = n;
    }

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buffer[_length++] = b;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public void WriteInt32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), v);
        _length += 4;
    }

    public void WriteInt64(long v)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), v);
        _length += 8;
    }

    public void WriteUInt64BigEndian(ulong v)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), v);
        _length += 8;
    }

    public void PatchInt32(int position, int v) => BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(position), v);

    public void Dispose()
    {
        var b = _buffer;
        _buffer = [];
        if (b.Length > 0) ArrayPool<byte>.Shared.Return(b);
    }
}
