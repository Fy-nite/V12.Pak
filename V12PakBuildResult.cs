namespace V12.Pak;

/// <summary>
/// What a pack produced: the file to ship (one pak or the part files in order)
/// and, for a split build, each part's size and SHA-256.
/// </summary>
public sealed record V12PakBuildResult(
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, V12PakPartInfo> PartInfo)
{
    /// <summary>True when the archive was written as numbered parts.</summary>
    public bool IsSplit => Files.Count > 1;

    /// <summary>Result for an unsplit archive.</summary>
    public static V12PakBuildResult Single(string path)
        => new(new[] { Path.GetFullPath(path) }, EmptyPartInfo);

    private static readonly IReadOnlyDictionary<string, V12PakPartInfo> EmptyPartInfo
        = new Dictionary<string, V12PakPartInfo>();
}
