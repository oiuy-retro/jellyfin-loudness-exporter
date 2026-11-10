namespace JellyfinLoudnessExporter;

internal sealed class ExportBuilder
{
    public const double OutOfRangeAbsDb = 30.0;
    public const double InformationalLufsReference = -18.0;

    private readonly List<RootMapping> _roots;
    private readonly bool _partial;
    private readonly string? _serverVersion; // null => omit field
    private readonly bool _includeLufsReferenceHint;
    private readonly string _sourceKind;

    public ExportBuilder(
        List<RootMapping> roots,
        bool partial,
        string? serverVersion,
        bool includeLufsReferenceHint,
        string sourceKind = "jellyfin-api")
    {
        _roots = roots;
        _partial = partial;
        _serverVersion = serverVersion;
        _includeLufsReferenceHint = includeLufsReferenceHint;
        _sourceKind = sourceKind;
    }

    public ExportBuildResult Build(IReadOnlyList<SourceItem> items)
    {
        var coverage = new ExportCoverage { Partial = _partial };
        coverage.ItemsSeen = items.Count;

        var mapped = new List<MappedCandidate>();
        var hardFail = false;
        string? failCode = null;

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                coverage.Rejected++;
                AddError(coverage, "missing_item_id", null);
                if (!_partial) { hardFail = true; failCode = "missing_item_id"; break; }
                continue;
            }

            // Gains: invalid type / nonfinite => rejected (partial can skip item)
            if (item.TrackGainStatus == GainStatus.InvalidType || item.AlbumGainStatus == GainStatus.InvalidType)
            {
                coverage.Rejected++;
                AddError(coverage, "invalid_gain_type", item.Id);
                if (!_partial) { hardFail = true; failCode = "invalid_gain_type"; break; }
                continue;
            }

            if (item.TrackGainStatus == GainStatus.NonFinite || item.AlbumGainStatus == GainStatus.NonFinite)
            {
                coverage.Rejected++;
                AddError(coverage, "nonfinite_gain", item.Id);
                if (!_partial) { hardFail = true; failCode = "nonfinite_gain"; break; }
                continue;
            }

            var map = PathMapper.MapPath(item.Path, _roots);
            switch (map.Status)
            {
                case MapStatus.Ok:
                    mapped.Add(new MappedCandidate
                    {
                        Item = item,
                        RootId = map.RootId!,
                        RelativePath = map.RelativePath!,
                        CollisionKey = map.CollisionKey!,
                        TrackGain = item.TrackGainStatus == GainStatus.Finite ? item.NormalizationGain : null,
                        AlbumGain = item.AlbumGainStatus == GainStatus.Finite ? item.AlbumNormalizationGain : null
                    });
                    break;
                case MapStatus.Unmatched:
                    coverage.Unmatched++;
                    AddError(coverage, "unmatched_root", item.Id);
                    if (!_partial) { hardFail = true; failCode = "unmatched_root"; }
                    break;
                case MapStatus.AmbiguousRoot:
                    coverage.Rejected++;
                    AddError(coverage, "ambiguous_root", item.Id);
                    if (!_partial) { hardFail = true; failCode = "ambiguous_root"; }
                    break;
                default:
                    coverage.Rejected++;
                    AddError(coverage, map.ErrorCode ?? "invalid_relative", item.Id);
                    if (!_partial) { hardFail = true; failCode = map.ErrorCode ?? "invalid_relative"; }
                    break;
            }

            if (hardFail) break;
        }

        if (hardFail)
        {
            return ExportBuildResult.Failed(coverage, failCode ?? "complete_mode_blocked");
        }

        // Order-independent collisions: group by collision key.
        var groups = mapped.GroupBy(m => m.CollisionKey, StringComparer.Ordinal);
        var exported = new List<MappedCandidate>();
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count == 1)
            {
                exported.Add(list[0]);
            }
            else
            {
                // collisions = total colliding source items (all members)
                coverage.Collisions += list.Count;
                foreach (var c in list)
                {
                    AddError(coverage, "collision", c.Item.Id);
                }
            }
        }

        if (!_partial && coverage.Collisions > 0)
        {
            return ExportBuildResult.Failed(coverage, "collision");
        }

        if (!_partial && (coverage.Unmatched > 0 || coverage.Rejected > 0))
        {
            return ExportBuildResult.Failed(coverage, "complete_mode_blocked");
        }

        // missing / out-of-range ONLY on final exported tracks
        foreach (var e in exported)
        {
            if (e.TrackGain is null) coverage.MissingTrackGain++;
            if (e.AlbumGain is null) coverage.MissingAlbumGain++;
            if (e.TrackGain is null && e.AlbumGain is null) coverage.MissingBothGains++;
            if ((e.TrackGain is double tg && GainParser.IsOutOfRange(tg)) ||
                (e.AlbumGain is double ag && GainParser.IsOutOfRange(ag)))
            {
                coverage.OutOfRangeGains++;
            }
        }

        var doc = new ExportDocument
        {
            Format = "jellyfin-loudness-export",
            SchemaVersion = 1,
            ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            CollisionKeyAlgorithm = UnicodeCaseFold.AlgorithmId,
            CollisionKey = new CollisionKeyInfo
            {
                CaseFold = UnicodeCaseFold.CaseFoldSpec,
                Nfc = UnicodeCaseFold.NfcSpec,
                RootId = "ordinal-exact",
                RelativePath = "NFC(runtime) then unicode-15.1 full C+F case fold; stored relativePath unchanged"
            },
            Source = new ExportSource
            {
                Kind = _sourceKind,
                GainOrigin = "lufs-or-replaygain-unspecified",
                ServerVersion = _serverVersion,
                GainUnits = "dB",
                LufsDerivedReferenceLufs = _includeLufsReferenceHint ? InformationalLufsReference : null
            },
            Roots = _roots.Select(r => new ExportRoot { Id = r.Id }).ToList(),
            Tracks = exported
                .OrderBy(t => t.RootId, StringComparer.Ordinal)
                .ThenBy(t => t.RelativePath, StringComparer.Ordinal)
                .Select(t => new ExportTrack
                {
                    RootId = t.RootId,
                    RelativePath = t.RelativePath,
                    SourceItemId = t.Item.Id,
                    SourceAlbumId = string.IsNullOrWhiteSpace(t.Item.AlbumId) ? null : t.Item.AlbumId,
                    TrackGainDb = t.TrackGain,
                    AlbumGainDb = t.AlbumGain
                })
                .ToList(),
            Coverage = coverage
        };

        coverage.EntriesExported = doc.Tracks.Count;
        coverage.Partial = _partial;

        return ExportBuildResult.Ok(doc);
    }

    private static void AddError(ExportCoverage coverage, string code, string? sourceItemId)
    {
        if (coverage.Errors.Count >= 200) return;
        coverage.Errors.Add(new ExportError { Code = code, SourceItemId = sourceItemId });
    }

    private sealed class MappedCandidate
    {
        public required SourceItem Item { get; init; }
        public required string RootId { get; init; }
        public required string RelativePath { get; init; }
        public required string CollisionKey { get; init; }
        public double? TrackGain { get; init; }
        public double? AlbumGain { get; init; }
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

internal enum GainStatus
{
    Missing,
    Finite,
    NonFinite,
    InvalidType
}
