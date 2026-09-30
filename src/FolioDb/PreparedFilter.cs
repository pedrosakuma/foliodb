using FolioDb.Query;

namespace FolioDb;

/// <summary>
/// An immutable, reusable filter for Find, FindOne, Count, Explain and Visit. Owns its constants and can be
/// shared across collections, databases and threads. It does not cache a query plan or a snapshot: indexes are
/// selected on each execution. Preparing a filter interprets it once; it does not generate runtime code.
/// </summary>
public sealed class PreparedFilter
{
    internal Filter Compiled { get; }

    private PreparedFilter(Filter compiled) => Compiled = compiled;

    /// <summary>Copies and prepares a filter document. Null or empty means all documents.</summary>
    public static PreparedFilter FromDocument(Document? filter) => new(FilterParser.Parse(filter?.Clone()));

    /// <summary>Parses and prepares a JSON filter. Null, whitespace or an empty document means all documents.</summary>
    public static PreparedFilter Parse(string? filter) =>
        new(FilterParser.Parse(string.IsNullOrWhiteSpace(filter) ? null : Document.Parse(filter)));
}
