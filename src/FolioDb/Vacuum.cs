using FolioDb.Engine;

namespace FolioDb;

/// <summary>Observable stages of <see cref="FolioDatabase.VacuumInto"/> used by deterministic tests.</summary>
internal enum VacuumStage
{
    SnapshotStarted,
    CopyCompleted,
    BeforePublish,
    Published,
}

internal static class Vacuum
{
    public static void Into(FolioDatabase source, string destination, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();

        string target = Path.GetFullPath(destination);
        ValidateDestination(source.Path, target);
        string stage = ReserveStage(target);
        bool published = false;
        try
        {
            using (var snapshot = source.BeginRead())
            {
                source.TestVacuumStage?.Invoke(VacuumStage.SnapshotStarted);
                var options = new FolioOptions
                {
                    PageSize = source.Pager.PageSize,
                    CacheSizePages = source.Options.CacheSizePages,
                    AutoCheckpointFrames = 0,
                    Synchronous = SynchronousMode.Full,
                    WriterAdmission = source.Options.WriterAdmission,
                    BusyTimeout = source.Options.BusyTimeout,
                    DeleteRebalance = source.Options.DeleteRebalance,
                };
                using (var output = FolioDatabase.Open(stage, options))
                {
                    using (var tx = output.BeginTransaction())
                    {
                        foreach (var name in snapshot.ListCollections())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var meta = snapshot.GetCollection(name)!;
                            CollectionEngine.CopyForVacuum(snapshot, meta, tx.Engine, cancellationToken);
                        }
                        tx.Commit();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!output.Checkpoint())
                        throw new FolioException("Vacuum output checkpoint was blocked unexpectedly.");
                }
            }

            source.TestVacuumStage?.Invoke(VacuumStage.CopyCompleted);
            cancellationToken.ThrowIfCancellationRequested();
            source.TestVacuumStage?.Invoke(VacuumStage.BeforePublish);
            File.Move(stage, target, overwrite: false);
            published = true;
            DeleteOwned(stage + "-wal");
            DeleteOwned(stage + "-wal2");
            source.TestVacuumStage?.Invoke(VacuumStage.Published);
        }
        finally
        {
            if (!published)
            {
                DeleteOwned(stage);
                DeleteOwned(stage + "-wal");
                DeleteOwned(stage + "-wal2");
            }
        }
    }

    private static void ValidateDestination(string source, string destination)
    {
        string sourcePath = Path.GetFullPath(source);
        if (PathEquals(destination, sourcePath) || PathEquals(destination, sourcePath + "-wal") || PathEquals(destination, sourcePath + "-wal2"))
            throw new ArgumentException("Vacuum destination must not be the source database or its WAL.", nameof(destination));
        var entry = new FileInfo(destination);
        if (entry.Exists || Directory.Exists(destination) || entry.LinkTarget is not null)
            throw new IOException($"Vacuum destination '{destination}' already exists.");
    }

    private static string ReserveStage(string destination)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();
        string fileName = Path.GetFileName(destination);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string stage = Path.Combine(directory, $".{fileName}.vacuum-{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.RandomAccess,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var _ = new FileStream(stage, options);
                return stage;
            }
            catch (IOException) when (attempt < 31)
            {
            }
        }
        throw new IOException("Could not reserve a private vacuum staging file.");
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteOwned(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
