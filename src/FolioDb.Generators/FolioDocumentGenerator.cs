using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace FolioDb.Generators;

/// <summary>
/// Generates reflection-free <c>IFolioDocument&lt;T&gt;</c> implementations (ToDocument / FromDocument) for
/// partial types annotated with <c>[FolioDocument]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class FolioDocumentGenerator : IIncrementalGenerator
{
    private const string AttributeName = "FolioDb.FolioDocumentAttribute";

    private static readonly DiagnosticDescriptor NotPartial = new(
        "FOLIO001", "Type must be partial", "Type '{0}' is marked [FolioDocument] and must be declared partial",
        "FolioDb", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "FOLIO002", "Unsupported member type", "Member '{0}' has type '{1}', which FolioDb cannot map; mark it [FolioIgnore] or use a supported type",
        "FolioDb", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoConstructor = new(
        "FOLIO003", "No usable constructor", "Type '{0}' needs a parameterless constructor or a constructor whose parameters all match mapped properties",
        "FolioDb", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateField = new(
        "FOLIO004", "Duplicate field name", "Field name '{0}' is used by more than one member of '{1}'",
        "FolioDb", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => Build((INamedTypeSymbol)ctx.TargetSymbol, (TypeDeclarationSyntax)ctx.TargetNode, ct));

        context.RegisterSourceOutput(results, static (spc, result) =>
        {
            foreach (var d in result.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
            if (result.Source is not null) spc.AddSource(result.HintName, SourceText.From(result.Source, Encoding.UTF8));
        });
    }

    // ------------------------------------------------------------------ model

    private sealed class Result : IEquatable<Result>
    {
        public string HintName = "";
        public string? Source;
        public List<DiagInfo> Diagnostics = new();

        public bool Equals(Result? other) =>
            other is not null && HintName == other.HintName && Source == other.Source && Diagnostics.SequenceEqual(other.Diagnostics);

        public override bool Equals(object? obj) => Equals(obj as Result);
        public override int GetHashCode() => (HintName.GetHashCode() * 31) ^ (Source?.GetHashCode() ?? 0);
    }

    private sealed class DiagInfo : IEquatable<DiagInfo>
    {
        public DiagnosticDescriptor Descriptor = null!;
        public string[] Args = Array.Empty<string>();
        public string? FilePath;
        public TextSpan Span;
        public LinePositionSpan LineSpan;

        public static DiagInfo Create(DiagnosticDescriptor d, Location? location, params string[] args)
        {
            var info = new DiagInfo { Descriptor = d, Args = args };
            if (location is not null && location.IsInSource)
            {
                info.FilePath = location.SourceTree?.FilePath;
                info.Span = location.SourceSpan;
                info.LineSpan = location.GetLineSpan().Span;
            }
            return info;
        }

        public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor,
            FilePath is null ? Location.None : Location.Create(FilePath, Span, LineSpan), Args);

        public bool Equals(DiagInfo? o) => o is not null && Descriptor.Id == o.Descriptor.Id && FilePath == o.FilePath
            && Span == o.Span && Args.SequenceEqual(o.Args);

        public override bool Equals(object? obj) => Equals(obj as DiagInfo);
        public override int GetHashCode() => Descriptor.Id.GetHashCode() ^ Span.GetHashCode();
    }

    private sealed class Member
    {
        public string Name = "";
        public string Field = "";
        public ITypeSymbol Type = null!;
        public bool IsId;
        public bool CanSet;     // settable after construction
        public bool InitOnly;   // init accessor or required
        public Location? Location;
    }

    // ------------------------------------------------------------------ analysis

    private static Result Build(INamedTypeSymbol type, TypeDeclarationSyntax syntax, System.Threading.CancellationToken ct)
    {
        var result = new Result { HintName = HintNameFor(type) };
        var location = syntax.Identifier.GetLocation();

        for (var t = type; t is not null; t = t.ContainingType)
        {
            bool isPartial = t.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(ct) is TypeDeclarationSyntax s && s.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!isPartial)
            {
                result.Diagnostics.Add(DiagInfo.Create(NotPartial, location, t.Name));
                return result;
            }
        }

        var members = new List<Member>();
        foreach (var symbol in AllInstanceMembers(type))
        {
            if (HasAttribute(symbol, "FolioIgnoreAttribute")) continue;
            if (symbol is not IPropertySymbol p) continue;
            if (p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public || p.GetMethod is null) continue;
            if (p.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
            bool hasPublicSetter = p.SetMethod is { DeclaredAccessibility: Accessibility.Public };
            bool isInit = p.SetMethod is { IsInitOnly: true };
            var fieldAttr = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "FolioFieldAttribute");
            string field = fieldAttr?.ConstructorArguments.FirstOrDefault().Value as string ?? CamelCase(p.Name);
            bool isId = HasAttribute(p, "FolioIdAttribute");
            members.Add(new Member
            {
                Name = p.Name,
                Field = field,
                Type = p.Type,
                IsId = isId,
                CanSet = hasPublicSetter && !isInit,
                InitOnly = hasPublicSetter && isInit || p.IsRequired,
                Location = p.Locations.FirstOrDefault(),
            });
        }

        if (!members.Any(m => m.IsId))
        {
            var id = members.FirstOrDefault(m => m.Name == "Id") ?? members.FirstOrDefault(m => m.Name == "_id");
            if (id is not null) id.IsId = true;
        }
        foreach (var m in members.Where(m => m.IsId)) m.Field = "_id";

        // Constructor selection: prefer parameterless, else the public ctor with most parameters that all match members.
        var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        IMethodSymbol? ctor = ctors.FirstOrDefault(c => c.Parameters.Length == 0);
        var ctorArgs = new List<Member>();
        if (ctor is null)
        {
            foreach (var c in ctors.OrderByDescending(c => c.Parameters.Length))
            {
                var args = c.Parameters.Select(prm => members.FirstOrDefault(m =>
                    string.Equals(m.Name, prm.Name, StringComparison.OrdinalIgnoreCase)
                    && SymbolEqualityComparer.Default.Equals(m.Type.WithNullableAnnotation(NullableAnnotation.None), prm.Type.WithNullableAnnotation(NullableAnnotation.None)))).ToList();
                if (args.All(a => a is not null))
                {
                    ctor = c;
                    ctorArgs = args!;
                    break;
                }
            }
        }
        if (ctor is null && !type.IsValueType)
        {
            result.Diagnostics.Add(DiagInfo.Create(NoConstructor, location, type.Name));
            return result;
        }

        // Get-only properties are mapped only when a constructor parameter populates them (computed properties are skipped).
        members.RemoveAll(m => !m.CanSet && !m.InitOnly && !ctorArgs.Contains(m));
        foreach (var dup in members.GroupBy(m => m.Field).Where(g => g.Count() > 1))
        {
            result.Diagnostics.Add(DiagInfo.Create(DuplicateField, dup.First().Location ?? location, dup.Key, type.Name));
            return result;
        }

        bool ok = true;
        foreach (var m in members)
        {
            if (!IsSupported(m.Type, 0))
            {
                result.Diagnostics.Add(DiagInfo.Create(UnsupportedType, m.Location ?? location, m.Name, m.Type.ToDisplayString()));
                ok = false;
            }
        }
        if (!ok) return result;

        result.Source = Emit(type, syntax, members, ctorArgs);
        return result;
    }

    private static IEnumerable<ISymbol> AllInstanceMembers(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
            chain.Insert(0, t);
        var seen = new HashSet<string>();
        var perType = new List<List<ISymbol>>();
        // Most-derived declarations win (handles 'new' hiding and overrides); output is base-to-derived.
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var own = new List<ISymbol>();
            foreach (var m in chain[i].GetMembers())
                if (!m.IsStatic && m is IPropertySymbol && seen.Add(m.Name)) own.Add(m);
            perType.Insert(0, own);
        }
        return perType.SelectMany(l => l);
    }

    private static bool HasAttribute(ISymbol s, string name) =>
        s.GetAttributes().Any(a => a.AttributeClass?.Name == name && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "FolioDb");

    private static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static string HintNameFor(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        var sb = new StringBuilder();
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
        return sb + ".FolioDocument.g.cs";
    }

    // ------------------------------------------------------------------ type support

    private enum Kind { Unsupported, Simple, Nullable, Enum, Nested, List, Array, HashSet, Map }

    private static Kind Classify(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            element = n.TypeArguments[0];
            return Kind.Nullable;
        }
        if (SimpleWrite(type, "x") is not null) return Kind.Simple;
        if (type.TypeKind == TypeKind.Enum) return Kind.Enum;
        if (type is IArrayTypeSymbol { Rank: 1 } arr)
        {
            element = arr.ElementType;
            return Kind.Array;
        }
        if (type is INamedTypeSymbol named)
        {
            if (named.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "FolioDb.IFolioDocument<TSelf>")
                || named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == AttributeName))
                return Kind.Nested;
            if (named.IsGenericType)
            {
                string def = named.OriginalDefinition.ToDisplayString();
                switch (def)
                {
                    case "System.Collections.Generic.List<T>":
                    case "System.Collections.Generic.IList<T>":
                    case "System.Collections.Generic.IReadOnlyList<T>":
                    case "System.Collections.Generic.ICollection<T>":
                    case "System.Collections.Generic.IReadOnlyCollection<T>":
                    case "System.Collections.Generic.IEnumerable<T>":
                        element = named.TypeArguments[0];
                        return Kind.List;
                    case "System.Collections.Generic.HashSet<T>":
                    case "System.Collections.Generic.ISet<T>":
                    case "System.Collections.Generic.IReadOnlySet<T>":
                        element = named.TypeArguments[0];
                        return Kind.HashSet;
                    case "System.Collections.Generic.Dictionary<TKey, TValue>":
                    case "System.Collections.Generic.IDictionary<TKey, TValue>":
                    case "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>":
                        if (named.TypeArguments[0].SpecialType == SpecialType.System_String)
                        {
                            element = named.TypeArguments[1];
                            return Kind.Map;
                        }
                        break;
                }
            }
        }
        return Kind.Unsupported;
    }

    private static bool IsSupported(ITypeSymbol type, int depth)
    {
        if (depth > 8) return false;
        var kind = Classify(type, out var element);
        return kind switch
        {
            Kind.Unsupported => false,
            Kind.Nullable or Kind.List or Kind.Array or Kind.HashSet or Kind.Map => IsSupported(element!, depth + 1),
            _ => true,
        };
    }

    private static string Fq(ITypeSymbol t) => t.ToDisplayString(TypeFormat);

    /// <summary>Expression converting <paramref name="x"/> of a simple type into a DocValue, or null if not simple.</summary>
    private static string? SimpleWrite(ITypeSymbol type, string x)
    {
        const string DV = "global::FolioDb.DocValue";
        const string M = "global::FolioDb.Mapping.FolioMapper";
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return $"{DV}.FromBoolean({x})";
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32: return $"{DV}.FromInt32({x})";
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64: return $"{DV}.FromInt64({x})";
            case SpecialType.System_UInt64: return $"{M}.WriteUInt64({x})";
            case SpecialType.System_Single:
            case SpecialType.System_Double: return $"{DV}.FromDouble({x})";
            case SpecialType.System_Decimal: return $"{M}.WriteDecimal({x})";
            case SpecialType.System_Char: return $"{M}.WriteChar({x})";
            case SpecialType.System_String: return $"{DV}.FromString({x})";
            case SpecialType.System_DateTime: return $"{DV}.FromDateTime({x})";
        }
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 }) return $"{DV}.FromBinary({x})";
        switch (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            case "global::System.Guid": return $"{M}.WriteGuid({x})";
            case "global::System.DateTimeOffset": return $"{M}.WriteDateTimeOffset({x})";
            case "global::System.DateOnly": return $"{M}.WriteDateOnly({x})";
            case "global::System.TimeSpan": return $"{M}.WriteTimeSpan({x})";
            case "global::FolioDb.ObjectId": return $"{DV}.FromObjectId({x})";
            case "global::FolioDb.Document": return $"{DV}.FromDocument({x})";
            case "global::FolioDb.DocArray": return $"{DV}.FromArray({x})";
            case "global::FolioDb.DocValue": return x;
        }
        return null;
    }

    private static string? SimpleRead(ITypeSymbol type, string v)
    {
        const string M = "global::FolioDb.Mapping.FolioMapper";
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return $"{v}.AsBoolean";
            case SpecialType.System_Byte: return $"checked((byte){v}.AsInt32)";
            case SpecialType.System_SByte: return $"checked((sbyte){v}.AsInt32)";
            case SpecialType.System_Int16: return $"checked((short){v}.AsInt32)";
            case SpecialType.System_UInt16: return $"checked((ushort){v}.AsInt32)";
            case SpecialType.System_Int32: return $"{v}.AsInt32";
            case SpecialType.System_UInt32: return $"checked((uint){v}.AsInt64)";
            case SpecialType.System_Int64: return $"{v}.AsInt64";
            case SpecialType.System_UInt64: return $"{M}.ReadUInt64({v})";
            case SpecialType.System_Single: return $"(float){v}.AsDouble";
            case SpecialType.System_Double: return $"{v}.AsDouble";
            case SpecialType.System_Decimal: return $"{M}.ReadDecimal({v})";
            case SpecialType.System_Char: return $"{M}.ReadChar({v})";
            case SpecialType.System_String: return $"({v}.IsNull ? null! : {v}.AsString)";
            case SpecialType.System_DateTime: return $"{v}.AsDateTime";
        }
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 }) return $"({v}.IsNull ? null! : {v}.AsBinary)";
        switch (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            case "global::System.Guid": return $"{M}.ReadGuid({v})";
            case "global::System.DateTimeOffset": return $"{M}.ReadDateTimeOffset({v})";
            case "global::System.DateOnly": return $"{M}.ReadDateOnly({v})";
            case "global::System.TimeSpan": return $"{M}.ReadTimeSpan({v})";
            case "global::FolioDb.ObjectId": return $"{v}.AsObjectId";
            case "global::FolioDb.Document": return $"({v}.IsNull ? null! : {v}.AsDocument)";
            case "global::FolioDb.DocArray": return $"({v}.IsNull ? null! : {v}.AsArray)";
            case "global::FolioDb.DocValue": return v;
        }
        return null;
    }

    private static string WriteExpr(ITypeSymbol type, string x, int depth, string p)
    {
        const string M = "global::FolioDb.Mapping.FolioMapper";
        var kind = Classify(type, out var el);
        string e = p + "e" + depth;
        switch (kind)
        {
            case Kind.Simple: return SimpleWrite(type, x)!;
            case Kind.Nullable: return $"({x} is {{ }} {e} ? {WriteExpr(el!, e, depth + 1, p)} : global::FolioDb.DocValue.Null)";
            case Kind.Enum:
                var under = ((INamedTypeSymbol)type).EnumUnderlyingType!;
                return under.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_UInt32
                    ? $"global::FolioDb.DocValue.FromInt64((long){x})"
                    : $"global::FolioDb.DocValue.FromInt32((int){x})";
            case Kind.Nested:
                return type.IsValueType
                    ? $"global::FolioDb.DocValue.FromDocument({Fq(type)}.ToDocument({x}))"
                    : $"({x} is null ? global::FolioDb.DocValue.Null : global::FolioDb.DocValue.FromDocument({Fq(type).TrimEnd('?')}.ToDocument({x})))";
            case Kind.List:
            case Kind.Array:
            case Kind.HashSet:
                return $"{M}.WriteArray({x}, static {e} => {WriteExpr(el!, e, depth + 1, p)})";
            case Kind.Map:
                return $"{M}.WriteMap({x}, static {e} => {WriteExpr(el!, e, depth + 1, p)})";
        }
        throw new InvalidOperationException();
    }

    private static string ReadExpr(ITypeSymbol type, string v, int depth, string p)
    {
        const string M = "global::FolioDb.Mapping.FolioMapper";
        var kind = Classify(type, out var el);
        string e = p + "e" + depth;
        switch (kind)
        {
            case Kind.Simple: return SimpleRead(type, v)!;
            case Kind.Nullable: return $"({v}.IsNull ? default({Fq(type)}) : {ReadExpr(el!, v, depth + 1, p)})";
            case Kind.Enum:
                var under = ((INamedTypeSymbol)type).EnumUnderlyingType!;
                return under.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_UInt32
                    ? $"({Fq(type)}){v}.AsInt64"
                    : $"({Fq(type)}){v}.AsInt32";
            case Kind.Nested:
                string t = Fq(type).TrimEnd('?');
                return type.IsValueType
                    ? $"{t}.FromDocument({v}.AsDocument)"
                    : $"({v}.IsNull ? null! : {t}.FromDocument({v}.AsDocument))";
            case Kind.List: return $"{M}.ReadList({v}, static {e} => {ReadExpr(el!, e, depth + 1, p)})";
            case Kind.Array: return $"{M}.ReadArray({v}, static {e} => {ReadExpr(el!, e, depth + 1, p)})";
            case Kind.HashSet: return $"{M}.ReadHashSet({v}, static {e} => {ReadExpr(el!, e, depth + 1, p)})";
            case Kind.Map: return $"{M}.ReadMap({v}, static {e} => {ReadExpr(el!, e, depth + 1, p)})";
        }
        throw new InvalidOperationException();
    }

    /// <summary>Like <see cref="SimpleRead"/>, over a borrowed DocValueView; conversions are the same.</summary>
    private static string? ViewSimpleRead(ITypeSymbol type, string v)
    {
        const string M = "global::FolioDb.Mapping.FolioMapper";
        switch (type.SpecialType)
        {
            case SpecialType.System_UInt64: return $"{M}.ReadUInt64({v})";
            case SpecialType.System_Decimal: return $"{M}.ReadDecimal({v})";
            case SpecialType.System_Char: return $"{M}.ReadChar({v})";
            case SpecialType.System_String: return $"({v}.IsNull ? null! : {v}.GetString())";
        }
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 }) return $"({v}.IsNull ? null! : {v}.AsBinary.ToArray())";
        switch (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
        {
            case "global::FolioDb.Document": return $"({v}.IsNull ? null! : {v}.AsDocument.ToDocument())";
            case "global::FolioDb.DocArray": return $"({v}.IsNull ? null! : {v}.AsArray.ToArray())";
            case "global::FolioDb.DocValue": return $"{v}.ToDocValue()";
        }
        // Remaining simple types use accessors/helpers with identical names on DocValueView.
        return SimpleRead(type, v);
    }

    /// <summary>
    /// Read expression over a DocValueView. Views are ref structs and cannot be captured by lambdas, so collections
    /// are read by static local functions appended to <paramref name="funcs"/>.
    /// </summary>
    private static string ViewReadExpr(ITypeSymbol type, string v, int depth, string p, StringBuilder funcs, string ind)
    {
        const string M = "global::FolioDb.Mapping.FolioMapper";
        const string DVV = "global::FolioDb.DocValueView";
        var kind = Classify(type, out var el);
        string e = p + "e" + depth, fn = p + "read" + depth;
        string i1 = ind + "    ";
        switch (kind)
        {
            case Kind.Simple: return ViewSimpleRead(type, v)!;
            case Kind.Nullable: return $"({v}.IsNull ? default({Fq(type)}) : {ViewReadExpr(el!, v, depth + 1, p, funcs, ind)})";
            case Kind.Enum:
                var under = ((INamedTypeSymbol)type).EnumUnderlyingType!;
                return under.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_UInt32
                    ? $"({Fq(type)}){v}.AsInt64"
                    : $"({Fq(type)}){v}.AsInt32";
            case Kind.Nested:
                string t = Fq(type).TrimEnd('?');
                return type.IsValueType
                    ? $"{M}.ReadNested<{t}>({v}.AsDocument)"
                    : $"({v}.IsNull ? null! : {M}.ReadNested<{t}>({v}.AsDocument))";
            case Kind.List:
            case Kind.Array:
            case Kind.HashSet:
            {
                string et = Fq(el!);
                string item = ViewReadExpr(el!, e, depth + 1, p, funcs, ind);
                string ret = kind switch
                {
                    Kind.List => $"global::System.Collections.Generic.List<{et}>",
                    Kind.HashSet => $"global::System.Collections.Generic.HashSet<{et}>",
                    _ => et + "[]",
                };
                funcs.Append(ind).Append("static ").Append(ret).Append(' ').Append(fn).Append('(').Append(DVV).AppendLine(" v)");
                funcs.Append(ind).AppendLine("{");
                funcs.Append(i1).AppendLine("if (v.IsNull) return null!;");
                funcs.Append(i1).AppendLine("var items = v.AsArray;");
                if (kind == Kind.Array)
                {
                    funcs.Append(i1).Append("var result = ").Append(NewArray(et, "items.Count")).AppendLine(";");
                    funcs.Append(i1).AppendLine("int i = 0;");
                    funcs.Append(i1).Append("foreach (var ").Append(e).Append(" in items) result[i++] = ").Append(item).AppendLine(";");
                }
                else
                {
                    funcs.Append(i1).Append("var result = new ").Append(ret).AppendLine("(items.Count);");
                    funcs.Append(i1).Append("foreach (var ").Append(e).Append(" in items) result.Add(").Append(item).AppendLine(");");
                }
                funcs.Append(i1).AppendLine("return result;");
                funcs.Append(ind).AppendLine("}");
                return $"{fn}({v})";
            }
            case Kind.Map:
            {
                string et = Fq(el!);
                string item = ViewReadExpr(el!, e + ".Value", depth + 1, p, funcs, ind);
                string ret = $"global::System.Collections.Generic.Dictionary<string, {et}>";
                funcs.Append(ind).Append("static ").Append(ret).Append(' ').Append(fn).Append('(').Append(DVV).AppendLine(" v)");
                funcs.Append(ind).AppendLine("{");
                funcs.Append(i1).AppendLine("if (v.IsNull) return null!;");
                funcs.Append(i1).AppendLine("var fields = v.AsDocument;");
                funcs.Append(i1).Append("var result = new ").Append(ret).AppendLine("(fields.FieldCount);");
                funcs.Append(i1).Append("foreach (var ").Append(e).Append(" in fields) result[").Append(e).Append(".GetName()] = ").Append(item).AppendLine(";");
                funcs.Append(i1).AppendLine("return result;");
                funcs.Append(ind).AppendLine("}");
                return $"{fn}({v})";
            }
        }
        throw new InvalidOperationException();
    }

    /// <summary>Allocation expression for a 1-D array of <paramref name="elementType"/>, which may itself be an array type.</summary>
    private static string NewArray(string elementType, string length)
    {
        int bracket = elementType.IndexOf('[', elementType.LastIndexOf('>') + 1);
        return bracket < 0
            ? $"new {elementType}[{length}]"
            : $"new {elementType.Substring(0, bracket)}[{length}]{elementType.Substring(bracket).Replace("?", "")}";
    }

    /// <summary>For _id members: a condition under which the id is "unset" and should be generated by the database.</summary>
    private static string? UnsetIdCondition(ITypeSymbol type, string x)
    {
        if (type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::FolioDb.ObjectId") return $"{x} == default(global::FolioDb.ObjectId)";
        if (type.SpecialType == SpecialType.System_String) return $"{x} is null";
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) return $"{x} is null";
        if (!type.IsValueType) return $"{x} is null";
        return null;
    }

    // ------------------------------------------------------------------ emit

    private static string Emit(INamedTypeSymbol type, TypeDeclarationSyntax syntax, List<Member> members, List<Member> ctorArgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by FolioDb.Generators");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS8601, CS8603, CS8604, CS8619, CS0618");
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            sb.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
        }
        sb.AppendLine();

        var containers = new List<INamedTypeSymbol>();
        for (var c = type.ContainingType; c is not null; c = c.ContainingType) containers.Insert(0, c);
        int indentLevel = 0;
        foreach (var c in containers)
        {
            sb.Append(Indent(indentLevel)).Append("partial ").Append(Keyword(c)).Append(' ').Append(NameWithTypeParams(c)).AppendLine();
            sb.Append(Indent(indentLevel)).AppendLine("{");
            indentLevel++;
        }

        string self = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string ind = Indent(indentLevel);
        sb.Append(ind).Append("partial ").Append(Keyword(type)).Append(' ').Append(NameWithTypeParams(type))
          .Append(" : global::FolioDb.IFolioDocument<").Append(self).AppendLine(">");
        sb.Append(ind).AppendLine("{");
        string i1 = ind + "    ", i2 = i1 + "    ", i3 = i2 + "    ";

        // ToDocument
        sb.Append(i1).Append("public static global::FolioDb.Document ToDocument(").Append(self).AppendLine(" value)");
        sb.Append(i1).AppendLine("{");
        if (!type.IsValueType) sb.Append(i2).AppendLine("global::System.ArgumentNullException.ThrowIfNull(value);");
        sb.Append(i2).Append("var doc = new global::FolioDb.Document(").Append(members.Count).AppendLine(");");
        int w = 0;
        foreach (var m in members.OrderBy(m => m.IsId ? 0 : 1))
        {
            string access = "value." + m.Name;
            string? unset = m.IsId ? UnsetIdCondition(m.Type, access) : null;
            string add = $"doc.Set({Literal(m.Field)}, {WriteExpr(m.Type, access, 0, "w" + w++ + "_")});";
            if (unset is not null) sb.Append(i2).Append("if (!(").Append(unset).Append(")) ").AppendLine(add);
            else sb.Append(i2).AppendLine(add);
        }
        sb.Append(i2).AppendLine("return doc;");
        sb.Append(i1).AppendLine("}");
        sb.AppendLine();

        // FromDocument
        sb.Append(i1).Append("public static ").Append(self).AppendLine(" FromDocument(global::FolioDb.Document document)");
        sb.Append(i1).AppendLine("{");
        sb.Append(i2).AppendLine("global::System.ArgumentNullException.ThrowIfNull(document);");
        int idx = 0;
        var local = new Dictionary<Member, string>();
        foreach (var m in members)
        {
            string name = "v" + idx++;
            local[m] = name;
            sb.Append(i2).Append("bool has_").Append(name).Append(" = document.TryGetValue(").Append(Literal(m.Field)).Append(", out var ").Append(name).AppendLine(");");
        }
        string Value(Member m) => $"(has_{local[m]} ? {ReadExpr(m.Type, local[m], 0, "r" + local[m] + "_")} : default!)";

        var inCtor = new HashSet<Member>(ctorArgs);
        sb.Append(i2).Append("var result = new ").Append(self).Append('(')
          .Append(string.Join(", ", ctorArgs.Select(Value))).Append(')');
        var initMembers = members.Where(m => !inCtor.Contains(m) && m.InitOnly).ToList();
        if (initMembers.Count > 0)
        {
            sb.AppendLine();
            sb.Append(i2).AppendLine("{");
            foreach (var m in initMembers) sb.Append(i3).Append(m.Name).Append(" = ").Append(Value(m)).AppendLine(",");
            sb.Append(i2).Append('}');
        }
        sb.AppendLine(";");
        foreach (var m in members.Where(m => !inCtor.Contains(m) && !m.InitOnly && m.CanSet))
            sb.Append(i2).Append("if (has_").Append(local[m]).Append(") result.").Append(m.Name).Append(" = ").Append(ReadExpr(m.Type, local[m], 0, "r" + local[m] + "_")).AppendLine(";");
        sb.Append(i2).AppendLine("return result;");
        sb.Append(i1).AppendLine("}");
        sb.AppendLine();

        EmitFromView(sb, self, members, ctorArgs, i1);

        sb.Append(ind).AppendLine("}");
        for (int i = containers.Count - 1; i >= 0; i--) sb.Append(Indent(i)).AppendLine("}");
        return sb.ToString();
    }

    private static string Indent(int level) => new(' ', level * 4);

    /// <summary>
    /// Single pass over the stored fields, matching names as UTF-8 bytes (grouped by length). Like FromDocument's
    /// TryGetValue, the first occurrence of a duplicated name wins; unmapped fields are skipped without decoding.
    /// </summary>
    private static void EmitFromView(StringBuilder sb, string self, List<Member> members, List<Member> ctorArgs, string i1)
    {
        string i2 = i1 + "    ", i3 = i2 + "    ", i4 = i3 + "    ", i5 = i4 + "    ";
        var funcs = new StringBuilder();
        sb.Append(i1).AppendLine("/// <summary>Reads the entity directly from a borrowed view (valid only during the calling callback).</summary>");
        sb.Append(i1).Append("public static ").Append(self).AppendLine(" FromView(global::FolioDb.DocumentView view)");
        sb.Append(i1).AppendLine("{");
        var local = new Dictionary<Member, string>();
        int idx = 0;
        foreach (var m in members)
        {
            string name = "v" + idx++;
            local[m] = name;
            sb.Append(i2).Append("bool has_").Append(name).Append(" = false; scoped global::FolioDb.DocValueView ").Append(name).AppendLine(" = default;");
        }
        if (members.Count > 0)
        {
            sb.Append(i2).AppendLine("foreach (var field in view)");
            sb.Append(i2).AppendLine("{");
            sb.Append(i3).AppendLine("var name = field.Utf8Name;");
            sb.Append(i3).AppendLine("switch (name.Length)");
            sb.Append(i3).AppendLine("{");
            foreach (var group in members.GroupBy(m => Encoding.UTF8.GetByteCount(m.Field)).OrderBy(g => g.Key))
            {
                sb.Append(i4).Append("case ").Append(group.Key).AppendLine(":");
                bool first = true;
                foreach (var m in group)
                {
                    string v = local[m];
                    sb.Append(i5).Append(first ? "if" : "else if").Append(" (global::System.MemoryExtensions.SequenceEqual(name, ")
                      .Append(Literal(m.Field)).Append("u8)) { if (!has_").Append(v).Append(") { has_").Append(v).Append(" = true; ")
                      .Append(v).AppendLine(" = field.Value; } }");
                    first = false;
                }
                sb.Append(i5).AppendLine("break;");
            }
            sb.Append(i3).AppendLine("}");
            sb.Append(i2).AppendLine("}");
        }

        string Read(Member m) => ViewReadExpr(m.Type, local[m], 0, "r" + local[m] + "_", funcs, i2);
        string Value(Member m) => $"(has_{local[m]} ? {Read(m)} : default!)";

        var inCtor = new HashSet<Member>(ctorArgs);
        sb.Append(i2).Append("var result = new ").Append(self).Append('(')
          .Append(string.Join(", ", ctorArgs.Select(Value))).Append(')');
        var initMembers = members.Where(m => !inCtor.Contains(m) && m.InitOnly).ToList();
        if (initMembers.Count > 0)
        {
            sb.AppendLine();
            sb.Append(i2).AppendLine("{");
            foreach (var m in initMembers) sb.Append(i3).Append(m.Name).Append(" = ").Append(Value(m)).AppendLine(",");
            sb.Append(i2).Append('}');
        }
        sb.AppendLine(";");
        foreach (var m in members.Where(m => !inCtor.Contains(m) && !m.InitOnly && m.CanSet))
            sb.Append(i2).Append("if (has_").Append(local[m]).Append(") result.").Append(m.Name).Append(" = ").Append(Read(m)).AppendLine(";");
        sb.Append(i2).AppendLine("return result;");
        if (funcs.Length > 0)
        {
            sb.AppendLine();
            sb.Append(funcs);
        }
        sb.Append(i1).AppendLine("}");
    }

    private static string Keyword(INamedTypeSymbol t)
    {
        if (t.IsRecord) return t.IsValueType ? "record struct" : "record";
        return t.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    private static string NameWithTypeParams(INamedTypeSymbol t) =>
        t.TypeParameters.Length == 0 ? t.Name : t.Name + "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);
}
