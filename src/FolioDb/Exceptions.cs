namespace FolioDb;

/// <summary>Base exception for all FolioDb errors.</summary>
public class FolioException : Exception
{
    public FolioException(string message) : base(message) { }
    public FolioException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when an insert/update violates a unique constraint (primary key or unique index).</summary>
public sealed class DuplicateKeyException : FolioException
{
    public DuplicateKeyException(string message) : base(message) { }
}

/// <summary>Thrown when the database file or WAL is corrupted or has an unsupported format.</summary>
public sealed class CorruptDatabaseException : FolioException
{
    public CorruptDatabaseException(string message) : base(message) { }
}
