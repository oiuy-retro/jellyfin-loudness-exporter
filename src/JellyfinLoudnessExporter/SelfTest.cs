using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal static class SelfTest
{
    public static int Run(string fixturesDir)
    {
        var failed = RegressionTests.Run(fixturesDir);
        failed += Check("normal", () => CaseNormal(fixturesDir));
        failed += Check("missing_gains", () => CaseMissingGains(fixturesDir));
        failed += Check("backslash", () => CaseBackslash());
        failed += Check("unicode_preserve", () => CaseUnicodePreserve());
        failed += Check("casefold_strasse", () => CaseStrasse());
        failed += Check("casefold_sigma", () => CaseSigma());
        failed += Check("nfc_nfd_collision", () => CaseUnicodeCollision());
        failed += Check("rootId_ordinal", () => CaseRootIdOrdinal());
        failed += Check("turkish_non_turkic", () => CaseTurkishNonTurkic());
        failed += Check("supplementary", () => CaseSupplementary());
        failed += Check("case_collision", () => CaseCaseCollision());
        failed += Check("triple_collision", () => CaseTripleCollision());
        failed += Check("reject_traversal", () => CaseRejectTraversal());
        failed += Check("absolute_residual_matched", () => CaseAbsoluteResidualMatched());
        failed += Check("reject_overlap_roots", () => CaseRejectOverlapRoots());
        failed += Check("drive_root_C", () => CaseDriveRoot());
        failed += Check("reject_device_namespace", () => CaseDeviceNamespace());
        failed += Check("reject_dot_root", () => CaseDotRoot());
        failed += Check("no_trim_source_path", () => CaseNoTrim());
        failed += Check("explicit_partial", () => CaseExplicitPartial());
        failed += Check("coverage_exported_only", () => CaseCoverageExportedOnly());
        failed += Check("out_of_range", () => CaseOutOfRange());
        failed += Check("nonfinite_json_number_fixture", () => CaseNonfiniteFixture(fixturesDir));
        failed += Check("unknown_nested_fields", () => CaseUnknownNested(fixturesDir));
        failed += Check("logical_alias_dup", () => CaseLogicalAliasDup());
        failed += Check("duplicate_json_property", () => CaseDuplicateProperty());
        failed += Check("invalid_gain_type", () => CaseInvalidGainType());
        failed += Check("atomic_writer", () => CaseAtomicWriter());
        failed += Check("writer_preexisting_temp", () => CaseWriterPreexistingTemp());
        failed += Check("shared_valid_export_schema", () => CaseSharedValid(fixturesDir));
        failed += Check("http_normalize_slash", () => CaseHttpNormalizeSlash());
        failed += Check("http_reject_userinfo", () => CaseHttpUserInfo());
        failed += Check("http_single_page_fake", () => CaseHttpSinglePageFake());
        failed += Check("http_redirect_refused", () => CaseHttpRedirect());
        failed += Check("http_unreachable", () => CaseHttpUnreachable());
        failed += Check("http_invalid_gain", () => CaseHttpInvalidGain());
        failed += Check("collision_vectors_file", () => CaseCollisionVectors(fixturesDir));
        failed += Check("perf_100k", () => CasePerf100k());
        failed += Check("fixture_markers", () => CaseFixtureMarkers(fixturesDir));
        failed += Check("expected_normal_canonical", () => CaseExpectedNormalCanonical(fixturesDir));
        failed += Check("database_lufs", () => CaseDatabaseLufs());

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
            Console.Error.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static List<RootMapping> MusicRoot() => PathMapper.ParseRoots(["music=C:\\Music"]);

    private static ExportDocument MustBuild(IReadOnlyList<SourceItem> items, bool partial = false, bool lufsHint = true, string? ver = "10.11.0-SYNTHETIC")
    {
        var result = new ExportBuilder(MusicRoot(), partial, ver, lufsHint).Build(items);
        if (!result.Success || result.Document is null)
            throw new Exception($"expected success, got {result.FailureCode}");
        return result.Document;
    }

    private static SourceItem Item(string id, string path, double? track = null, double? album = null, GainStatus? ts = null, GainStatus? als = null) => new()
    {
        Id = id,
        Path = path,
        NormalizationGain = track,
        AlbumNormalizationGain = album,
        TrackGainStatus = ts ?? (track is null ? GainStatus.Missing : GainStatus.Finite),
        AlbumGainStatus = als ?? (album is null ? GainStatus.Missing : GainStatus.Finite)
    };

    private static void CaseNormal(string fixturesDir)
    {
        var (ver, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-normal.json"));
        var doc = new ExportBuilder(MusicRoot(), false, ver, true).Build(items).Document ?? throw new Exception("null");
        if (doc.Tracks.Count != 1) throw new Exception("count");
        if (doc.Tracks[0].RelativePath != "Artist/Album/01 Track.flac") throw new Exception(doc.Tracks[0].RelativePath);
        if (doc.CollisionKeyAlgorithm != UnicodeCaseFold.AlgorithmId) throw new Exception("algo");
        if (!doc.CollisionKey.Nfc.Contains("runtime", StringComparison.Ordinal)) throw new Exception("nfc runtime note");
    }

    private static void CaseMissingGains(string fixturesDir)
    {
        foreach (var name in new[] { "input-missing-track-gain.json", "input-missing-album-gain.json", "input-missing-both-gains.json" })
        {
            var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, name));
            MustBuild(items);
        }
    }

    private static void CaseBackslash()
    {
        var doc = MustBuild([Item("a", @"C:\Music\Sub\File.mp3", 1.0)]);
        if (doc.Tracks[0].RelativePath != "Sub/File.mp3") throw new Exception(doc.Tracks[0].RelativePath);
    }

    private static void CaseUnicodePreserve()
    {
        var doc = MustBuild([Item("u1", "C:/Music/Artist/Café/Song.flac", 0.0)]);
        if (doc.Tracks[0].RelativePath != "Artist/Café/Song.flac") throw new Exception(doc.Tracks[0].RelativePath);
    }

    private static void CaseStrasse()
    {
        var a = UnicodeCaseFold.FullCaseFoldNfc("Straße");
        var b = UnicodeCaseFold.FullCaseFoldNfc("STRASSE");
        if (a != b) throw new Exception($"Strasse fold {a} vs {b}");
        var key1 = UnicodeCaseFold.BuildCollisionKey("music", "Straße/a.flac");
        var key2 = UnicodeCaseFold.BuildCollisionKey("music", "STRASSE/a.flac");
        if (key1 != key2) throw new Exception("strasse keys");
    }

    private static void CaseSigma()
    {
        // final sigma ς (U+03C2) folds to σ (U+03C3); capital Σ also to σ
        var finalSig = UnicodeCaseFold.FullCaseFold("\u03C2");
        var capital = UnicodeCaseFold.FullCaseFold("\u03A3");
        if (finalSig != capital) throw new Exception($"sigma {finalSig} vs {capital}");
    }

    private static void CaseUnicodeCollision()
    {
        var nfc = "Café";
        var nfd = "Caf" + "e\u0301";
        var items = new List<SourceItem>
        {
            Item("1", $"C:/Music/{nfc}/a.flac", 1),
            Item("2", $"C:/Music/{nfd}/a.flac", 2)
        };
        if (new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items).Success)
            throw new Exception("complete should fail");
        var partial = MustBuild(items, partial: true);
        if (partial.Tracks.Count != 0) throw new Exception("no emit");
        if (partial.Coverage.Collisions != 2) throw new Exception($"collisions={partial.Coverage.Collisions}");
    }

    private static void CaseRootIdOrdinal()
    {
        var k1 = UnicodeCaseFold.BuildCollisionKey("Music", "a.flac");
        var k2 = UnicodeCaseFold.BuildCollisionKey("music", "a.flac");
        if (k1 == k2) throw new Exception("rootId must be ordinal-exact");
    }

    private static void CaseTurkishNonTurkic()
    {
        // Non-Turkic: I (U+0049) -> i (U+0069), NOT ı (U+0131). T mapping excluded.
        var folded = UnicodeCaseFold.FullCaseFold("I");
        if (folded != "i") throw new Exception($"got {folded}");
        // Dotted İ (U+0130) full fold -> i + combining dot
        var dotted = UnicodeCaseFold.FullCaseFold("\u0130");
        if (!dotted.StartsWith('i')) throw new Exception(dotted);
    }

    private static void CaseSupplementary()
    {
        // Deseret capital 𐐀 (U+10400) -> 𐐨 (U+10428) per CaseFolding
        var s = char.ConvertFromUtf32(0x10400);
        var folded = UnicodeCaseFold.FullCaseFold(s);
        var expect = char.ConvertFromUtf32(0x10428);
        if (folded != expect) throw new Exception($"{folded} vs {expect}");
    }

    private static void CaseCaseCollision()
    {
        var items = new List<SourceItem>
        {
            Item("1", @"C:\Music\Artist\Song.flac", 1),
            Item("2", @"C:\Music\artist\song.flac", 2)
        };
        var partial = MustBuild(items, partial: true);
        if (partial.Tracks.Count != 0 || partial.Coverage.Collisions != 2) throw new Exception("case collision");
    }

    private static void CaseTripleCollision()
    {
        var items = new List<SourceItem>
        {
            Item("1", @"C:\Music\A\x.flac", 1),
            Item("2", @"C:\Music\a\x.flac", 2),
            Item("3", @"C:\Music\A\X.flac", 3)
        };
        var partial = MustBuild(items, partial: true);
        if (partial.Tracks.Count != 0) throw new Exception("reemit");
        if (partial.Coverage.Collisions != 3) throw new Exception($"collisions={partial.Coverage.Collisions}");
    }

    private static void CaseRejectTraversal()
    {
        var r = new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build([Item("1", @"C:\Music\..\Secret\a.flac", 1)]);
        if (r.Success) throw new Exception("should reject");
    }

    private static void CaseAbsoluteResidualMatched()
    {
        // Matched root but residual itself absolute-like: craft via path C:\Music\C:\evil — after strip residual "C:/evil"
        var map = PathMapper.MapPath(@"C:\Music\C:\evil.flac", MusicRoot());
        if (map.Status == MapStatus.Ok) throw new Exception("should not ok");
        if (map.ErrorCode is not ("drive_residual" or "colon_in_segment" or "invalid_path_syntax"))
            throw new Exception("code=" + map.ErrorCode);
    }

    private static void CaseRejectOverlapRoots()
    {
        try
        {
            PathMapper.ParseRoots(["music=C:\\Music", "other=C:\\Music\\Album"]);
            throw new Exception("should throw");
        }
        catch (SafeErrorException) { }
    }

    private static void CaseDriveRoot()
    {
        var roots = PathMapper.ParseRoots(["drive=C:\\"]);
        if (roots[0].NormalizedPrefix != "C:/") throw new Exception(roots[0].NormalizedPrefix);
        var map = PathMapper.MapPath(@"C:\Music\a.flac", roots);
        if (map.Status != MapStatus.Ok || map.RelativePath != "Music/a.flac") throw new Exception(map.RelativePath + " " + map.Status);
    }

    private static void CaseDeviceNamespace()
    {
        try
        {
            PathMapper.ParseRoots(["x=\\\\.\\C:\\Music"]);
            throw new Exception("should reject");
        }
        catch (SafeErrorException) { }
    }

    private static void CaseDotRoot()
    {
        try
        {
            PathMapper.ParseRoots(["x=C:\\Music\\..\\Other"]);
            throw new Exception("should reject");
        }
        catch (SafeErrorException) { }
    }

    private static void CaseNoTrim()
    {
        // Trailing space segment invalid
        var map = PathMapper.MapPath("C:/Music/bad /a.flac", MusicRoot());
        if (map.Status == MapStatus.Ok) throw new Exception("trailing space should fail");
    }

    private static void CaseExplicitPartial()
    {
        var items = new List<SourceItem>
        {
            Item("ok", @"C:\Music\A\a.flac", 1),
            Item("bad", @"D:\Nope\b.flac", 1)
        };
        if (new ExportBuilder(MusicRoot(), false, "SYNTHETIC", false).Build(items).Success)
            throw new Exception("complete block");
        var doc = MustBuild(items, partial: true);
        if (!doc.Coverage.Partial || doc.Tracks.Count != 1 || doc.Coverage.Unmatched != 1) throw new Exception("partial");
    }

    private static void CaseCoverageExportedOnly()
    {
        // One unmatched (missing gains irrelevant), one exported with missing track gain
        var items = new List<SourceItem>
        {
            Item("bad", @"D:\Nope\b.flac"),
            Item("ok", @"C:\Music\A\a.flac", track: null, album: -1.0)
        };
        var doc = MustBuild(items, partial: true);
        if (doc.Coverage.MissingTrackGain != 1) throw new Exception("missingTrack only exported");
        if (doc.Coverage.MissingAlbumGain != 0) throw new Exception("album present");
        if (doc.Coverage.Unmatched != 1) throw new Exception("unmatched");
    }

    private static void CaseOutOfRange()
    {
        var doc = MustBuild([Item("1", @"C:\Music\a.flac", 40.0)]);
        if (doc.Tracks[0].TrackGainDb != 40.0 || doc.Coverage.OutOfRangeGains != 1) throw new Exception("oor");
    }

    private static void CaseNonfiniteFixture(string fixturesDir)
    {
        // Actual invalid JSON with NaN token — must fail parse as malformed_json (not catch Assertion)
        var path = Path.Combine(fixturesDir, "input-nonfinite-nan.json");
        var raw = File.ReadAllBytes(path);
        try
        {
            OfflineItemsReader.Parse(raw);
            throw new Exception("should fail parse");
        }
        catch (SafeErrorException ex) when (ex.Code is "malformed_json" or "nonfinite_gain")
        {
            // STJ rejects NaN as malformed JSON — expected
        }
        catch (SafeErrorException ex)
        {
            throw new Exception("unexpected code " + ex.Code);
        }
    }

    private static void CaseUnknownNested(string fixturesDir)
    {
        var (_, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-unknown-field.json"));
        MustBuild(items);
    }

    private static void CaseLogicalAliasDup()
    {
        var json = """{"serverVersion":"SYNTHETIC","items":[{"Id":"1","Path":"C:\\Music\\a.flac","NormalizationGain":1.0,"trackGainDb":2.0}]}"""u8.ToArray();
        try
        {
            OfflineItemsReader.Parse(json);
            throw new Exception("should fail alias");
        }
        catch (SafeErrorException ex) when (ex.Code == "track_gain_alias_dup") { }
    }

    private static void CaseDuplicateProperty()
    {
        var json = """{"serverVersion":"x","items":[{"Id":"1","Id":"2","Path":"C:\\Music\\a.flac"}]}"""u8.ToArray();
        try { OfflineItemsReader.Parse(json); throw new Exception("dup"); }
        catch (SafeErrorException ex) when (ex.Code == "duplicate_property") { }
    }

    private static void CaseInvalidGainType()
    {
        var json = """{"serverVersion":"SYNTHETIC","items":[{"Id":"1","Path":"C:\\Music\\a.flac","NormalizationGain":true}]}"""u8.ToArray();
        var (_, items) = OfflineItemsReader.Parse(json);
        var r = new ExportBuilder(MusicRoot(), true, "SYNTHETIC", false).Build(items);
        if (!r.Success) throw new Exception("partial should succeed with reject");
        if (r.Document!.Coverage.Rejected != 1) throw new Exception("rejected");
    }

    private static void CaseAtomicWriter()
    {
        var dir = Directory.CreateTempSubdirectory("jle-w-");
        try
        {
            var outPath = Path.Combine(dir.FullName, "out.json");
            var doc = MustBuild([Item("1", @"C:\Music\a.flac", 1)]);
            ExportWriter.WriteAtomic(doc, outPath, force: false);
            try { ExportWriter.WriteAtomic(doc, outPath, force: false); throw new Exception("overwrite"); }
            catch (SafeErrorException) { }
            ExportWriter.WriteAtomic(doc, outPath, force: true);
            var bytes = File.ReadAllBytes(outPath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF) throw new Exception("BOM");
            // failure path: simulate by writing to invalid nested when parent is file — skip
        }
        finally { dir.Delete(true); }
    }

    private static void CaseWriterPreexistingTemp()
    {
        var dir = Directory.CreateTempSubdirectory("jle-t-");
        try
        {
            // Predictable .tmp sibling must NOT be used; create one and ensure write still works
            File.WriteAllText(Path.Combine(dir.FullName, "out.json.tmp"), "STALE");
            var outPath = Path.Combine(dir.FullName, "out.json");
            var doc = MustBuild([Item("1", @"C:\Music\a.flac", 1)]);
            ExportWriter.WriteAtomic(doc, outPath, force: false);
            if (File.ReadAllText(Path.Combine(dir.FullName, "out.json.tmp")) != "STALE")
                throw new Exception("must not touch predictable tmp");
            if (!File.Exists(outPath)) throw new Exception("missing out");
        }
        finally { dir.Delete(true); }
    }

    private static void CaseSharedValid(string fixturesDir)
    {
        ContractAssertions.Validate(File.ReadAllBytes(Path.Combine(fixturesDir, "shared-valid-export.json")));
        ContractAssertions.Validate(File.ReadAllBytes(Path.Combine(fixturesDir, "shared-future-fields-export.json")));
    }

    private static void CaseHttpNormalizeSlash()
    {
        var u = JellyfinApiClient.NormalizeBaseUri("https://example.com/jellyfin", allowHttp: false);
        if (!u.AbsoluteUri.EndsWith("/jellyfin/", StringComparison.Ordinal)) throw new Exception(u.AbsoluteUri);
    }

    private static void CaseHttpUserInfo()
    {
        try
        {
            JellyfinApiClient.NormalizeBaseUri("https://user:pass@example.com/", false);
            throw new Exception("should reject");
        }
        catch (SafeErrorException ex) when (ex.Code == "base_url_userinfo") { }
    }

    private static void CaseHttpSinglePageFake()
    {
        var calls = 0;
        var handler = new FakeHttpHandler
        {
            Responder = req =>
            {
                calls++;
                var path = req.RequestUri!.PathAndQuery;
                if (path.Contains("System/Info", StringComparison.Ordinal))
                    return FakeHttpHandler.Json(HttpStatusCode.OK, """{"Version":"10.11.0-SYNTHETIC"}""");
                if (path.Contains("startIndex=0", StringComparison.Ordinal))
                    return FakeHttpHandler.Json(HttpStatusCode.OK,
                        """{"TotalRecordCount":2,"Items":[{"Id":"1","Path":"C:\\Music\\a.flac","NormalizationGain":1.0},{"Id":"2","Path":"C:\\Music\\b.flac","NormalizationGain":2.0}]}""");
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"TotalRecordCount":2,"Items":[]}""");
            }
        };
        using var client = new JellyfinApiClient("https://example.com/", "token", handler);
        var items = client.FetchAudioItemsAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        if (items.Count != 2) throw new Exception("count " + items.Count);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("unreachable");
    }

    private static void CaseHttpUnreachable()
    {
        using var client = new JellyfinApiClient("https://example.com/", "token", new UnreachableHandler());
        try
        {
            client.GetServerVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("should report unreachable");
        }
        catch (SafeErrorException ex) when (ex.Code == "unreachable") { }
    }

    private static void CaseHttpRedirect()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/") } }
        };
        using var client = new JellyfinApiClient("https://example.com/", "token", handler);
        try
        {
            client.FetchAudioItemsAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("should refuse redirect");
        }
        catch (SafeErrorException ex) when (ex.Code == "redirect_refused") { }
    }

    private static void CaseHttpInvalidGain()
    {
        var handler = new FakeHttpHandler
        {
            Responder = req =>
            {
                if (req.RequestUri!.PathAndQuery.Contains("Items", StringComparison.Ordinal))
                    return FakeHttpHandler.Json(HttpStatusCode.OK,
                        """{"TotalRecordCount":1,"Items":[{"Id":"1","Path":"C:\\Music\\a.flac","NormalizationGain":true}]}""");
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"Version":"x"}""");
            }
        };
        using var client = new JellyfinApiClient("https://example.com/", "token", handler);
        var items = client.FetchAudioItemsAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        if (items[0].TrackGainStatus != GainStatus.InvalidType) throw new Exception("invalid type");
    }

    private static void CaseCollisionVectors(string fixturesDir)
    {
        var path = Path.Combine(fixturesDir, "collision-key-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var rootId = v.GetProperty("rootId").GetString()!;
            var relative = v.GetProperty("relativePath").GetString()!;
            var expected = v.GetProperty("expectedCollisionKey").GetString()!;
            var actual = UnicodeCaseFold.BuildCollisionKey(rootId, relative);
            if (actual != expected) throw new Exception($"{v.GetProperty("name").GetString()}: {actual} != {expected}");
        }
    }

    private static void CasePerf100k()
    {
        const int n = 100_000;
        var items = new List<SourceItem>(n);
        for (var i = 0; i < n; i++)
        {
            items.Add(Item(i.ToString("D8", CultureInfo.InvariantCulture), $@"C:\Music\A{i / 1000}\B{i}\track.flac", -1.0, -0.5));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = MustBuild(items);
        sw.Stop();
        if (doc.Tracks.Count != n) throw new Exception("count");
        if (sw.ElapsedMilliseconds > 90_000) throw new Exception($"slow {sw.ElapsedMilliseconds}");
        Console.WriteLine($"    (100k build {sw.ElapsedMilliseconds}ms)");
    }

    private static void CaseFixtureMarkers(string fixturesDir)
    {
        foreach (var file in Directory.EnumerateFiles(fixturesDir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.')) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("SYNTHETIC", StringComparison.Ordinal))
                throw new Exception("missing SYNTHETIC: " + name);
        }
    }

    private static void CaseExpectedNormalCanonical(string fixturesDir)
    {
        var (ver, items) = OfflineItemsReader.Read(Path.Combine(fixturesDir, "input-normal.json"));
        var doc = new ExportBuilder(MusicRoot(), false, ver, true).Build(items).Document!;
        ContractAssertions.CompareAllStableFields(doc, Path.Combine(fixturesDir, "expected-normal.json"));
    }

    private static void CaseDatabaseLufs()
    {
        var dir = Directory.CreateTempSubdirectory("jle-db-");
        try
        {
            var dbPath = Path.Combine(dir.FullName, "fixture.db");
            JellyfinDatabaseReader.CreateFixture(dbPath, """
                CREATE TABLE BaseItems (
                  Id TEXT, ParentId TEXT, Type TEXT, Path TEXT, LUFS REAL, NormalizationGain REAL
                );
                INSERT INTO BaseItems VALUES
                  ('a1', NULL, 'MediaBrowser.Controller.Entities.Audio.MusicAlbum', 'C:\Music\Album', -14.0, NULL),
                  ('t1', 'a1', 'MediaBrowser.Controller.Entities.Audio.Audio', 'C:\Music\Album\01.flac', -16.0, 9.0),
                  ('disc', 'a2', 'MediaBrowser.Controller.Entities.Folder', 'C:\Music\Other\CD1', NULL, NULL),
                  ('a2', NULL, 'MusicAlbum', 'C:\Music\Other', NULL, 0.5),
                  ('t2', 'disc', 'Audio', 'C:\Music\Other\CD1\02.flac', NULL, 1.25),
                  ('mov', NULL, 'MediaBrowser.Controller.Entities.Movie', 'C:\Music\movie.mkv', -10.0, NULL);
                """);
            var written = File.GetLastWriteTimeUtc(dbPath);
            var items = JellyfinDatabaseReader.Read(dbPath);
            if (File.GetLastWriteTimeUtc(dbPath) != written) throw new Exception("database was written");
            if (items.Count != 2) throw new Exception("count " + items.Count.ToString(CultureInfo.InvariantCulture));

            var t1 = items.Single(i => i.Id == "t1");
            if (t1.NormalizationGain != -2d || t1.AlbumNormalizationGain != -4d || t1.AlbumId != "a1")
                throw new Exception("lufs gain");
            var t2 = items.Single(i => i.Id == "t2");
            if (t2.NormalizationGain != 1.25d || t2.AlbumNormalizationGain != 0.5d || t2.AlbumId != "a2")
                throw new Exception("stored gain");

            var doc = new ExportBuilder(MusicRoot(), false, null, true, "jellyfin-database").Build(items).Document
                ?? throw new Exception("null");
            if (doc.Source.Kind != "jellyfin-database") throw new Exception("kind");
            if (doc.Tracks.Count != 2) throw new Exception("exported");
            if (doc.Tracks.Single(t => t.SourceItemId == "t1").TrackGainDb != -2d) throw new Exception("track db");
            if (doc.Tracks.Single(t => t.SourceItemId == "t1").AlbumGainDb != -4d) throw new Exception("album db");

            var missing = Path.Combine(dir.FullName, "missing.db");
            try
            {
                JellyfinDatabaseReader.Read(missing);
                throw new Exception("expected open failure");
            }
            catch (SafeErrorException ex) when (ex.Code == "database_open")
            {
            }

            if (File.Exists(missing)) throw new Exception("created missing database");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
