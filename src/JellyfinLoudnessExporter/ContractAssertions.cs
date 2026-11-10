using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellyfinLoudnessExporter;

// Dependency-free fixture/test validation for this exporter's Jellyfin v1 output.
// Reserved producer extensions are not implemented here. Unknown object fields are ignored.
internal static class ContractAssertions
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        OfflineItemsReader.RejectDuplicateProperties(bytes);
        using var doc = JsonDocument.Parse(bytes.ToArray());
        var r = doc.RootElement;
        Require(Str(r, "format") == "jellyfin-loudness-export", "format");
        Require(Int(r, "schemaVersion") == 1, "schemaVersion");
        var at = Str(r, "exportedAt");
        Require(at.EndsWith('Z') && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out _), "exportedAt");
        Require(Str(r, "collisionKeyAlgorithm") == UnicodeCaseFold.AlgorithmId, "collisionKeyAlgorithm");
        var ck = Obj(r, "collisionKey");
        Require(Str(ck, "caseFold") == UnicodeCaseFold.CaseFoldSpec, "caseFold");
        Require(Str(ck, "nfc") == UnicodeCaseFold.NfcSpec, "nfc");
        Require(Str(ck, "rootId") == "ordinal-exact", "rootId rules");
        Str(ck, "relativePath");
        var source = Obj(r, "source");
        Require(Str(source, "kind") == "jellyfin-api", "source.kind");
        Require(Str(source, "gainOrigin") == "lufs-or-replaygain-unspecified", "gainOrigin");
        Require(Str(source, "gainUnits") == "dB", "gainUnits");
        if (source.TryGetProperty("serverVersion", out _)) Str(source, "serverVersion");
        if (source.TryGetProperty("lufsDerivedReferenceLufs", out var hint))
            Require(Number(hint) == -18, "lufsDerivedReferenceLufs");

        var roots = Arr(r, "roots");
        Require(roots.GetArrayLength() > 0, "empty roots");
        var rootIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.EnumerateArray())
        {
            var id = Str(root, "id");
            PathMapper.ValidateRootId(id);
            Require(rootIds.Add(id), "duplicate root");
        }

        var tracks = Arr(r, "tracks");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var missingTrack = 0; var missingAlbum = 0; var missingBoth = 0; var outOfRange = 0;
        foreach (var track in tracks.EnumerateArray())
        {
            var rootId = Str(track, "rootId");
            var path = Str(track, "relativePath");
            Require(rootIds.Contains(rootId), "unknown rootId");
            Require(!path.Contains('\\') && !path.Any(c => c < 32) && PathMapper.ValidateRelative(path) is null, "relativePath");
            Require(keys.Add(UnicodeCaseFold.BuildCollisionKey(rootId, path)), "duplicate/colliding track key");
            Str(track, "sourceItemId"); // required for this exporter, never playback identity
            if (track.TryGetProperty("sourceAlbumId", out _)) Str(track, "sourceAlbumId");
            double? tg = track.TryGetProperty("trackGainDb", out var t) ? Number(t) : null;
            double? ag = track.TryGetProperty("albumGainDb", out var a) ? Number(a) : null;
            if (tg is null) missingTrack++;
            if (ag is null) missingAlbum++;
            if (tg is null && ag is null) missingBoth++;
            if ((tg is double tv && GainParser.IsOutOfRange(tv)) || (ag is double av && GainParser.IsOutOfRange(av))) outOfRange++;
        }

        var coverage = Obj(r, "coverage");
        Require(Get(coverage, "partial").ValueKind is JsonValueKind.True or JsonValueKind.False, "partial");
        var seen = Int(coverage, "itemsSeen");
        Require(Int(coverage, "entriesExported") == tracks.GetArrayLength(), "entriesExported");
        Require(Int(coverage, "missingTrackGain") == missingTrack, "missingTrackGain");
        Require(Int(coverage, "missingAlbumGain") == missingAlbum, "missingAlbumGain");
        Require(Int(coverage, "missingBothGains") == missingBoth, "missingBothGains");
        Require(Int(coverage, "outOfRangeGains") == outOfRange, "outOfRangeGains");
        var unmatched = Int(coverage, "unmatched");
        var collisions = Int(coverage, "collisions");
        var rejected = Int(coverage, "rejected");
        Require((long)seen == (long)tracks.GetArrayLength() + unmatched + collisions + rejected, "coverage accounting");
        Require(Get(coverage, "partial").GetBoolean() || (unmatched == 0 && collisions == 0 && rejected == 0), "complete coverage");
        var errors = Arr(coverage, "errors");
        Require(errors.GetArrayLength() <= 200, "errors cap");
        foreach (var error in errors.EnumerateArray())
        {
            Str(error, "code");
            if (error.TryGetProperty("sourceItemId", out _)) Str(error, "sourceItemId");
        }
    }

    public static void CompareAllStableFields(ExportDocument actual, string expectedPath)
    {
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(actual);
        var expectedBytes = File.ReadAllBytes(expectedPath);
        Validate(actualBytes);
        Validate(expectedBytes);
        var a = JsonNode.Parse(actualBytes)!.AsObject();
        var e = JsonNode.Parse(expectedBytes)!.AsObject();
        // The ONLY ignored field is the time of this invocation. Everything else is contract data.
        a.Remove("exportedAt");
        e.Remove("exportedAt");
        Require(JsonNode.DeepEquals(a, e), "all stable fields differ from " + Path.GetFileName(expectedPath));
    }

    private static JsonElement Get(JsonElement o, string key)
    {
        Require(o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out _), "missing " + key);
        return o.GetProperty(key);
    }
    private static JsonElement Obj(JsonElement o, string key) { var v = Get(o, key); Require(v.ValueKind == JsonValueKind.Object, key); return v; }
    private static JsonElement Arr(JsonElement o, string key) { var v = Get(o, key); Require(v.ValueKind == JsonValueKind.Array, key); return v; }
    private static string Str(JsonElement o, string key) { var v = Get(o, key); Require(v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()), key); return v.GetString()!; }
    private static int Int(JsonElement o, string key) { var v = Get(o, key); Require(v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0, key); return v.GetInt32(); }
    private static double Number(JsonElement v) { Require(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d), "finite number"); return v.GetDouble(); }
    private static void Require(bool valid, string field) { if (!valid) throw new InvalidDataException("Export contract: " + field); }
}
