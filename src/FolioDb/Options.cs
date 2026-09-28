namespace FolioDb;

public sealed class FindOptions
{
    /// <summary>Sort specification, e.g. <c>{ age: -1, name: 1 }</c>.</summary>
    public Document? Sort { get; init; }
    /// <summary>Projection, e.g. <c>{ name: 1 }</c> (inclusion) or <c>{ password: 0 }</c> (exclusion).</summary>
    public Document? Projection { get; init; }
    public int Skip { get; init; }
    public int? Limit { get; init; }
}

public readonly record struct UpdateResult(long MatchedCount, long ModifiedCount, DocValue? UpsertedId);

public sealed record IndexInfo(string Name, string Field, bool Unique, bool MultiKey);

public sealed record DatabaseStats(
    int PageSize,
    long PageCount,
    long FreePages,
    long WalFrames,
    long DatabaseFileBytes,
    long WalFileBytes,
    IReadOnlyList<string> Collections);
