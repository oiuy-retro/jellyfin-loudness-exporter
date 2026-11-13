using JellyfinLoudnessExporter;

static int Usage()
{
    Console.Error.WriteLine(
"""
Jellyfin Loudness Exporter — portable loudness JSON from Jellyfin or offline fixtures.

Usage:
  jellyfin-loudness-exporter --selftest [--fixtures <dir>]
  jellyfin-loudness-exporter --audio-hash-file <file> [<file> ...]
  jellyfin-loudness-exporter --offline <items.json> --root id=WindowsPath [--root ...] --output <out.json> [options]
  jellyfin-loudness-exporter --database <jellyfin.db> --root id=WindowsPath [--root ...] --output <out.json> [options]
  jellyfin-loudness-exporter --api --base-url <url> [--user-id <guid>] --root id=WindowsPath --output <out.json> [options]

Options:
  --partial                 Allow unmatched/collisions/rejects; coverage.partial=true
  --force                   Replace existing output via atomic overwrite move
  --server-version <text>   Set source.serverVersion when known (omit if unknown)
  --lufs-reference-hint     Include informational source.lufsDerivedReferenceLufs=-18
  --allow-http              Allow http:// base URL (prints a warning). Prefer https.
  --audio-hash              Read each exported track's file (read-only) and add audioSha256
                            (jle-audio-v1, tags and cover art excluded). Files that cannot be
                            read or parsed get no hash and are counted in coverage.hashUnavailable.
  --user-id <guid>          Optional for API-key mode; required when /Users/Me is unavailable
  --help                    Show help

Windows PowerShell examples (quote roots):
  .\jellyfin-loudness-exporter.exe --offline .\fixtures\input-normal.json --root "music=C:\Music" --output .\out.json --lufs-reference-hint
  .\jellyfin-loudness-exporter.exe --database .\jellyfin.db --root "music=C:\Music" --output .\out.json --lufs-reference-hint
  .\jellyfin-loudness-exporter.exe --database .\jellyfin.db --root "music=C:\Music" --output .\out.json --audio-hash
  .\jellyfin-loudness-exporter.exe --api --base-url "https://jellyfin.example/jellyfin" --user-id "..." --root "music=C:\Music" --output .\out.json

Security: token from masked prompt only (never args/config/log/disk). No credentials in URLs. Redirects refused. Errors are safe categories only.
""");
    return 2;
}

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    return Usage();
}

try
{
    if (string.Equals(args[0], "--audio-hash-file", StringComparison.OrdinalIgnoreCase))
    {
        // Diagnostic: print the jle-audio-v1 hash of each named file, one line per file, in order.
        // Output is the 64-hex hash or "none:<reason>"; file paths are not echoed.
        if (args.Length < 2) return Usage();
        foreach (var file in args.Skip(1))
        {
            var r = AudioHasher.HashFile(file);
            Console.WriteLine(r.Sha256 ?? "none:" + r.Status.ToString().ToLowerInvariant());
        }

        return 0;
    }

    if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
    {
        var fixtures = GetOpt(args, "--fixtures") ?? FindDefaultFixtures();
        return SelfTest.Run(fixtures);
    }

    var offline = GetOpt(args, "--offline");
    var database = GetOpt(args, "--database");
    var api = args.Contains("--api", StringComparer.OrdinalIgnoreCase);
    var modeCount = (offline is not null ? 1 : 0) + (database is not null ? 1 : 0) + (api ? 1 : 0);
    if (modeCount == 0)
    {
        Console.Error.WriteLine(SafeError.Format(SafeError.Category.Usage, "mode_required"));
        return Usage();
    }

    if (modeCount > 1)
    {
        Console.Error.WriteLine(SafeError.Format(SafeError.Category.Usage, "mode_conflict"));
        return 2;
    }

    var output = GetOpt(args, "--output");
    if (output is null)
    {
        Console.Error.WriteLine(SafeError.Format(SafeError.Category.Usage, "output_required"));
        return 2;
    }

    var rootArgs = GetOpts(args, "--root");
    if (rootArgs.Count == 0)
    {
        Console.Error.WriteLine(SafeError.Format(SafeError.Category.Usage, "root_required"));
        return 2;
    }

    var roots = PathMapper.ParseRoots(rootArgs);
    var partial = args.Contains("--partial", StringComparer.OrdinalIgnoreCase);
    var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    var lufsHint = args.Contains("--lufs-reference-hint", StringComparer.OrdinalIgnoreCase);
    var allowHttp = args.Contains("--allow-http", StringComparer.OrdinalIgnoreCase);
    var serverVersionOverride = GetOpt(args, "--server-version");
    var audioHash = args.Contains("--audio-hash", StringComparer.OrdinalIgnoreCase);

    string? serverVersion;
    List<SourceItem> items;
    var sourceKind = "jellyfin-api";

    if (offline is not null)
    {
        (serverVersion, items) = OfflineItemsReader.Read(offline);
        if (serverVersionOverride is not null) serverVersion = serverVersionOverride;
    }
    else if (database is not null)
    {
        serverVersion = serverVersionOverride;
        items = JellyfinDatabaseReader.Read(database);
        sourceKind = "jellyfin-database";
    }
    else
    {
        var baseUrl = GetOpt(args, "--base-url");
        if (baseUrl is null)
        {
            Console.Error.WriteLine(SafeError.Format(SafeError.Category.Usage, "base_url_required"));
            return 2;
        }

        if (allowHttp)
        {
            Console.Error.WriteLine("http_warn:non_https_base_url_allowed");
        }

        var userId = GetOpt(args, "--user-id");
        var token = JellyfinApiClient.ReadTokenMasked();
        try
        {
            using var client = new JellyfinApiClient(baseUrl, token, allowHttp: allowHttp);
            serverVersion = serverVersionOverride ?? await client.GetServerVersionAsync(CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = await client.TryGetUserIdFromMeAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // API-key mode: userId may remain null; server may still accept /Items.
            items = await client.FetchAudioItemsAsync(userId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            token = string.Empty;
        }
    }

    var builder = new ExportBuilder(roots, partial, serverVersion, lufsHint, sourceKind,
        audioHash ? AudioHasher.HashFile : null,
        audioHash ? static (done, total) =>
        {
            if (done % 500 == 0 || done == total)
                Console.Error.WriteLine($"audio_hash_progress:{done}/{total}");
        } : null);
    var result = builder.Build(items);
    if (!result.Success || result.Document is null)
    {
        Console.Error.WriteLine(SafeError.Format(SafeError.Category.Mapping, result.FailureCode ?? "export_blocked"));
        Console.Error.WriteLine(
            $"coverage_summary:seen={result.Coverage.ItemsSeen};unmatched={result.Coverage.Unmatched};collisions={result.Coverage.Collisions};rejected={result.Coverage.Rejected}");
        return 1;
    }

    if (audioHash)
    {
        var by = result.HashUnavailableByStatus;
        Console.Error.WriteLine(
            $"audio_hash_summary:hashed={result.Document.Coverage.Hashed};unavailable={result.Document.Coverage.HashUnavailable};" +
            $"unsupported={by.GetValueOrDefault(AudioHashStatus.Unsupported)};malformed={by.GetValueOrDefault(AudioHashStatus.Malformed)};" +
            $"unreadable={by.GetValueOrDefault(AudioHashStatus.Unreadable)}");
    }

    ExportWriter.WriteAtomic(result.Document, output, force);
    Console.WriteLine(
        $"Wrote {result.Document.Coverage.EntriesExported} tracks (partial={result.Document.Coverage.Partial})");
    return 0;
}
catch (SafeErrorException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(SafeError.Format(SafeError.Category.Http, "canceled"));
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine(SafeError.Format(SafeError.Category.Internal, "unexpected"));
    return 1;
}

static string? GetOpt(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static List<string> GetOpts(string[] args, string name)
{
    var list = new List<string>();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(args[i + 1]);
        }
    }

    return list;
}

static string FindDefaultFixtures()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "fixtures"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures"),
        Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures"),
        Path.Combine(Directory.GetCurrentDirectory(), "fixtures")
    };
    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (Directory.Exists(full)) return full;
    }

    throw new SafeErrorException(SafeError.Category.Usage, "fixtures_not_found");
}
