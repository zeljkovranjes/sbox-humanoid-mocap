#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HumanoidMocap.Core.Target;

/// <summary>A KV3 object: insertion-ordered string-keyed map.</summary>
public sealed class KvObject : KvValue
{
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, KvValue> _map = new(StringComparer.Ordinal);

    /// <summary>Keys in insertion order.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Number of key/value pairs.</summary>
    public int Count => _keys.Count;

    /// <summary>Gets a value (throws when absent) or sets it (appends new keys at the end).</summary>
    public KvValue this[string key]
    {
        get => _map[key];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_map.TryAdd(key, value))
                _keys.Add(key);
            else
                _map[key] = value;
        }
    }

    /// <summary>Returns the value for <paramref name="key"/>, or null when absent.</summary>
    public KvValue? GetOrNull(string key) => _map.TryGetValue(key, out var v) ? v : null;

    /// <summary>Returns the string value of <paramref name="key"/>, or null when absent or
    /// not a string.</summary>
    public string? GetString(string key) => GetOrNull(key) is KvString s ? s.Value : null;
}
