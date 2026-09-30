namespace FolioDb;

/// <summary>
/// A dotted field path (<c>"address.city"</c>, <c>"tags.0"</c>) with its UTF-8 segments decoded once. Index metadata
/// and parsed filters are both reused across documents, so this costs one allocation per path instead of one per
/// document visited.
/// </summary>
internal sealed class FieldPath
{
    /// <summary>A segment that names a field rather than selecting an array position.</summary>
    public const int NotAPosition = -1;

    public FieldPath(string path)
    {
        Text = path;
        var parts = path.Split('.');
        Segments = new byte[parts.Length][];
        Positions = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            Segments[i] = System.Text.Encoding.UTF8.GetBytes(parts[i]);
            Positions[i] = int.TryParse(parts[i], out int p) && p >= 0 ? p : NotAPosition;
        }
    }

    public string Text { get; }

    /// <summary>UTF-8 bytes of each dot-separated segment.</summary>
    public byte[][] Segments { get; }

    /// <summary>Array position each segment selects, or <see cref="NotAPosition"/> when it is a field name.</summary>
    public int[] Positions { get; }

    public int Length => Segments.Length;

    public override string ToString() => Text;
}
