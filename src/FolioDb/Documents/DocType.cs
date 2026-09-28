namespace FolioDb;

/// <summary>Value types supported by the FolioDb binary document format. Numeric values are the on-disk tags.</summary>
public enum DocType : byte
{
    Double = 0x01,
    String = 0x02,
    Document = 0x03,
    Array = 0x04,
    Binary = 0x05,
    ObjectId = 0x07,
    Boolean = 0x08,
    DateTime = 0x09,
    Null = 0x0A,
    Int32 = 0x10,
    Int64 = 0x12,
}
