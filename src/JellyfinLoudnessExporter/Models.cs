using System.Text.Json.Serialization;

namespace JellyfinLoudnessExporter;

internal sealed class ExportDocument
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "jellyfin-loudness-export";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public string ExportedAt { get; set; } = "";

    [JsonPropertyName("source")]
    public ExportSource Source { get; set; } = new();

    [JsonPropertyName("roots")]
    public List<ExportRoot> Roots { get; set; } = [];

    [JsonPropertyName("tracks")]
    public List<ExportTrack> Tracks { get; set; } = [];

    [JsonPropertyName("coverage")]
    public ExportCoverage Coverage { get; set; } = new();
}

internal sealed class ExportSource
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "jellyfin-api";

    [JsonPropertyName("gainOrigin")]
    public string GainOrigin { get; set; } = "lufs-or-replaygain-unspecified";

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = "";

    [JsonPropertyName("gainUnits")]
    public string GainUnits { get; set; } = "dB";

    [JsonPropertyName("lufsDerivedReferenceLufs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LufsDerivedReferenceLufs { get; set; }
}

internal sealed class ExportRoot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

internal sealed class ExportTrack
{
    [JsonPropertyName("rootId")]
    public string RootId { get; set; } = "";

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("sourceItemId")]
    public string SourceItemId { get; set; } = "";

    [JsonPropertyName("sourceAlbumId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceAlbumId { get; set; }

    [JsonPropertyName("trackGainDb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrackGainDb { get; set; }

    [JsonPropertyName("albumGainDb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AlbumGainDb { get; set; }
}

internal sealed class ExportCoverage
{
    [JsonPropertyName("partial")]
    public bool Partial { get; set; }

    [JsonPropertyName("itemsSeen")]
    public int ItemsSeen { get; set; }

    [JsonPropertyName("entriesExported")]
    public int EntriesExported { get; set; }

    [JsonPropertyName("missingTrackGain")]
    public int MissingTrackGain { get; set; }

    [JsonPropertyName("missingAlbumGain")]
    public int MissingAlbumGain { get; set; }

    [JsonPropertyName("missingBothGains")]
    public int MissingBothGains { get; set; }

    [JsonPropertyName("unmatched")]
    public int Unmatched { get; set; }

    [JsonPropertyName("collisions")]
    public int Collisions { get; set; }

    [JsonPropertyName("outOfRangeGains")]
    public int OutOfRangeGains { get; set; }

    [JsonPropertyName("rejected")]
    public int Rejected { get; set; }

    [JsonPropertyName("errors")]
    public List<ExportError> Errors { get; set; } = [];
}

internal sealed class ExportError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("sourceItemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceItemId { get; set; }
}

internal sealed class RootMapping
{
    public required string Id { get; init; }
    public required string AbsoluteWindowsPath { get; init; }
    public required string NormalizedPrefix { get; init; }
}

internal sealed class SourceItem
{
    public required string Id { get; init; }
    public string? AlbumId { get; init; }
    public required string Path { get; init; }
    public double? NormalizationGain { get; init; }
    public double? AlbumNormalizationGain { get; init; }
}

internal enum MapStatus
{
    Ok,
    Unmatched,
    AmbiguousRoot,
    InvalidRelative,
    Rejected
}

internal sealed class MapResult
{
    public MapStatus Status { get; init; }
    public string? RootId { get; init; }
    public string? RelativePath { get; init; }
    public string? CollisionKey { get; init; }
    public string? ErrorCode { get; init; }
}
