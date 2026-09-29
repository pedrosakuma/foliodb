using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FolioDb;

/// <summary>
/// JSON transport layer (never used for storage). The parser is lenient (Mongo-shell style): unquoted keys,
/// single-quoted strings, trailing commas, <c>ObjectId("..")</c>, <c>ISODate("..")</c>, <c>NumberLong(..)</c>, <c>NumberDecimal("..")</c>
/// and Extended JSON wrappers (<c>{"$oid":..}</c>, <c>{"$date":..}</c>, <c>{"$numberLong":..}</c>, <c>{"$numberDecimal":..}</c>, <c>{"$binary":..}</c>).
/// Numbers: integers → Int32/Int64, anything with '.', or exponent → Double.
/// The writer emits relaxed Extended JSON; doubles always keep a decimal point so types round-trip.
/// </summary>
public static class DocJson
{
    public static Document ParseDocument(string json)
    {
        var v = Parse(json);
        return v.Type == DocType.Document ? v.AsDocument : throw new FormatException("Expected a JSON object.");
    }

    public static DocValue Parse(string json)
    {
        var p = new Parser(json);
        var v = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd) throw p.Error("Unexpected trailing content");
        return v;
    }

    /// <summary>Parses a comma-separated list of values (e.g. shell call arguments).</summary>
    public static List<DocValue> ParseList(string text)
    {
        var p = new Parser(text);
        var list = new List<DocValue>();
        p.SkipWs();
        while (!p.AtEnd)
        {
            list.Add(p.ParseValue());
            p.SkipWs();
            if (p.AtEnd) break;
            p.Expect(',');
            p.SkipWs();
        }
        return list;
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _s;
        private int _pos;
        private int _depth;

        public Parser(string s)
        {
            _s = s;
            _pos = 0;
            _depth = 0;
        }

        public readonly bool AtEnd => _pos >= _s.Length;

        public readonly FormatException Error(string message) => new($"{message} at position {_pos}.");

        public void SkipWs()
        {
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (char.IsWhiteSpace(c)) _pos++;
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                {
                    while (_pos < _s.Length && _s[_pos] != '\n') _pos++;
                }
                else break;
            }
        }

        public void Expect(char c)
        {
            SkipWs();
            if (_pos >= _s.Length || _s[_pos] != c) throw Error($"Expected '{c}'");
            _pos++;
        }

        private bool TryConsume(char c)
        {
            SkipWs();
            if (_pos < _s.Length && _s[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        public DocValue ParseValue()
        {
            SkipWs();
            if (AtEnd) throw Error("Unexpected end of input");
            char c = _s[_pos];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"':
                case '\'': return DocValue.FromString(ParseString());
                default:
                    if (c == '-' || c == '+' || char.IsAsciiDigit(c) || c == '.') return ParseNumber();
                    if (IsIdentStart(c)) return ParseIdentifierValue();
                    throw Error($"Unexpected character '{c}'");
            }
        }

        private DocValue ParseObject()
        {
            if (++_depth > DocumentSerializer.MaxDepth) throw Error("Nesting too deep");
            _pos++; // {
            var doc = new Document();
            while (true)
            {
                SkipWs();
                if (TryConsume('}')) break;
                string key;
                char c = _s[_pos];
                if (c == '"' || c == '\'') key = ParseString();
                else if (IsIdentStart(c) || char.IsAsciiDigit(c)) key = ParseIdentifier();
                else throw Error("Expected property name");
                Expect(':');
                doc.Set(key, ParseValue());
                SkipWs();
                if (TryConsume(',')) continue;
                Expect('}');
                break;
            }
            _depth--;
            return ConvertExtended(doc);
        }

        private DocValue ConvertExtended(Document doc)
        {
            if (doc.Count != 1) return doc;
            foreach (var (k, v) in doc)
            {
                switch (k)
                {
                    case "$oid" when v.Type == DocType.String: return ObjectId.Parse(v.AsString);
                    case "$date": return ParseDate(v);
                    case "$numberLong" when v.Type == DocType.String:
                        return DocValue.FromInt64(long.Parse(v.AsString, CultureInfo.InvariantCulture));
                    case "$numberInt" when v.Type == DocType.String:
                        return DocValue.FromInt32(int.Parse(v.AsString, CultureInfo.InvariantCulture));
                    case "$numberDouble" when v.Type == DocType.String:
                        return DocValue.FromDouble(ParseDoubleSpecial(v.AsString));
                    case "$numberDecimal" when v.Type == DocType.String:
                        return ParseDecimal(v.AsString);
                    case "$binary" when v.Type == DocType.String: return DocValue.FromBinary(Convert.FromBase64String(v.AsString));
                    case "$binary" when v.Type == DocType.Document && v.AsDocument.TryGetValue("base64", out var b64):
                        return DocValue.FromBinary(Convert.FromBase64String(b64.AsString));
                }
            }
            return doc;
        }

        private DocValue ParseDecimal(string s) =>
            decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
                ? DocValue.FromDecimal(m)
                : throw Error($"Invalid decimal '{s}' (range ±7.9e28, up to 28 decimal places)");

        private static double ParseDoubleSpecial(string s) => s switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ => double.Parse(s, CultureInfo.InvariantCulture),
        };

        private readonly DocValue ParseDate(DocValue v)
        {
            if (v.Type == DocType.String)
                return DateTime.Parse(v.AsString, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (v.IsNumber) return DocValue.FromUnixMilliseconds(v.AsInt64);
            if (v.Type == DocType.Document && v.AsDocument.TryGetValue("$numberLong", out var nl))
                return DocValue.FromUnixMilliseconds(long.Parse(nl.AsString, CultureInfo.InvariantCulture));
            if (v.Type == DocType.DateTime) return v;
            throw Error("Invalid $date value");
        }

        private DocValue ParseArray()
        {
            if (++_depth > DocumentSerializer.MaxDepth) throw Error("Nesting too deep");
            _pos++; // [
            var arr = new DocArray();
            while (true)
            {
                SkipWs();
                if (TryConsume(']')) break;
                arr.Add(ParseValue());
                SkipWs();
                if (TryConsume(',')) continue;
                Expect(']');
                break;
            }
            _depth--;
            return arr;
        }

        private string ParseString()
        {
            char quote = _s[_pos++];
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _s.Length) throw Error("Unterminated string");
                char c = _s[_pos++];
                if (c == quote) break;
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_pos >= _s.Length) throw Error("Unterminated escape");
                char e = _s[_pos++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (_pos + 4 > _s.Length) throw Error("Invalid unicode escape");
                        sb.Append((char)int.Parse(_s.Slice(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private DocValue ParseNumber()
        {
            int start = _pos;
            if (_s[_pos] is '-' or '+') _pos++;
            if (_s[start..].StartsWith("-Infinity") || _s[start..].StartsWith("+Infinity"))
            {
                _pos = start + 9;
                return DocValue.FromDouble(_s[start] == '-' ? double.NegativeInfinity : double.PositiveInfinity);
            }
            bool isDouble = false;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (char.IsAsciiDigit(c)) _pos++;
                else if (c is '.' or 'e' or 'E')
                {
                    isDouble = true;
                    _pos++;
                    if (c is 'e' or 'E' && _pos < _s.Length && _s[_pos] is '+' or '-') _pos++;
                }
                else break;
            }
            var text = _s[start.._pos];
            if (!isDouble)
            {
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i)) return i;
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw Error($"Invalid number '{text}'");
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';
        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '.';

        private string ParseIdentifier()
        {
            int start = _pos;
            while (_pos < _s.Length && IsIdentPart(_s[_pos])) _pos++;
            return _s[start.._pos].ToString();
        }

        private DocValue ParseIdentifierValue()
        {
            string ident = ParseIdentifier();
            switch (ident)
            {
                case "true": return true;
                case "false": return false;
                case "null":
                case "undefined": return DocValue.Null;
                case "NaN": return double.NaN;
                case "Infinity": return double.PositiveInfinity;
            }
            if (ident == "new") return ParseIdentifierValue();
            Expect('(');
            SkipWs();
            DocValue arg = _s[_pos] == ')' ? DocValue.Null : ParseValue();
            Expect(')');
            switch (ident)
            {
                case "ObjectId":
                    return arg.IsNull ? ObjectId.NewObjectId() : ObjectId.Parse(arg.AsString);
                case "ISODate":
                case "Date":
                    if (arg.IsNull) return DateTime.UtcNow;
                    return ParseDate(arg);
                case "NumberLong":
                    return arg.Type == DocType.String ? long.Parse(arg.AsString, CultureInfo.InvariantCulture) : arg.AsInt64;
                case "NumberInt":
                    return arg.Type == DocType.String ? int.Parse(arg.AsString, CultureInfo.InvariantCulture) : arg.AsInt32;
                case "NumberDouble":
                    return arg.Type == DocType.String ? ParseDoubleSpecial(arg.AsString) : arg.AsDouble;
                case "NumberDecimal":
                    // Pass a string for exactness: NumberDecimal(0.1) goes through a double literal first.
                    return arg.Type == DocType.String ? ParseDecimal(arg.AsString) : DocValue.FromDecimal(arg.AsDecimal);
                case "BinData":
                    return Convert.FromBase64String(arg.AsString);
                default: throw Error($"Unknown function '{ident}'");
            }
        }
    }

    public static string Write(Document doc, bool indented = false) => WriteValue(DocValue.FromDocument(doc), indented);

    public static string WriteValue(DocValue value, bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true }))
            WriteValue(w, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteValue(Utf8JsonWriter w, DocValue v)
    {
        switch (v.Type)
        {
            case DocType.Null: w.WriteNullValue(); break;
            case DocType.Boolean: w.WriteBooleanValue(v.AsBoolean); break;
            case DocType.Int32: w.WriteNumberValue(v.AsInt32); break;
            case DocType.Int64: w.WriteNumberValue(v.AsInt64); break;
            case DocType.Double:
            {
                double d = v.AsDouble;
                if (double.IsFinite(d))
                {
                    string s = d.ToString("R", CultureInfo.InvariantCulture);
                    if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
                    w.WriteRawValue(s, skipInputValidation: true);
                }
                else
                {
                    w.WriteStartObject();
                    w.WriteString("$numberDouble", double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");
                    w.WriteEndObject();
                }
                break;
            }
            case DocType.Decimal:
                w.WriteStartObject();
                w.WriteString("$numberDecimal", v.AsDecimal.ToString(CultureInfo.InvariantCulture));
                w.WriteEndObject();
                break;
            case DocType.String: w.WriteStringValue(v.AsString); break;
            case DocType.ObjectId:
                w.WriteStartObject();
                w.WriteString("$oid", v.AsObjectId.ToString());
                w.WriteEndObject();
                break;
            case DocType.DateTime:
                w.WriteStartObject();
                w.WriteString("$date", v.AsDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                w.WriteEndObject();
                break;
            case DocType.Binary:
                w.WriteStartObject();
                w.WriteBase64String("$binary", v.AsBinary);
                w.WriteEndObject();
                break;
            case DocType.Document:
                w.WriteStartObject();
                foreach (var (k, val) in v.AsDocument)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, val);
                }
                w.WriteEndObject();
                break;
            case DocType.Array:
                w.WriteStartArray();
                foreach (var item in v.AsArray) WriteValue(w, item);
                w.WriteEndArray();
                break;
        }
    }
}
