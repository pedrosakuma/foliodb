namespace FolioDb.Storage;

internal sealed record BTreeStorageDiagnostics(
    uint RootPage,
    int Height,
    long EntryCount,
    long LeafPages,
    long InteriorPages,
    long OverflowPages,
    long LeafLiveBytes,
    long LeafCellBytes,
    long LeafFragmentedBytes,
    long LeafFreeBytes,
    long InteriorLiveBytes,
    long InteriorCellBytes,
    long InteriorFragmentedBytes,
    long InteriorFreeBytes,
    long OverflowLiveBytes,
    long OverflowPayloadBytes,
    long OverflowFreeBytes,
    long AllocatedPageRuns,
    long AllocatedPageSpan,
    long MaximumPageGap);

internal enum StorageTreeKind
{
    Catalog,
    Primary,
    Secondary,
}

internal sealed record TreeStorageDiagnostics(
    string Name,
    StorageTreeKind Kind,
    string? Collection,
    string? Index,
    bool MultiKey,
    BTreeStorageDiagnostics Tree);

internal sealed record DatabaseStorageDiagnostics(
    DatabaseStats Database,
    IReadOnlyList<TreeStorageDiagnostics> Trees);
