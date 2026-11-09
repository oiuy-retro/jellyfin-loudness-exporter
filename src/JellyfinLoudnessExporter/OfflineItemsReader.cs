using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal static class OfflineItemsReader
{
    public static (string ServerVersion, List<SourceItem> Items) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static (string ServerVersion, List<SourceItem> Items) Parse(ReadOnlySpan<byte> utf8Json)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

        RejectDuplicateProperties(utf8Json);
        var rented = utf8Json.ToArray();
        using var doc = JsonDocument.Parse(rented, options);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Offline input root must be an object.");
        }

        var root = doc.RootElement;
        var serverVersion = "unknown";
        if (root.TryGetProperty("serverVersion", out var sv) && sv.ValueKind == JsonValueKind.String)
        {
            serverVersion = sv.GetString() ?? "unknown";
        }

        if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Offline input must contain an 'items' array.");
        }

        var items = new List<SourceItem>(itemsEl.GetArrayLength());
        foreach (var el in itemsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Each item must be an object.");
            }

            items.Add(ParseItem(el));
        }

        return (serverVersion, items);
    }

    private static SourceItem ParseItem(JsonElement el)
    {
        string? id = null;
        string? albumId = null;
        string? path = null;
        double? trackGain = null;
        double? albumGain = null;
        var trackGainSeen = false;
        var albumGainSeen = false;

        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "Id":
                case "id":
                case "sourceItemId":
                    id = prop.Value.GetString();
                    break;
                case "AlbumId":
                case "albumId":
                case "sourceAlbumId":
                    if (prop.Value.ValueKind != JsonValueKind.Null)
                    {
                        albumId = prop.Value.GetString();
                    }

                    break;
                case "Path":
                case "path":
                    path = prop.Value.GetString();
                    break;
                case "NormalizationGain":
                case "normalizationGain":
                case "trackGainDb":
                    trackGainSeen = true;
                    trackGain = ReadNumber(prop.Value, "NormalizationGain");
                    break;
                case "AlbumNormalizationGain":
                case "albumNormalizationGain":
                case "albumGainDb":
                    albumGainSeen = true;
                    albumGain = ReadNumber(prop.Value, "AlbumNormalizationGain");
                    break;
                default:
                    // Additive unknown fields ignored.
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException("Item missing Id.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"Item {id} missing Path.");
        }

        return new SourceItem
        {
            Id = id,
            AlbumId = albumId,
            Path = path,
            NormalizationGain = trackGainSeen ? trackGain : null,
            AlbumNormalizationGain = albumGainSeen ? albumGain : null
        };
    }

    private static double? ReadNumber(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out var d))
            {
                throw new InvalidDataException($"{field} is not a finite number.");
            }

            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new InvalidDataException($"{field} is not a finite number.");
            }

            return d;
        }

        // Non-standard tokens like NaN are not valid JSON; if a reader ever produced a string, reject.
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (string.Equals(s, "NaN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "-Infinity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{field} is not a finite number.");
            }
        }

        throw new InvalidDataException($"{field} must be a number or null.");
    }

    /// <summary>
    /// System.Text.Json may last-win duplicates by default depending on options.
    /// Scan tokens and reject duplicate property names within the same object for security.
    /// </summary>
    internal static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });

        var stack = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    stack.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? "";
                    var set = stack.Peek();
                    if (!set.Add(name))
                    {
                        throw new InvalidDataException($"Duplicate JSON property: {name}");
                    }

                    break;
            }
        }
    }
}
