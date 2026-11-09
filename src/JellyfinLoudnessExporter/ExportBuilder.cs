namespace JellyfinLoudnessExporter;

internal sealed class ExportBuilder
{
    public const double OutOfRangeAbsDb = 30.0;
    public const double InformationalLufsReference = -18.0;

    private readonly List<RootMapping> _roots;
    private readonly bool _partial;
    private readonly string _serverVersion;
    private readonly bool _includeLufsReferenceHint;

    public ExportBuilder(
        List<RootMapping> roots,
        bool partial,
        string serverVersion,
        bool includeLufsReferenceHint)
    {
        _roots = roots;
        _partial = partial;
        _serverVersion = serverVersion;
        _includeLufsReferenceHint = includeLufsReferenceHint;
    }

    public ExportBuildResult Build(IReadOnlyList<SourceItem> items)
    {
        var coverage = new ExportCoverage { Partial = _partial };
        var pending = new Dictionary<string, PendingTrack>(StringComparer.Ordinal);
        var collisionKeys = new HashSet<string>(StringComparer.Ordinal);
        var blockedKeys = new HashSet<string>(StringComparer.Ordinal);

        coverage.ItemsSeen = items.Count;

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                coverage.Rejected++;
                AddError(coverage, "missing_item_id", null);
                if (!_partial)
                {
                    return ExportBuildResult.Failed(coverage, "missing_item_id");
                }

                continue;
            }

            if (!IsUsableGain(item.NormalizationGain, out var trackGain, out var trackGainBad))
            {
                if (trackGainBad == GainBadness.NonFinite)
                {
                    coverage.Rejected++;
                    AddError(coverage, "nonfinite_track_gain", item.Id);
                    if (!_partial)
                    {
                        return ExportBuildResult.Failed(coverage, "nonfinite_track_gain");
                    }

                    continue;
                }
            }

            if (!IsUsableGain(item.AlbumNormalizationGain, out var albumGain, out var albumGainBad))
            {
                if (albumGainBad == GainBadness.NonFinite)
                {
                    coverage.Rejected++;
                    AddError(coverage, "nonfinite_album_gain", item.Id);
                    if (!_partial)
                    {
                        return ExportBuildResult.Failed(coverage, "nonfinite_album_gain");
                    }

                    continue;
                }
            }

            var map = PathMapper.MapPath(item.Path, _roots);
            switch (map.Status)
            {
                case MapStatus.Ok:
                    break;
                case MapStatus.Unmatched:
                    coverage.Unmatched++;
                    AddError(coverage, "unmatched_root", item.Id);
                    if (!_partial)
                    {
                        return ExportBuildResult.Failed(coverage, "unmatched_root");
                    }

                    continue;
                case MapStatus.AmbiguousRoot:
                    coverage.Rejected++;
                    AddError(coverage, "ambiguous_root", item.Id);
                    if (!_partial)
                    {
                        return ExportBuildResult.Failed(coverage, "ambiguous_root");
                    }

                    continue;
                default:
                    coverage.Rejected++;
                    AddError(coverage, map.ErrorCode ?? "invalid_relative", item.Id);
                    if (!_partial)
                    {
                        return ExportBuildResult.Failed(coverage, map.ErrorCode ?? "invalid_relative");
                    }

                    continue;
            }

            var key = map.CollisionKey!;
            if (blockedKeys.Contains(key))
            {
                coverage.Collisions++;
                AddError(coverage, "collision", item.Id);
                continue;
            }

            if (pending.ContainsKey(key) || !collisionKeys.Add(key))
            {
                // First collision for this key: remove any pending entry; never emit either.
                if (pending.Remove(key))
                {
                    coverage.Collisions++;
                }

                coverage.Collisions++;
                blockedKeys.Add(key);
                collisionKeys.Remove(key);
                AddError(coverage, "collision", item.Id);
                if (!_partial)
                {
                    return ExportBuildResult.Failed(coverage, "collision");
                }

                continue;
            }

            if (trackGain is null)
            {
                coverage.MissingTrackGain++;
            }

            if (albumGain is null)
            {
                coverage.MissingAlbumGain++;
            }

            if (trackGain is null && albumGain is null)
            {
                coverage.MissingBothGains++;
            }

            if (trackGainBad == GainBadness.OutOfRange || albumGainBad == GainBadness.OutOfRange)
            {
                coverage.OutOfRangeGains++;
            }

            pending[key] = new PendingTrack
            {
                RootId = map.RootId!,
                RelativePath = map.RelativePath!,
                SourceItemId = item.Id,
                SourceAlbumId = string.IsNullOrWhiteSpace(item.AlbumId) ? null : item.AlbumId,
                TrackGainDb = trackGain,
                AlbumGainDb = albumGain
            };
        }

        if (!_partial && (coverage.Collisions > 0 || coverage.Unmatched > 0 || coverage.Rejected > 0))
        {
            return ExportBuildResult.Failed(coverage, "complete_mode_blocked");
        }

        var doc = new ExportDocument
        {
            Format = "jellyfin-loudness-export",
            SchemaVersion = 1,
            ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Source = new ExportSource
            {
                Kind = "jellyfin-api",
                GainOrigin = "lufs-or-replaygain-unspecified",
                ServerVersion = _serverVersion,
                GainUnits = "dB",
                LufsDerivedReferenceLufs = _includeLufsReferenceHint ? InformationalLufsReference : null
            },
            Roots = _roots.Select(r => new ExportRoot { Id = r.Id }).ToList(),
            Tracks = pending.Values
                .OrderBy(t => t.RootId, StringComparer.Ordinal)
                .ThenBy(t => t.RelativePath, StringComparer.Ordinal)
                .Select(t => new ExportTrack
                {
                    RootId = t.RootId,
                    RelativePath = t.RelativePath,
                    SourceItemId = t.SourceItemId,
                    SourceAlbumId = t.SourceAlbumId,
                    TrackGainDb = t.TrackGainDb,
                    AlbumGainDb = t.AlbumGainDb
                })
                .ToList(),
            Coverage = coverage
        };

        coverage.EntriesExported = doc.Tracks.Count;
        coverage.Partial = _partial && (coverage.Collisions > 0 || coverage.Unmatched > 0 || coverage.Rejected > 0 || _partial);
        // Explicit: partial flag is true only when --partial was requested AND export is produced.
        // Requirements: coverage.partial bool — set true when running in partial mode.
        coverage.Partial = _partial;

        return ExportBuildResult.Ok(doc);
    }

    private static void AddError(ExportCoverage coverage, string code, string? sourceItemId)
    {
        if (coverage.Errors.Count >= 200)
        {
            return;
        }

        coverage.Errors.Add(new ExportError
        {
            Code = code,
            SourceItemId = sourceItemId
        });
    }

    private enum GainBadness
    {
        None,
        Missing,
        NonFinite,
        OutOfRange
    }

    private static bool IsUsableGain(double? raw, out double? value, out GainBadness badness)
    {
        value = null;
        if (raw is null)
        {
            badness = GainBadness.Missing;
            return true;
        }

        var v = raw.Value;
        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            badness = GainBadness.NonFinite;
            return false;
        }

        value = v;
        if (Math.Abs(v) > OutOfRangeAbsDb)
        {
            badness = GainBadness.OutOfRange;
            return true;
        }

        badness = GainBadness.None;
        return true;
    }

    private sealed class PendingTrack
    {
        public required string RootId { get; init; }
        public required string RelativePath { get; init; }
        public required string SourceItemId { get; init; }
        public string? SourceAlbumId { get; init; }
        public double? TrackGainDb { get; init; }
        public double? AlbumGainDb { get; init; }
    }
}

internal sealed class ExportBuildResult
{
    public bool Success { get; private init; }
    public ExportDocument? Document { get; private init; }
    public ExportCoverage Coverage { get; private init; } = new();
    public string? FailureCode { get; private init; }

    public static ExportBuildResult Ok(ExportDocument document) => new()
    {
        Success = true,
        Document = document,
        Coverage = document.Coverage
    };

    public static ExportBuildResult Failed(ExportCoverage coverage, string code) => new()
    {
        Success = false,
        Coverage = coverage,
        FailureCode = code
    };
}
