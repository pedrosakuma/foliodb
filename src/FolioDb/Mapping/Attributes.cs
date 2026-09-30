namespace FolioDb;

/// <summary>
/// Implemented by the FolioDb source generator for types marked with <see cref="FolioDocumentAttribute"/>
/// (the type must be <c>partial</c>). Reflection-free and Native AOT safe.
/// </summary>
public interface IFolioDocument<TSelf> where TSelf : IFolioDocument<TSelf>
{
    static abstract Document ToDocument(TSelf value);
    static abstract TSelf FromDocument(Document document);

    /// <summary>
    /// Builds the entity straight from a borrowed view, without materializing a <see cref="Document"/>. Generated
    /// mappers read the stored bytes in one pass; the default materializes and delegates to <see cref="FromDocument"/>.
    /// Typed collection reads call it inside a borrowed read, so it must not write to the database.
    /// </summary>
    static virtual TSelf FromView(DocumentView view) => TSelf.FromDocument(view.ToDocument());
}

/// <summary>Generates a <see cref="IFolioDocument{TSelf}"/> implementation for this partial class/struct/record.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class FolioDocumentAttribute : Attribute;

/// <summary>Overrides the stored field name of a property.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class FolioFieldAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Excludes a property from mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class FolioIgnoreAttribute : Attribute;

/// <summary>Marks the property mapped to <c>_id</c> (by default a property named <c>Id</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class FolioIdAttribute : Attribute;
