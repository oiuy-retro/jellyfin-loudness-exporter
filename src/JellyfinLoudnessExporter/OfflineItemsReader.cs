using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal static class OfflineItemsReader
{
    // Logical aliases that must not appear together on one item.
    private static readonly string[] TrackGainAliases = ["NormalizationGain", "normalizationGain", "trackGainDb"];
    private static readonly string[] AlbumGainAliases = ["AlbumNormalizationGain", "albumNormalizationGain", "albumGainDb"];
    private static readonly string[] IdAliases = ["Id", "id", "sourceItemId"];
    private static readonly string[] AlbumIdAliases = ["AlbumId", "albumId", "sourceAlbumId"];
    private static readonly string[] PathAliases = ["Path", "path"];

    public static (string? ServerVersion, List<SourceItem> Items) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static (string? ServerVersion, List<SourceItem> Items) Parse(ReadOnlySpan<byte> utf8Json)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

        RejectDuplicateProperties(utf8Json);
        var rented = utf8Json.ToArray();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rented, options);
        }
        catch (JsonException)
        {
            throw new SafeErrorException(SafeError.Category.Json, "malformed_json");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SafeErrorException(SafeError.Category.Json, "root_not_object");
            }

            var root = doc.RootElement;
            string? serverVersion = null;
            if (root.TryGetProperty("serverVersion", out var sv))
            {
                if (sv.ValueKind == JsonValueKind.String)
                {
                    serverVersion = sv.GetString();
                }
                else if (sv.ValueKind != JsonValueKind.Null)
                {
                    throw new SafeErrorException(SafeError.Category.Json, "serverVersion_type");
                }
            }

            if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
            {
                throw new SafeErrorException(SafeError.Category.Json, "items_missing");
            }

            var items = new List<SourceItem>(itemsEl.GetArrayLength());
            foreach (var el in itemsEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    throw new SafeErrorException(SafeError.Category.Json, "item_not_object");
                }

                items.Add(ParseItem(el));
            }

            return (serverVersion, items);
        }
    }

    private static SourceItem ParseItem(JsonElement el)
    {
        RejectLogicalAliasDupes(el, TrackGainAliases, "track_gain_alias_dup");
        RejectLogicalAliasDupes(el, AlbumGainAliases, "album_gain_alias_dup");
        RejectLogicalAliasDupes(el, IdAliases, "id_alias_dup");
        RejectLogicalAliasDupes(el, AlbumIdAliases, "album_id_alias_dup");
        RejectLogicalAliasDupes(el, PathAliases, "path_alias_dup");

        string? id = null;
        string? albumId = null;
        string? path = null;
        var trackStatus = GainStatus.Missing;
        var albumStatus = GainStatus.Missing;
        double? trackGain = null;
        double? albumGain = null;

        foreach (var prop in el.EnumerateObject())
        {
            // Unknown fields ignored at every object level.
            if (TrackGainAliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                var g = GainParser.Parse(prop.Value);
                trackStatus = MapGain(g);
                trackGain = g.Value;
            }
            else if (AlbumGainAliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                var g = GainParser.Parse(prop.Value);
                albumStatus = MapGain(g);
                albumGain = g.Value;
            }
            else if (IdAliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    throw new SafeErrorException(SafeError.Category.Json, "id_type");
                }

                id = prop.Value.GetString();
            }
            else if (AlbumIdAliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    albumId = null;
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    albumId = prop.Value.GetString();
                }
                else
                {
                    // Guid may serialize as string; reject object/bool
                    albumId = prop.Value.ToString();
                }
            }
            else if (PathAliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    throw new SafeErrorException(SafeError.Category.Json, "path_type");
                }

                path = prop.Value.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SafeErrorException(SafeError.Category.Json, "item_missing_id");
        }

        if (path is null)
        {
            throw new SafeErrorException(SafeError.Category.Json, "item_missing_path");
        }

        return new SourceItem
        {
            Id = id,
            AlbumId = albumId,
            Path = path, // do not trim
            NormalizationGain = trackGain,
            AlbumNormalizationGain = albumGain,
            TrackGainStatus = trackStatus,
            AlbumGainStatus = albumStatus
        };
    }

    private static GainStatus MapGain(GainParser.ParseResult g) => g.Kind switch
    {
        GainParser.ResultKind.Missing => GainStatus.Missing,
        GainParser.ResultKind.Finite => GainStatus.Finite,
        GainParser.ResultKind.NonFinite => GainStatus.NonFinite,
        _ => GainStatus.InvalidType
    };

    internal static void RejectLogicalAliasDupes(JsonElement el, string[] aliases, string code)
    {
        var count = 0;
        foreach (var prop in el.EnumerateObject())
        {
            if (aliases.Contains(prop.Name, StringComparer.Ordinal))
            {
                count++;
            }
        }

        if (count > 1)
        {
            throw new SafeErrorException(SafeError.Category.Json, code);
        }
    }

    /// <summary>API items should not mix Jellyfin DTO names with export aliases.</summary>
    internal static void RejectLogicalAliasDupesPublic(JsonElement el)
    {
        RejectLogicalAliasDupes(el, TrackGainAliases, "track_gain_alias_dup");
        RejectLogicalAliasDupes(el, AlbumGainAliases, "album_gain_alias_dup");
    }

    internal static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });

        var stack = new Stack<HashSet<string>>();
        try
        {
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
                        if (!stack.Peek().Add(name))
                        {
                            throw new SafeErrorException(SafeError.Category.Json, "duplicate_property");
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            throw new SafeErrorException(SafeError.Category.Json, "malformed_json");
        }
    }
}
