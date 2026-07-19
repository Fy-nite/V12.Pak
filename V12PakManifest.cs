using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace V12.Pak
{
    /// <summary>
    /// Data model for the manifest.json inside a .v12pak archive.
    /// Describes worlds, assets, and DLL gamepaks contained in the pak.
    /// </summary>
    public class V12PakManifest
    {
        /// <summary>Manifest format version. Currently 1.</summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>Engine identifier. Always "v12".</summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "v12";

        /// <summary>Worlds contained in this pak. A pak can hold multiple worlds.</summary>
        [JsonPropertyName("worlds")]
        public List<WorldEntry> Worlds { get; set; } = new();

        /// <summary>
        /// Virtual path to archive path mapping.
        /// Keys are v12:// resolvable paths (e.g. "textures/crate.png").
        /// Values are tar entry paths (e.g. "assets/textures/crate.png").
        /// </summary>
        [JsonPropertyName("assets")]
        public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Archive paths to C# DLL gamepaks. Loaded via GamepackLoader.</summary>
        [JsonPropertyName("paks")]
        public List<string> Paks { get; set; } = new();

        /// <summary>Optional description of this pak.</summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        /// <summary>Optional author.</summary>
        [JsonPropertyName("author")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Author { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

        public static V12PakManifest FromJson(byte[] json) =>
            JsonSerializer.Deserialize<V12PakManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pak manifest");

        public static V12PakManifest FromJson(string json) =>
            JsonSerializer.Deserialize<V12PakManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pak manifest");
    }

    /// <summary>
    /// Describes a single world inside a pak archive.
    /// </summary>
    public class WorldEntry
    {
        /// <summary>Human-readable world name. Used as the v12:// mount point.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Path inside the tar archive to world.xml.</summary>
        [JsonPropertyName("entry")]
        public string Entry { get; set; } = string.Empty;

        /// <summary>Path inside the tar archive to the templates directory.</summary>
        [JsonPropertyName("templatesDir")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TemplatesDir { get; set; }
    }
}
