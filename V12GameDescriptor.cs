using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12.Pak;

/// <summary>
/// The launch descriptor a build ships next to its pak (<c>game.json</c>).
///
/// It exists so nothing has to be inferred from an executable name: a host
/// reads it, resolves <see cref="Pak"/> relative to the descriptor, and runs it.
/// Fields other than <see cref="Pak"/> are hints for the shell, which is free to
/// ignore them.
///
/// It lives in <c>V12.Pak</c> because both sides already depend on this package:
/// <c>vcsdk pack</c> writes it and <c>V12.Host</c> reads it.
/// </summary>
public sealed class V12GameDescriptor
{
    /// <summary>File name a host looks for beside itself.</summary>
    public const string FileName = "game.json";

    /// <summary>Pak file name or path, relative to the descriptor.</summary>
    public string? Pak { get; set; }

    /// <summary>
    /// Part file names, relative to the descriptor, when the pak is split. Null
    /// or empty means <see cref="Pak"/> is the whole archive. The parts are a
    /// byte-level split and are read in order.
    /// </summary>
    public List<string>? Parts { get; set; }

    /// <summary>
    /// Size and SHA-256 per part file name, written for split builds. Used by
    /// resumable delivery and optional load-time verification; older descriptors
    /// simply omit it.
    /// </summary>
    public Dictionary<string, V12PakPartInfo>? PartInfo { get; set; }

    /// <summary>World to select after load. Defaults to the pak's first world.</summary>
    public string? World { get; set; }

    /// <summary>Engine identifier the build expects, currently <c>"v12"</c>.</summary>
    public string? Engine { get; set; }

    /// <summary>True when the game wants XR at launch. A hint for the shell.</summary>
    public bool Xr { get; set; }

    /// <summary>Preferred renderer id, for a shell that has a choice. Optional.</summary>
    public string? Renderer { get; set; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a descriptor, returning null when the file is missing or unreadable.</summary>
    public static V12GameDescriptor? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses descriptor JSON, returning null when it is empty or invalid.</summary>
    public static V12GameDescriptor? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<V12GameDescriptor>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes a descriptor for writing, matching what <see cref="Parse"/> accepts.</summary>
    public static string Serialize(V12GameDescriptor descriptor)
        => JsonSerializer.Serialize(descriptor, WriteOptions);

    /// <summary>Writes a descriptor to <paramref name="path"/>.</summary>
    public static void Save(string path, V12GameDescriptor descriptor)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, Serialize(descriptor));
    }
}
