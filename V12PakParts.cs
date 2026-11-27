using System.Text.RegularExpressions;

namespace V12.Pak;

/// <summary>
/// Naming and discovery for a pak split across several files.
///
/// A split build writes <c>MyGame.part001.v12pak</c>, <c>.part002</c>, ... and the
/// parts are the byte-for-byte concatenation of one archive. The reader joins
/// them into a single logical stream, so nothing else in the loader changes.
///
/// Parts keep the <c>.v12pak</c> extension so existing filters (Godot's
/// <c>include_filter</c>, deployment copies) still pick them up. A part is only
/// a part by name; discovery must never treat one as a standalone pak.
/// </summary>
public static class V12PakParts
{
    public const string Extension = ".v12pak";
    public const string PartInfix = ".part";

    private static readonly Regex PartPattern = new(
        @"^(?<stem>.+)\.part(?<index>\d{3,})\.v12pak$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Path of part <paramref name="index"/> (1-based) for a logical pak path.</summary>
    public static string PartPath(string logicalPakPath, int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));

        string full = Path.GetFullPath(logicalPakPath);
        string directory = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(directory, $"{stem}{PartInfix}{index:D3}{Extension}");
    }

    /// <summary>True when <paramref name="path"/> is a split part rather than a whole pak.</summary>
    public static bool IsPartFile(string path)
        => TryParsePart(Path.GetFileName(path), out _, out _);

    /// <summary>
    /// The whole-pak path a part belongs to. Returns <paramref name="partOrPak"/>
    /// unchanged when it is already a whole pak.
    /// </summary>
    public static string LogicalName(string partOrPak)
    {
        if (!TryParsePart(Path.GetFileName(partOrPak), out string stem, out _))
            return Path.GetFullPath(partOrPak);

        string directory = Path.GetDirectoryName(Path.GetFullPath(partOrPak))!;
        return Path.Combine(directory, stem + Extension);
    }

    /// <summary>
    /// Existing parts for a whole-pak path, ordered by index. A path that is
    /// itself a part resolves its siblings.
    /// </summary>
    public static IReadOnlyList<string> DiscoverParts(string partOrPak)
    {
        string logical = LogicalName(partOrPak);
        var parts = EnumeratePartFiles(logical)
            .OrderBy(PartIndex)
            .ToList();

        if (parts.Count > 0)
        {
            ValidateContiguous(parts);
            return parts;
        }

        if (TryParsePart(Path.GetFileName(partOrPak), out _, out _))
        {
            throw new FileNotFoundException(
                $"No parts found for '{Path.GetFileName(logical)}'.", logical);
        }

        return new[] { Path.GetFullPath(partOrPak) };
    }

    /// <summary>Every part file for a logical pak path, unordered.</summary>
    public static IEnumerable<string> EnumeratePartFiles(string logicalPakPath)
    {
        string full = Path.GetFullPath(logicalPakPath);
        string directory = Path.GetDirectoryName(full)!;
        string stem = Path.GetFileNameWithoutExtension(full);

        if (!Directory.Exists(directory)) yield break;

        foreach (string file in Directory.EnumerateFiles(directory, stem + PartInfix + "*" + Extension))
        {
            if (!TryParsePart(Path.GetFileName(file), out string candidateStem, out _)) continue;
            if (!string.Equals(candidateStem, stem, StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    /// <summary>Part index from a name, or 0 when the name is not a part.</summary>
    public static int PartIndex(string path)
        => TryParsePart(Path.GetFileName(path), out _, out int index) ? index : 0;

    private static bool TryParsePart(string fileName, out string stem, out int index)
    {
        stem = string.Empty;
        index = 0;

        if (string.IsNullOrWhiteSpace(fileName)) return false;

        Match match = PartPattern.Match(fileName);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["index"].Value, out index)) return false;

        stem = match.Groups["stem"].Value;
        return stem.Length > 0;
    }

    /// <summary>
    /// Verifies every part against the recorded size and SHA-256. Throws with the
    /// first mismatch, naming the part, so a delivery layer can re-fetch just
    /// that one.
    /// </summary>
    public static void Verify(
        IReadOnlyList<string> parts,
        IReadOnlyDictionary<string, V12PakPartInfo> partInfo)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(partInfo);

        foreach (string part in parts)
        {
            string name = Path.GetFileName(part);

            if (!partInfo.TryGetValue(name, out V12PakPartInfo? expected))
                throw new InvalidDataException($"No recorded size or hash for part '{name}'.");

            var file = new FileInfo(part);
            if (!file.Exists)
                throw new FileNotFoundException($"Split pak part not found: {part}", part);

            if (expected.Bytes > 0 && file.Length != expected.Bytes)
            {
                throw new InvalidDataException(
                    $"Part '{name}' is {file.Length} bytes, expected {expected.Bytes}.");
            }

            string actual = HashFile(part);
            if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Part '{name}' hash mismatch: expected {expected.Sha256}, got {actual}.");
            }
        }
    }

    /// <summary>Lowercase hex SHA-256 of a whole file.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static void ValidateContiguous(IReadOnlyList<string> parts)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            int expected = i + 1;
            int actual = PartIndex(parts[i]);
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"Split pak parts are not contiguous: expected part {expected:D3}, found " +
                    $"'{Path.GetFileName(parts[i])}'.");
            }
        }
    }
}
