using System.Buffers;
using System.Text;

namespace FolioDb;

/// <summary>
/// Read-only, borrowed view over a stored document, passed to the callback of
/// <see cref="Collection.TryReadById{TResult}(DocValue, Func{DocumentView, TResult}, out TResult)"/>.
/// It points directly into database page memory: it is only valid during that synchronous callback, and as a
/// <c>ref struct</c> it cannot be stored in fields, captured by lambdas, boxed or used across <c>await</c>.
/// Use <see cref="ToDocument"/> or <see cref="DocValueView.ToDocValue"/> to obtain owned copies.
/// </summary>
public readonly ref struct DocumentView
{
    private readonly RawDocument _raw;

    internal DocumentView(RawDocument raw) => _raw = raw;

    /// <summary>Looks up the first top-level field with this name (no dotted paths). Returns false when absent.</summary>
    public bool TryGetValue(string name, out DocValueView value)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> utf8 = stackalloc byte[DocumentSerializer.MaxNameBytes];
        // Longer names can never be stored, so they are simply absent.
        if (!Encoding.UTF8.TryGetBytes(name, utf8, out int n))
        {
            value = default;
            return false;
        }
        return TryGetValue(utf8[..n], out value);
    }

    /// <summary>Looks up the first top-level field whose UTF-8 name equals <paramref name="utf8Name"/>.</summary>
    public bool TryGetValue(scoped ReadOnlySpan<byte> utf8Name, out DocValueView value)
    {
        if (_raw.TryGetField(utf8Name, out var raw))
        {
            value = new DocValueView(raw);
            return true;
        }
        value = default;
        return false;
    }

    public bool ContainsField(string name) => TryGetValue(name, out _);

    /// <summary>Number of fields (walks the document).</summary>
    public int FieldCount
    {
        get
        {
            int n = 0;
            foreach (var _ in _raw) n++;
            return n;
        }
    }

    /// <summary>Allocates an independent <see cref="Document"/> (strings, binaries and nested values are copied).</summary>
    public Document ToDocument() => _raw.ToDocument();

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());

    /// <summary>Enumerates fields in stored order.</summary>
    public ref struct Enumerator
    {
        private RawDocument.Enumerator _inner;

        internal Enumerator(RawDocument.Enumerator inner) => _inner = inner;

        public readonly DocFieldView Current
        {
            get
            {
                var f = _inner.Current;
                return new DocFieldView(f.Name, new DocValueView(f.Value));
            }
        }

        public bool MoveNext() => _inner.MoveNext();
    }
}

/// <summary>A field of a <see cref="DocumentView"/>; same lifetime rules.</summary>
public readonly ref struct DocFieldView
{
    internal DocFieldView(ReadOnlySpan<byte> utf8Name, DocValueView value)
    {
        Utf8Name = utf8Name;
        Value = value;
    }

    /// <summary>Borrowed UTF-8 bytes of the field name.</summary>
    public ReadOnlySpan<byte> Utf8Name { get; }
    public DocValueView Value { get; }

    /// <summary>Allocates the field name as a string.</summary>
    public string GetName() => Encoding.UTF8.GetString(Utf8Name);

    public bool NameEquals(scoped ReadOnlySpan<char> name) => DocValueView.Utf8EqualsUtf16(Utf8Name, name);
}

/// <summary>
/// Read-only, borrowed view over a stored value; same lifetime rules as <see cref="DocumentView"/>.
/// Scalar accessors follow <see cref="DocValue"/> conversion rules and throw <see cref="InvalidCastException"/>
/// on type mismatch; span accessors return borrowed bytes without copying.
/// </summary>
public readonly ref struct DocValueView
{
    private readonly RawValue _raw;

    internal DocValueView(RawValue raw) => _raw = raw;

    /// <summary><see cref="DocType.Null"/> for <c>default</c>.</summary>
    public DocType Type => _raw.Type == 0 ? DocType.Null : _raw.Type;
    public bool IsNull => Type == DocType.Null;
    public bool IsNumber => _raw.Type is DocType.Int32 or DocType.Int64 or DocType.Double or DocType.Decimal;

    public int AsInt32 => _raw.Type switch
    {
        DocType.Int32 => _raw.Int32,
        DocType.Int64 => checked((int)_raw.Int64),
        DocType.Double => checked((int)_raw.Double),
        DocType.Decimal => (int)_raw.Decimal,
        _ => throw InvalidCast("Int32"),
    };

    public long AsInt64 => _raw.Type switch
    {
        DocType.Int32 => _raw.Int32,
        DocType.Int64 => _raw.Int64,
        DocType.Double => checked((long)_raw.Double),
        DocType.Decimal => (long)_raw.Decimal,
        _ => throw InvalidCast("Int64"),
    };

    public double AsDouble => _raw.Type switch
    {
        DocType.Int32 => _raw.Int32,
        DocType.Int64 => _raw.Int64,
        DocType.Double => _raw.Double,
        DocType.Decimal => (double)_raw.Decimal,
        _ => throw InvalidCast("Double"),
    };

    public decimal AsDecimal => _raw.Type switch
    {
        DocType.Int32 => _raw.Int32,
        DocType.Int64 => _raw.Int64,
        DocType.Double => (decimal)_raw.Double,
        DocType.Decimal => _raw.Decimal,
        _ => throw InvalidCast("Decimal"),
    };

    public bool AsBoolean => _raw.Type == DocType.Boolean ? _raw.Boolean : throw InvalidCast("Boolean");
    public long AsUnixMilliseconds => _raw.Type == DocType.DateTime ? _raw.Int64 : throw InvalidCast("DateTime");
    public DateTime AsDateTime => _raw.Type == DocType.DateTime
        ? new DateTime(DateTime.UnixEpoch.Ticks + _raw.Int64 * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc)
        : throw InvalidCast("DateTime");
    public ObjectId AsObjectId => _raw.Type == DocType.ObjectId ? new ObjectId(_raw.Data) : throw InvalidCast("ObjectId");

    /// <summary>Borrowed UTF-8 bytes of a string value (no length prefix, no terminator).</summary>
    public ReadOnlySpan<byte> AsUtf8String => _raw.Type == DocType.String ? _raw.Data : throw InvalidCast("String");

    /// <summary>Borrowed bytes of a binary value.</summary>
    public ReadOnlySpan<byte> AsBinary => _raw.Type == DocType.Binary ? _raw.Data : throw InvalidCast("Binary");

    public DocumentView AsDocument => _raw.Type == DocType.Document ? new DocumentView(_raw.AsDocument) : throw InvalidCast("Document");
    public DocArrayView AsArray => _raw.Type == DocType.Array ? new DocArrayView(_raw.AsArray) : throw InvalidCast("Array");

    /// <summary>Allocates a string (explicit materialization).</summary>
    public string GetString() => Encoding.UTF8.GetString(AsUtf8String);

    /// <summary>True when this is a string equal (ordinal) to <paramref name="text"/>; false for other types. Does not allocate.</summary>
    public bool StringEquals(scoped ReadOnlySpan<char> text) =>
        _raw.Type == DocType.String && Utf8EqualsUtf16(_raw.Data, text);

    /// <summary>True when this is a string whose UTF-8 bytes equal <paramref name="utf8"/>; false for other types.</summary>
    public bool StringEquals(scoped ReadOnlySpan<byte> utf8) =>
        _raw.Type == DocType.String && _raw.Data.SequenceEqual(utf8);

    /// <summary>Allocates an independent <see cref="DocValue"/> (strings, binaries and nested values are copied).</summary>
    public DocValue ToDocValue() => _raw.Type == 0 ? DocValue.Null : _raw.ToDocValue();

    private InvalidCastException InvalidCast(string target) => new($"Cannot read {Type} value as {target}.");

    /// <summary>Ordinal comparison consistent with materializing the UTF-8 bytes with <see cref="Encoding.UTF8"/>.</summary>
    internal static bool Utf8EqualsUtf16(ReadOnlySpan<byte> utf8, ReadOnlySpan<char> text)
    {
        // Each UTF-16 code unit needs 1..3 UTF-8 bytes; equal lengths therefore imply pure ASCII.
        if (utf8.Length < text.Length || utf8.Length > text.Length * 3) return false;
        if (utf8.Length == text.Length) return Ascii.Equals(utf8, text);
        while (!text.IsEmpty)
        {
            if (utf8.IsEmpty) return false;
            // Stored strings are valid UTF-8, so they never decode to a lone surrogate: invalid UTF-16 never matches.
            if (Rune.DecodeFromUtf16(text, out var expected, out int chars) != OperationStatus.Done) return false;
            Rune.DecodeFromUtf8(utf8, out var actual, out int bytes);
            if (actual != expected) return false;
            text = text[chars..];
            utf8 = utf8[bytes..];
        }
        return utf8.IsEmpty;
    }
}

/// <summary>Read-only, borrowed view over a stored array; same lifetime rules as <see cref="DocumentView"/>.</summary>
public readonly ref struct DocArrayView
{
    private readonly RawArray _raw;

    internal DocArrayView(RawArray raw) => _raw = raw;

    /// <summary>Number of elements (walks the array).</summary>
    public int Count
    {
        get
        {
            int n = 0;
            foreach (var _ in _raw) n++;
            return n;
        }
    }

    /// <summary>Allocates an independent <see cref="DocArray"/>.</summary>
    public DocArray ToArray() => _raw.ToArray();

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());

    public ref struct Enumerator
    {
        private RawArray.Enumerator _inner;

        internal Enumerator(RawArray.Enumerator inner) => _inner = inner;

        public readonly DocValueView Current => new(_inner.Current);

        public bool MoveNext() => _inner.MoveNext();
    }
}
