using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JellyfinLoudnessExporter;

internal static class RegressionTests
{
    public static int Run(string fixturesDir)
    {
        var failed = 0;
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixturesDir, "manifest.json"))))
        {
            foreach (var fixture in manifest.RootElement.GetProperty("cases").EnumerateArray())
            {
                var entry = fixture.Clone();
                failed += Check("portable:" + entry.GetProperty("name").GetString(), () => Portable(fixturesDir, entry));
            }
        }
        failed += Check("schema_rejects_invalid_exports", () => InvalidExports(fixturesDir));
        failed += Check("canonical_checks_all_fields", () => CanonicalChecksAllFields(fixturesDir));
        failed += Check("http_two_pages_auth_prefix", TwoPages);
        failed += Check("http_missing_invalid_totals", InvalidTotals);
        failed += Check("http_changed_and_second_missing_totals", ChangedTotals);
        failed += Check("http_count_mismatch_duplicate_id", CountAndDuplicate);
        failed += Check("http_stalled_body_deadline", StalledBody);
        failed += Check("http_stalled_headers_deadline", StalledHeaders);
        failed += Check("http_caller_cancellation", CallerCancellation);
        failed += Check("http_stream_oversize", StreamOversize);
        failed += Check("http_declared_oversize", DeclaredOversize);
        failed += Check("http_error_body_not_read", ErrorBodyNotRead);
        failed += Check("http_invalid_base_urls", InvalidBaseUrls);
        failed += Check("writer_held_create_new_stream", HeldCreateNewStream);
        failed += Check("writer_output_symlink_refused", OutputSymlink);
        failed += Check("coverage_six_permutations", CoveragePermutations);
        failed += Check("build_unicode_permission_notice", UnicodeNotice);
        return failed;
    }

    private static int Check(string name, Action test)
    {
        try { test(); Console.WriteLine("  PASS " + name); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }
    private static void Require(bool b, string what) { if (!b) throw new Exception(what); }
    private static void ExpectSafe(string code, Action action)
    {
        try { action(); }
        catch (SafeErrorException e) when (e.Code == code) { return; }
        throw new Exception("expected safe error " + code);
    }
    private static HttpResponseMessage Json(string json) => FakeHttpHandler.Json(HttpStatusCode.OK, json);
    private static string Page(int first, int count, int total) => JsonSerializer.Serialize(new
    {
        TotalRecordCount = total,
        Items = Enumerable.Range(first, count).Select(i => new { Id = i.ToString(), Path = $@"C:\Music\{i}.flac", NormalizationGain = 1.0 })
    });
    private static List<SourceItem> Fetch(JellyfinApiClient c, CancellationToken ct = default) =>
        c.FetchAudioItemsAsync(null, ct).GetAwaiter().GetResult();

    private static void Portable(string dir, JsonElement fixture)
    {
        var error = fixture.TryGetProperty("expectedError", out var expectedError) ? expectedError.GetString() : null;
        try
        {
            var roots = PathMapper.ParseRoots(fixture.GetProperty("roots").EnumerateArray().Select(r => r.GetString()!));
            var (version, items) = OfflineItemsReader.Read(Path.Combine(dir, fixture.GetProperty("input").GetString()!));
            var result = new ExportBuilder(roots, fixture.GetProperty("partial").GetBoolean(), version,
                fixture.GetProperty("lufsReferenceHint").GetBoolean()).Build(items);
            if (error is not null)
            {
                Require(!result.Success && result.FailureCode == error, "expected " + error + ", got " + result.FailureCode);
                return;
            }
            Require(result.Success && result.Document is not null, "build failed " + result.FailureCode);
            ContractAssertions.CompareAllStableFields(result.Document!, Path.Combine(dir, fixture.GetProperty("expectedExport").GetString()!));
        }
        catch (SafeErrorException e) when (error is not null && e.Code == error) { }
    }

    private static void InvalidExports(string dir)
    {
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "shared-valid-export.json")))!;
        Action<JsonNode>[] mutations = [
            j => j.AsObject().Remove("exportedAt"),
            j => j["schemaVersion"] = 2,
            j => j["source"]!["gainUnits"] = "LUFS",
            j => j["roots"]!.AsArray().Add(j["roots"]![0]!.DeepClone()),
            j => j["tracks"]![0]!["rootId"] = "unknown",
            j => j["tracks"]![0]!["relativePath"] = "",
            j => j["tracks"]![0]!["relativePath"] = "../a.flac",
            j => j["tracks"]![0]!["relativePath"] = "C:/absolute.flac",
            j => j["tracks"]![0]!["relativePath"] = "Artist\\a.flac",
            j => j["tracks"]![0]!["trackGainDb"] = "NaN",
            j => j["tracks"]![0]!["trackGainDb"] = null,
            j => j["tracks"]![0]!.AsObject().Remove("sourceItemId"),
            j => j["tracks"]!.AsArray().Add(j["tracks"]![0]!.DeepClone()),
            j => j["coverage"]!["entriesExported"] = 999,
            j => j["coverage"]!["missingTrackGain"] = 999,
            j => j["coverage"]!["itemsSeen"] = 999,
            j => j["collisionKey"]!.AsObject().Remove("caseFold")
        ];
        foreach (var mutate in mutations)
        {
            var modified = original.DeepClone(); mutate(modified);
            try { ContractAssertions.Validate(Encoding.UTF8.GetBytes(modified.ToJsonString())); }
            catch (InvalidDataException) { continue; }
            catch (SafeErrorException) { continue; }
            throw new Exception("schema accepted invalid export");
        }
    }

    private static void CanonicalChecksAllFields(string dir)
    {
        var (version, items) = OfflineItemsReader.Read(Path.Combine(dir, "input-normal.json"));
        var doc = new ExportBuilder(PathMapper.ParseRoots([@"music=C:\Music"]), false, version, true).Build(items).Document!;
        ContractAssertions.CompareAllStableFields(doc, Path.Combine(dir, "expected-normal.json"));
        doc.Source.ServerVersion = "SYNTHETIC different valid version";
        try { ContractAssertions.CompareAllStableFields(doc, Path.Combine(dir, "expected-normal.json")); }
        catch (InvalidDataException) { return; }
        throw new Exception("canonical comparison ignored source field");
    }

    private static void TwoPages()
    {
        var calls = 0;
        using var c = new JellyfinApiClient("https://example.invalid/jellyfin", "SYNTHETIC-HEADER-ONLY", new FakeHttpHandler
        {
            Responder = req =>
            {
                calls++;
                Require(req.Method == HttpMethod.Get, "not read-only");
                Require(req.RequestUri!.AbsolutePath == "/jellyfin/Items", "lost prefix");
                Require(req.Headers.GetValues("X-Emby-Token").Single() == "SYNTHETIC-HEADER-ONLY", "auth header");
                Require(!req.RequestUri.Query.Contains("SYNTHETIC-HEADER-ONLY", StringComparison.Ordinal), "token query leak");
                Require(req.RequestUri.Query.Contains("sortBy=SortName", StringComparison.Ordinal), "stable sort");
                Require(req.RequestUri.Query.Contains("userId=user%20SYNTHETIC", StringComparison.Ordinal), "escaped userId");
                Require(req.RequestUri.Query.Contains($"startIndex={(calls == 1 ? 0 : 200)}", StringComparison.Ordinal), "pagination index");
                return Json(Page(calls == 1 ? 0 : 200, calls == 1 ? 200 : 1, 201));
            }
        });
        var items = c.FetchAudioItemsAsync("user SYNTHETIC", CancellationToken.None).GetAwaiter().GetResult();
        Require(calls == 2 && items.Count == 201 && items[^1].Id == "200", "not two pages");
    }

    private static void InvalidTotals()
    {
        foreach (var total in new[] { "MISSING", "null", "true", "\"1\"", "{}", "[]", "-1", "1.5", "2147483648" })
        {
            var field = total == "MISSING" ? "" : "\"TotalRecordCount\":" + total + ",";
            using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler { Responder = _ => Json("{" + field + "\"Items\":[]}") });
            ExpectSafe("total_missing_or_invalid", () => Fetch(c));
        }
    }
    private static void ChangedTotals()
    {
        foreach (var missing in new[] { false, true })
        {
            var calls = 0;
            using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
            { Responder = _ => ++calls == 1 ? Json(Page(0, 200, 201)) : Json(missing ? "{\"Items\":[]}" : Page(200, 1, 202)) });
            ExpectSafe(missing ? "total_missing_or_invalid" : "total_changed", () => Fetch(c));
            Require(calls == 2, "second page not reached");
        }
    }
    private static void CountAndDuplicate()
    {
        using (var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler { Responder = _ => Json(Page(0, 1, 2)) }))
            ExpectSafe("count_mismatch", () => Fetch(c));
        var calls = 0;
        using var duplicate = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => ++calls == 1 ? Json(Page(0, 200, 201)) : Json(Page(0, 1, 201)) });
        ExpectSafe("duplicate_item_id", () => Fetch(duplicate));
    }

    private static void StalledBody()
    {
        var stream = new TestStream(0, stall: true);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) } }, timeout: TimeSpan.FromMilliseconds(50));
        ExpectSafe("timeout", () => c.FetchAudioItemsAsync(null, safety.Token).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
        Require(stream.ReadCalls > 0 && stream.Disposed, "body not read/disposed");
    }
    private static void StalledHeaders()
    {
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new AsyncHandler(async (_, ct) =>
        { await Task.Delay(Timeout.Infinite, ct); return Json("{}"); }), timeout: TimeSpan.FromMilliseconds(50));
        ExpectSafe("timeout", () => c.FetchAudioItemsAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
    }
    private static void CallerCancellation()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TestStream(0, stall: true)) } }, timeout: TimeSpan.FromSeconds(2));
        try { Fetch(c, caller.Token); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { return; }
        throw new Exception("caller cancellation not preserved");
    }
    private static void StreamOversize()
    {
        var stream = new TestStream(JellyfinApiClient.MaxResponseBytes + 1);
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) } });
        ExpectSafe("response_too_large", () => Fetch(c));
        Require(stream.ReadCalls > 1 && stream.Disposed, "stream cap not exercised");
    }
    private static void DeclaredOversize()
    {
        var stream = new TestStream(0, stall: true);
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => { var content = new StreamContent(stream); content.Headers.ContentLength = JellyfinApiClient.MaxResponseBytes + 1; return new(HttpStatusCode.OK) { Content = content }; } });
        ExpectSafe("response_too_large", () => Fetch(c));
        Require(stream.ReadCalls == 0 && stream.Disposed, "oversize declared body read");
    }
    private static void ErrorBodyNotRead()
    {
        var stream = new TestStream(0, stall: true);
        using var c = new JellyfinApiClient("https://example.invalid/", "SYNTHETIC", new FakeHttpHandler
        { Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(stream) } });
        ExpectSafe("status_401", () => Fetch(c));
        Require(stream.ReadCalls == 0 && stream.Disposed, "error body read");
    }
    private static void InvalidBaseUrls()
    {
        foreach (var url in new[] { "http://example.invalid/", "https://u:p@example.invalid/", "https://example.invalid/?token=SYNTHETIC", "https://example.invalid/#x" })
        {
            try { JellyfinApiClient.NormalizeBaseUri(url, false); }
            catch (SafeErrorException) { continue; }
            throw new Exception("unsafe base URL accepted");
        }
    }

    private static void HeldCreateNewStream()
    {
        var dir = Directory.CreateTempSubdirectory("jle-held-");
        try
        {
            using var stream = ExportWriter.CreateExclusiveTempFile(dir.FullName);
            Require(stream.CanWrite && stream.Length == 0, "CreateNew stream not retained");
            stream.WriteByte(42); stream.Flush(true);
            Require(stream.Length == 1, "original stream not writable");
        }
        finally { dir.Delete(true); }
    }
    private static void OutputSymlink()
    {
        if (OperatingSystem.IsWindows()) return; // Windows creation needs privileges; target-PC check is explicit.
        var dir = Directory.CreateTempSubdirectory("jle-link-");
        try
        {
            var target = Path.Combine(dir.FullName, "target.txt"); File.WriteAllText(target, "SYNTHETIC untouched");
            var link = Path.Combine(dir.FullName, "out.json"); File.CreateSymbolicLink(link, target);
            var doc = new ExportBuilder(PathMapper.ParseRoots([@"music=C:\Music"]), false, "SYNTHETIC", false).Build([]).Document!;
            ExpectSafe("Refusing to use symlink as output path.", () => ExportWriter.WriteAtomic(doc, link, true));
            Require(File.ReadAllText(target) == "SYNTHETIC untouched", "symlink target touched");
        }
        finally { dir.Delete(true); }
    }
    private static void CoveragePermutations()
    {
        SourceItem[] items = [
            new() { Id = "1", Path = @"C:\Music\A.flac" },
            new() { Id = "2", Path = @"C:\Music\a.flac", NormalizationGain = 40, TrackGainStatus = GainStatus.Finite },
            new() { Id = "3", Path = @"C:\Music\A.FLAC", NormalizationGain = 2, TrackGainStatus = GainStatus.Finite }
        ];
        foreach (var a in Enumerable.Range(0, 3)) foreach (var b in Enumerable.Range(0, 3).Where(i => i != a))
        {
            var z = 3 - a - b;
            var doc = new ExportBuilder(PathMapper.ParseRoots([@"music=C:\Music"]), true, "SYNTHETIC", false).Build([items[a], items[b], items[z]]).Document!;
            Require(doc.Tracks.Count == 0 && doc.Coverage.Collisions == 3 && doc.Coverage.MissingTrackGain == 0 && doc.Coverage.OutOfRangeGains == 0, "order-dependent counts");
        }
    }
    private static void UnicodeNotice()
    {
        var license = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "notices", "unicode", "LICENSE.txt"));
        Require(license.Contains("UNICODE LICENSE V3", StringComparison.Ordinal) && license.Contains("Permission is hereby granted", StringComparison.Ordinal)
            && license.Contains("this copyright and permission notice appear", StringComparison.Ordinal), "incomplete permission notice");
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => respond(req, ct); }

    private sealed class TestStream(long remaining, bool stall = false) : Stream
    {
        public int ReadCalls { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadCalls++;
            if (stall) await Task.Delay(Timeout.Infinite, ct);
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(remaining, buffer.Length); remaining -= n; buffer.Span[..n].Fill((byte)' '); return n;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long x, SeekOrigin o) => throw new NotSupportedException();
        public override void SetLength(long l) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
