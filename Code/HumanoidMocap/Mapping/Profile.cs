#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HumanoidMocap.Mapping;

/// <summary>
/// A named bone-mapping profile for a rig family: per-role alias name lists plus optional
/// namespace patterns stripped from source bone names before alias comparison. Alias
/// comparison is case- and separator-insensitive (see <see cref="NormalizeName"/>), so
/// <c>"LeftArm"</c> matches <c>"left_arm"</c>, <c>"Left Arm"</c>, etc.
/// </summary>
/// <remarks>
/// Profiles serialize to a schema-versioned JSON document (<c>{"v":1,...}</c>); the shipped
/// presets live in <see cref="ProfileLibrary"/> and are also written to
/// <c>Assets/humanoid_mocap/profiles/*.json</c> by the regenerate-and-diff test.
/// User presets (saved after preview confirmation) use the same format, keyed by
/// <see cref="SkeletonSignature"/>.
/// </remarks>
public sealed class Profile
{
    /// <summary>
    /// Current JSON schema version written by <see cref="ToJson"/>.
    /// Migration policy: <see cref="FromJson"/> accepts exactly this version and rejects
    /// everything else loudly (user presets are cheap to regenerate via the preview-confirm
    /// flow). When the schema changes, bump this constant and add an explicit upgrade path
    /// in <see cref="FromJson"/> for every prior version that shipped — never reinterpret
    /// old documents silently.
    /// </summary>
    public const int SchemaVersion = 1;

    private readonly Regex[] _namespaceRegexes;

    /// <summary>Profile name (e.g. <c>mixamo</c>); preset asset files use it as file name.</summary>
    public string Name { get; }

    /// <summary>
    /// Regex patterns removed from bone names before alias matching, e.g.
    /// <c>"^mixamorig[0-9]*:"</c>. Applied in order, each at most once per name.
    /// </summary>
    public IReadOnlyList<string> NamespacePatterns { get; }

    /// <summary>
    /// Per-role alias lists, in preference order: the first alias that names an unused
    /// source bone wins. Roles absent from the dictionary are not mapped by this profile.
    /// </summary>
    public IReadOnlyDictionary<BoneRole, string[]> Aliases { get; }

    /// <summary>Creates a profile from in-memory data (used by presets and FromJson).</summary>
    public Profile(string name, IReadOnlyList<string> namespacePatterns,
        IReadOnlyDictionary<BoneRole, string[]> aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(namespacePatterns);
        ArgumentNullException.ThrowIfNull(aliases);

        Name = name;
        NamespacePatterns = namespacePatterns.ToArray();
        Aliases = aliases.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _namespaceRegexes = NamespacePatterns
            .Select(p => new Regex(p, RegexOptions.CultureInvariant))
            .ToArray();
    }

    /// <summary>Removes this profile's namespace patterns from a bone name.</summary>
    public string StripNamespace(string boneName)
    {
        ArgumentNullException.ThrowIfNull(boneName);
        foreach (var regex in _namespaceRegexes)
            boneName = regex.Replace(boneName, string.Empty, 1);
        return boneName;
    }

    /// <summary>
    /// Canonical comparison form of a bone name or alias: lower-case with every
    /// non-alphanumeric character (separators like <c>_ - . : space</c>) removed.
    /// </summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    // ---------------------------------------------------------------- json

    /// <summary>Serializes to the versioned profile JSON document (deterministic: roles in
    /// enum order, indented, trailing newline).</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SchemaVersion);
            writer.WriteString("name", Name);

            writer.WriteStartArray("namespace_patterns");
            foreach (var pattern in NamespacePatterns)
                writer.WriteStringValue(pattern);
            writer.WriteEndArray();

            writer.WriteStartObject("aliases");
            foreach (var role in Enum.GetValues<BoneRole>())
            {
                if (!Aliases.TryGetValue(role, out var aliases))
                    continue;
                writer.WriteStartArray(role.ToString());
                foreach (var alias in aliases)
                    writer.WriteStringValue(alias);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n"; // Environment.NewLine is not s&box-whitelisted
    }

    /// <summary>Parses a profile JSON document produced by <see cref="ToJson"/>.</summary>
    /// <exception cref="ArgumentException">Thrown on schema-version mismatch, unknown role
    /// names, or a structurally invalid document.</exception>
    public static Profile FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("v", out var version) || version.GetInt32() != SchemaVersion)
            throw new ArgumentException(
                $"Unsupported profile schema version (expected \"v\": {SchemaVersion}).");

        var name = root.GetProperty("name").GetString()
            ?? throw new ArgumentException("Profile JSON has a null name.");

        var patterns = new List<string>();
        if (root.TryGetProperty("namespace_patterns", out var patternsElement))
        {
            foreach (var pattern in patternsElement.EnumerateArray())
                patterns.Add(pattern.GetString()
                    ?? throw new ArgumentException("Null namespace pattern in profile JSON."));
        }

        var aliases = new Dictionary<BoneRole, string[]>();
        if (root.TryGetProperty("aliases", out var aliasesElement))
        {
            foreach (var property in aliasesElement.EnumerateObject())
            {
                if (!Enum.TryParse<BoneRole>(property.Name, ignoreCase: false, out var role))
                    throw new ArgumentException($"Unknown bone role '{property.Name}' in profile JSON.");
                aliases[role] = property.Value.EnumerateArray()
                    .Select(a => a.GetString()
                        ?? throw new ArgumentException($"Null alias for role '{role}' in profile JSON."))
                    .ToArray();
            }
        }

        return new Profile(name, patterns, aliases);
    }
}
