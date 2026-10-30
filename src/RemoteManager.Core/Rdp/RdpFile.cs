using System.Globalization;
using System.Text;

namespace RemoteManager.Core.Rdp;

/// <summary>
/// The lines of an .rdp file ("key:type:value", type <c>i</c> integer, <c>s</c> string, <c>b</c> binary).
/// Keys are case-insensitive and keep their order. mstsc writes UTF-16 with BOM; reading accepts any
/// encoding with a BOM and falls back to UTF-8.
/// </summary>
public sealed class RdpFile
{
    private readonly List<(string Key, char Type, string Value)> entries = [];

    public IEnumerable<string> Keys => entries.Select(e => e.Key);

    public static RdpFile Parse(string text)
    {
        var file = new RdpFile();
        foreach (string raw in text.Split('\n'))
        {
            // "full address:s:host:3389": the value may contain colons, so split into three parts only.
            string[] parts = raw.TrimEnd('\r').Trim('﻿').Split(':', 3);
            if (parts.Length == 3 && parts[0].Trim().Length > 0 && parts[1].Trim() is [var type]
                && char.ToLowerInvariant(type) is 'i' or 's' or 'b')
            {
                file.Set(parts[0].Trim(), char.ToLowerInvariant(type), parts[2]);
            }
        }

        return file;
    }

    public static RdpFile Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public string? GetString(string key) =>
        entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index ? entries[index].Value : null;

    public int? GetInt(string key) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>1 is true, any other number false, missing gives <paramref name="fallback"/>.</summary>
    public bool GetBool(string key, bool fallback) => GetInt(key) is { } value ? value == 1 : fallback;

    public RdpFile Set(string key, string value) => Set(key, 's', value);

    public RdpFile Set(string key, int value) => Set(key, 'i', value.ToString(CultureInfo.InvariantCulture));

    public RdpFile Set(string key, bool value) => Set(key, value ? 1 : 0);

    public bool Contains(string key) => GetString(key) is not null;

    /// <summary>The file text, one "key:type:value" per line.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach ((string key, char type, string value) in entries)
            builder.Append(key).Append(':').Append(type).Append(':').Append(value).Append("\r\n");
        return builder.ToString();
    }

    /// <summary>Writes UTF-16 LE with BOM, like mstsc.</summary>
    public void Save(string path) => File.WriteAllText(path, ToString(), Encoding.Unicode);

    private RdpFile Set(string key, char type, string value)
    {
        // Line breaks would start a new key; an .rdp value never contains one.
        value = value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        int index = entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            entries[index] = (entries[index].Key, type, value);
        else
            entries.Add((key, type, value));
        return this;
    }
}
