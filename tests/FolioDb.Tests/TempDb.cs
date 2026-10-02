namespace FolioDb.Tests;

/// <summary>Creates a unique database path and deletes the files afterwards.</summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "folio-tests", Guid.NewGuid().ToString("N") + ".folio");

    public TempDb() => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

    public FolioDatabase Open(FolioOptions? options = null) =>
        FolioDatabase.Open(Path, options ?? new FolioOptions { Synchronous = SynchronousMode.Off });

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
            File.Delete(Path + "-wal");
            File.Delete(Path + "-wal2");
        }
        catch (IOException) { }
    }
}
