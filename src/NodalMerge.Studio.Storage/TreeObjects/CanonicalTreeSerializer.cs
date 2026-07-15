using System.Text;
using System.Text.Json;

namespace NodalMerge.Studio.Storage.TreeObjects;

// docs/TREE_OBJECT_FORMAT.md — frozen contract, golden vectors in
// tests/NodalMerge.Studio.Integration.Tests/TestData/tree-format-vectors.v1.json. This is the ONE
// writer of tree-object bytes in the codebase; every byte here must match the doc exactly (field
// order, escaping, ordering) because the object's identity IS the BLAKE3 hash of these bytes — any
// drift silently produces a different hash for logically identical content.
internal static class CanonicalTreeSerializer
{
    // Last control character requiring \uXXXX escaping (U+001F) — everything above this, including
    // all non-ASCII, is written verbatim per the format doc.
    private const int LastEscapedControlChar = 0x1F;

    // v1 — one blob for the whole snapshot's path→file-blob-hash map. See the format doc's "v1 —
    // flat tree" section. Deliberately hand-rolled (not System.Text.Json for emission) — a
    // general-purpose serializer's escaping (e.g. "\/" for forward slash under some settings, or
    // different control-character handling) is not guaranteed byte-stable across framework versions,
    // which would silently change every existing tree object's hash.
    public static byte[] SerializeFlat(IReadOnlyDictionary<string, string> entries)
    {
        var sb = new StringBuilder();
        sb.Append("{\"nodalmerge\":\"tree\",\"version\":1,\"entries\":{");

        var first = true;
        foreach (var key in entries.Keys.OrderBy(k => k, Utf8ByteComparer.Instance))
        {
            if (!first) sb.Append(',');
            first = false;
            AppendEscapedString(sb, key);
            sb.Append(':');
            AppendEscapedString(sb, entries[key]);
        }

        sb.Append("}}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // Reader — parses either shape. Unlike SerializeFlat, using System.Text.Json here is fine (the
    // format doc explicitly allows any JSON parser for readers; only writers are constrained).
    public static TreeDocument Parse(ReadOnlySpan<byte> bytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException ex)
        {
            throw new FormatException("Tree object payload is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodalmerge", out var marker)
                || marker.ValueKind != JsonValueKind.String
                || marker.GetString() != "tree")
                throw new FormatException("Payload is not a nodalmerge tree object (missing/incorrect 'nodalmerge' marker).");

            if (!root.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number)
                throw new FormatException("Tree object is missing a numeric 'version' field.");

            var version = versionEl.GetInt32();
            if (!root.TryGetProperty("entries", out var entriesEl))
                throw new FormatException("Tree object is missing an 'entries' field.");

            return version switch
            {
                1 => ParseV1(entriesEl),
                2 => ParseV2(entriesEl),
                _ => throw new FormatException($"Unsupported tree object version {version}."),
            };
        }
    }

    private static TreeDocument ParseV1(JsonElement entriesEl)
    {
        if (entriesEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("v1 tree object 'entries' must be a JSON object.");

        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in entriesEl.EnumerateObject())
        {
            flat[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()!
                : throw new FormatException($"v1 entry '{prop.Name}' has a non-string hash value.");
        }

        return new TreeDocument(1, flat, null);
    }

    private static TreeDocument ParseV2(JsonElement entriesEl)
    {
        if (entriesEl.ValueKind != JsonValueKind.Array)
            throw new FormatException("v2 tree object 'entries' must be a JSON array.");

        var list = new List<TreeEntry>();
        foreach (var item in entriesEl.EnumerateArray())
        {
            if (!item.TryGetProperty("n", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                throw new FormatException("v2 entry is missing a string 'n' (name) field.");
            if (!item.TryGetProperty("k", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
                throw new FormatException("v2 entry is missing a string 'k' (kind) field.");
            if (!item.TryGetProperty("h", out var hashEl) || hashEl.ValueKind != JsonValueKind.String)
                throw new FormatException("v2 entry is missing a string 'h' (hash) field.");

            var kind = kindEl.GetString() switch
            {
                "f" => TreeEntryKind.File,
                "d" => TreeEntryKind.Directory,
                var other => throw new FormatException($"v2 entry has an unrecognized kind '{other}'."),
            };

            list.Add(new TreeEntry(nameEl.GetString()!, kind, hashEl.GetString()!));
        }

        return new TreeDocument(2, null, list);
    }

    // Escaping, exactly per docs/TREE_OBJECT_FORMAT.md: quote -> \", backslash -> \\, U+0000-U+001F
    // -> \u + 4 lowercase hex digits. Everything else — including non-ASCII — is verbatim UTF-8.
    // Iterating char-by-char and appending unmodified is correct even for surrogate pairs: both
    // halves pass through untouched, and Encoding.UTF8.GetBytes on the final string re-encodes the
    // pair as the correct 4-byte UTF-8 sequence.
    private static void AppendEscapedString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '"')
                sb.Append("\\\"");
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c <= LastEscapedControlChar)
                sb.Append("\\u").Append(((int)c).ToString("x4"));
            else
                sb.Append(c);
        }
        sb.Append('"');
    }

    // Explicit UTF-8 byte-wise comparer — UTF-16 ordinal comparison (the .NET string default)
    // disagrees with UTF-8 byte order for keys containing surrogate pairs (see the format doc's
    // v1-utf8-ordering vector: U+FF61 sorts before U+10000 in UTF-8 byte order, but a UTF-16 ordinal
    // compare puts the surrogate-pair character first because its lead surrogate 0xD800 is a lower
    // char value than U+FF61).
    private sealed class Utf8ByteComparer : IComparer<string>
    {
        public static readonly Utf8ByteComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var bx = Encoding.UTF8.GetBytes(x);
            var by = Encoding.UTF8.GetBytes(y);
            var len = Math.Min(bx.Length, by.Length);
            for (var i = 0; i < len; i++)
            {
                var cmp = bx[i].CompareTo(by[i]);
                if (cmp != 0) return cmp;
            }
            return bx.Length.CompareTo(by.Length);
        }
    }
}

// Version 1 -> FlatEntries populated, Entries null. Version 2 -> Entries populated, FlatEntries null.
internal sealed record TreeDocument(
    int Version,
    IReadOnlyDictionary<string, string>? FlatEntries,
    IReadOnlyList<TreeEntry>? Entries);

// v2 directory entry — n(ame)/k(ind)/h(ash) per docs/TREE_OBJECT_FORMAT.md.
internal sealed record TreeEntry(string Name, TreeEntryKind Kind, string Hash);

internal enum TreeEntryKind
{
    File,
    Directory,
}
