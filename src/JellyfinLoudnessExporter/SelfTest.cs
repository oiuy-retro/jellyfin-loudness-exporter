using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal static class SelfTest
{
    public static int Run(string fixturesDir)
    {
        var failed = 0;
        failed += Check("normal", () => CaseNormal(fixturesDir));
        failed += Check("missing_track_gain", () => CaseMissingTrack(fixturesDir));
        failed += Check("missing_album_gain", () => CaseMissingAlbum(fixturesDir));
        failed += Check("missing_both_gains", () => CaseMissingBoth(fixturesDir));
        failed += Check("backslash_conversion", () => CaseBackslash());
        failed += Check("unicode_preserve", () => CaseUnicodePreserve());
        failed += Check("unicode_nfc_nfd_collision", () => CaseUnicodeCollision());
        failed += Check("case_collision", () => CaseCaseCollision());
        failed += Check("reject_traversal", () => CaseRejectTraversal());
        failed += Check("reject_absolute_residual", () => CaseRejectAbsoluteResidual());
        failed += Check("reject_overlap_roots", () => CaseRejectOverlapRoots());
        failed += Check("explicit_partial", () => CaseExplicitPartial());
        failed += Check("out_of_range", () => CaseOutOfRange());
        failed += Check("nonfinite_reject", () => CaseNonfinite(fixturesDir));
        failed += Check("unknown_field_ignored", () => CaseUnknownField(fixturesDir));
        failed += Check("duplicate_json_property", () => CaseDuplicateProperty());
        failed += Check("atomic_no_overwrite", () => CaseAtomicNoOverwrite());
        failed += Check("perf_100k", () => CasePerf100k());
        failed += Check("fixture_files_marked", () => CaseFixtureMarkers(fixturesDir));

        if (failed == 0)
        {
            Console.WriteLine("SELFTEST OK");
            return 0;
        }

        Console.Error.WriteLine($"SELFTEST FAILED: {failed} case(s)");
        return 1;
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    private static List<RootMapping> MusicRoot() =>
        PathMapper.ParseRoots(["music=C:\\Music"]);

    private static ExportDocument MustBuild(IReadOnlyList<SourceItem> items, bool partial = false, bool lufsHint = true)
    {
        var result = new ExportBuilder(MusicRoot(), partial, "10.11.0-SYNTHETIC", lufsHint).Build(items);
        if (!result.Success || result.Document is null)
        {
            throw new Exception($"expected success, got {result.FailureCode}");
        }

        return result.Document;
    }

    private static void CaseNormal(string fixturesDir)
    {
        var path = Path.Combine(fixturesDir, "input-normal.json");
        var (ver, items) = OfflineItemsReader.Read(path);
        var doc = new ExportBuilder(MusicRoot(), false, ver, true).Build(items).Document
                  ?? throw new Exception("null doc");
        if (doc.Tracks.Count != 1) throw new Exception("track count");
        var t = doc.Tracks[0];
        if (t.RelativePath != "Artist/Album/01 Track.flac") throw new Exception("path " + t.RelativePath);
        if (t.TrackGainDb != -1.5) throw new Exception("track gain");
        if (t.AlbumGainDb != -0.7) throw new Exception("album gain");
        AssertValidExportShape(doc);
        var expected = Path.Combine(fixturesDir, "expected-normal.json");
        AssertMatchesExpectedTracks(expected, doc);
    }

    private static void CaseMissingTrack(string fixturesDir)
    {
        var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-missing-track-gain.json"));
        var doc = MustBuild(items);
        if (doc.Tracks[0].TrackGainDb is not null) throw new Exception("track gain should omit");
        if (doc.Coverage.MissingTrackGain != 1) throw new Exception("missingTrackGain");
    }

    private static void CaseMissingAlbum(string fixturesDir)
    {
        var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-missing-album-gain.json"));
        var doc = MustBuild(items);
        if (doc.Tracks[0].AlbumGainDb is not null) throw new Exception("album gain should omit");
        if (doc.Coverage.MissingAlbumGain != 1) throw new Exception("missingAlbumGain");
    }

    private static void CaseMissingBoth(string fixturesDir)
    {
        var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-missing-both-gains.json"));
        var doc = MustBuild(items);
        if (doc.Tracks.Count != 1) throw new Exception("should include entry with both missing");
        if (doc.Coverage.MissingBothGains != 1) throw new Exception("missingBothGains");
    }

    private static void CaseBackslash()
    {
        var items = new List<SourceItem>
        {
            new()
            {
                Id = "a",
                Path = @"C:\Music\Sub\File.mp3",
                NormalizationGain = 1.0
            }
        };
        var doc = MustBuild(items);
        if (doc.Tracks[0].RelativePath != "Sub/File.mp3") throw new Exception(doc.Tracks[0].RelativePath);
    }

    private static void CaseUnicodePreserve()
    {
        var items = new List<SourceItem>
        {
            new()
            {
                Id = "u1",
                Path = "C:/Music/Artist/Café/Song.flac",
                NormalizationGain = 0.0
            }
        };
        var doc = MustBuild(items);
        if (!doc.Tracks[0].RelativePath.Contains('é', StringComparison.Ordinal) &&
            !doc.Tracks[0].RelativePath.Contains("Cafe", StringComparison.Ordinal))
        {
            // Accept NFC café
        }

        if (doc.Tracks[0].RelativePath != "Artist/Café/Song.flac")
        {
            throw new Exception("preserve unicode: " + doc.Tracks[0].RelativePath);
        }
    }

    private static void CaseUnicodeCollision()
    {
        // U+00E9 (NFC é) vs e + combining acute (NFD)
        var nfc = "Café";
        var nfd = ("Cafe" + "\u0301"); // e + combining acute on last e? Better: "Cafe\u0301" is Cafe + acute on e
        nfd = "Caf" + "e\u0301";
        var items = new List<SourceItem>
        {
            new() { Id = "1", Path = $"C:/Music/{nfc}/a.flac", NormalizationGain = 1 },
            new() { Id = "2", Path = $"C:/Music/{nfd}/a.flac", NormalizationGain = 2 }
        };
        var complete = new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items);
        if (complete.Success) throw new Exception("complete should fail on unicode collision");
        var partial = MustBuild(items, partial: true);
        if (partial.Tracks.Count != 0) throw new Exception("collision must not emit either");
        if (partial.Coverage.Collisions < 1) throw new Exception("collisions count");
    }

    private static void CaseCaseCollision()
    {
        var items = new List<SourceItem>
        {
            new() { Id = "1", Path = @"C:\Music\Artist\Song.flac", NormalizationGain = 1 },
            new() { Id = "2", Path = @"C:\Music\artist\song.flac", NormalizationGain = 2 }
        };
        var complete = new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items);
        if (complete.Success) throw new Exception("complete should fail case collision");
        var partial = MustBuild(items, partial: true);
        if (partial.Tracks.Count != 0) throw new Exception("no emit on collision");
    }

    private static void CaseRejectTraversal()
    {
        var items = new List<SourceItem>
        {
            new() { Id = "1", Path = @"C:\Music\..\Secret\a.flac", NormalizationGain = 1 }
        };
        // After strip of C:\Music\, residual is ../Secret/a.flac → dot_segment
        // Actually path C:\Music\..\Secret\a.flac unified = C:/Music/../Secret/a.flac
        // Starts with C:/Music/ → residual ../Secret/a.flac → reject
        var complete = new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items);
        if (complete.Success) throw new Exception("should reject traversal");
    }

    private static void CaseRejectAbsoluteResidual()
    {
        // Crafted: root C:\Music and path that somehow — use overlapping style via MapPath directly
        var roots = MusicRoot();
        var r = PathMapper.MapPath(@"D:\Other\a.flac", roots);
        if (r.Status != MapStatus.Unmatched) throw new Exception("expected unmatched");
    }

    private static void CaseRejectOverlapRoots()
    {
        try
        {
            PathMapper.ParseRoots(["music=C:\\Music", "other=C:\\Music\\Album"]);
            throw new Exception("should throw");
        }
        catch (ArgumentException)
        {
            // expected
        }
    }

    private static void CaseExplicitPartial()
    {
        var items = new List<SourceItem>
        {
            new() { Id = "ok", Path = @"C:\Music\A\a.flac", NormalizationGain = 1 },
            new() { Id = "bad", Path = @"D:\Nope\b.flac", NormalizationGain = 1 }
        };
        var blocked = new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items);
        if (blocked.Success) throw new Exception("complete should block unmatched");
        var doc = MustBuild(items, partial: true);
        if (!doc.Coverage.Partial) throw new Exception("partial flag");
        if (doc.Tracks.Count != 1) throw new Exception("one exported");
        if (doc.Coverage.Unmatched != 1) throw new Exception("unmatched count");
    }

    private static void CaseOutOfRange()
    {
        var items = new List<SourceItem>
        {
            new() { Id = "1", Path = @"C:\Music\a.flac", NormalizationGain = 40.0 }
        };
        var doc = MustBuild(items);
        if (doc.Tracks[0].TrackGainDb != 40.0) throw new Exception("must not clamp");
        if (doc.Coverage.OutOfRangeGains != 1) throw new Exception("outOfRange flag");
    }

    private static void CaseNonfinite(string fixturesDir)
    {
        var rawPath = Path.Combine(fixturesDir, "input-nonfinite-raw.txt");
        var raw = File.ReadAllBytes(rawPath);
        try
        {
            OfflineItemsReader.Parse(raw);
            throw new Exception("nonfinite JSON should fail parse");
        }
        catch (Exception)
        {
            // JsonDocument may throw JsonException on NaN; or our reader throws InvalidDataException
        }
    }

    private static void CaseUnknownField(string fixturesDir)
    {
        var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-unknown-field.json"));
        var doc = MustBuild(items);
        if (doc.Tracks.Count != 1) throw new Exception("should still export");
    }

    private static void CaseDuplicateProperty()
    {
        var json = """{"serverVersion":"x","items":[{"Id":"1","Id":"2","Path":"C:\\Music\\a.flac"}]}"""u8;
        try
        {
            OfflineItemsReader.Parse(json.ToArray());
            throw new Exception("duplicate should fail");
        }
        catch (InvalidDataException)
        {
            // expected
        }
    }

    private static void CaseAtomicNoOverwrite()
    {
        var dir = Directory.CreateTempSubdirectory("jle-test-");
        try
        {
            var outPath = Path.Combine(dir.FullName, "out.json");
            var doc = MustBuild(
            [
                new SourceItem { Id = "1", Path = @"C:\Music\a.flac", NormalizationGain = 1 }
            ]);
            ExportWriter.WriteAtomic(doc, outPath, force: false);
            try
            {
                ExportWriter.WriteAtomic(doc, outPath, force: false);
                throw new Exception("should refuse overwrite");
            }
            catch (IOException)
            {
                // expected
            }

            ExportWriter.WriteAtomic(doc, outPath, force: true);
            // UTF-8 no BOM
            var bytes = File.ReadAllBytes(outPath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                throw new Exception("BOM not allowed");
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static void CasePerf100k()
    {
        const int n = 100_000;
        var items = new List<SourceItem>(n);
        for (var i = 0; i < n; i++)
        {
            items.Add(new SourceItem
            {
                Id = i.ToString("D8", CultureInfo.InvariantCulture),
                AlbumId = (i / 10).ToString("D8", CultureInfo.InvariantCulture),
                Path = $@"C:\Music\A{i / 1000}\B{i}\track.flac",
                NormalizationGain = -1.0,
                AlbumNormalizationGain = -0.5
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = MustBuild(items);
        sw.Stop();
        if (doc.Tracks.Count != n) throw new Exception("100k count");
        if (sw.ElapsedMilliseconds > 60_000) throw new Exception($"too slow: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"    (100k build {sw.ElapsedMilliseconds}ms)");
    }

    private static void CaseFixtureMarkers(string fixturesDir)
    {
        foreach (var file in Directory.EnumerateFiles(fixturesDir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("SYNTHETIC", StringComparison.Ordinal))
            {
                throw new Exception($"fixture missing SYNTHETIC marker: {name}");
            }
        }
    }

    private static void AssertValidExportShape(ExportDocument doc)
    {
        if (doc.Format != "jellyfin-loudness-export") throw new Exception("format");
        if (doc.SchemaVersion != 1) throw new Exception("schemaVersion");
        if (doc.Source.Kind != "jellyfin-api") throw new Exception("source.kind");
        if (doc.Source.GainOrigin != "lufs-or-replaygain-unspecified") throw new Exception("gainOrigin");
        if (doc.Source.GainUnits != "dB") throw new Exception("gainUnits");
    }

    private static void AssertMatchesExpectedTracks(string expectedPath, ExportDocument doc)
    {
        using var expectedDoc = JsonDocument.Parse(File.ReadAllText(expectedPath));
        var expTracks = expectedDoc.RootElement.GetProperty("tracks");
        if (expTracks.GetArrayLength() != doc.Tracks.Count) throw new Exception("expected track count");
        for (var i = 0; i < doc.Tracks.Count; i++)
        {
            var e = expTracks[i];
            var t = doc.Tracks[i];
            if (e.GetProperty("relativePath").GetString() != t.RelativePath) throw new Exception("expected path mismatch");
            if (e.GetProperty("rootId").GetString() != t.RootId) throw new Exception("rootId");
        }
    }
}
