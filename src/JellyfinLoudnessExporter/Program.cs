using JellyfinLoudnessExporter;

static int Usage()
{
    Console.Error.WriteLine(
"""
Jellyfin Loudness Exporter — export portable loudness JSON from Jellyfin (or offline fixtures).

Usage:
  jellyfin-loudness-exporter --selftest [--fixtures <dir>]
  jellyfin-loudness-exporter --offline <items.json> --root id=WindowsPath [--root ...] --output <out.json> [options]
  jellyfin-loudness-exporter --api --base-url <url> --user-id <guid> --root id=WindowsPath [--root ...] --output <out.json> [options]

Options:
  --partial                 Allow unmatched/collisions/rejects; set coverage.partial=true
  --force                   Overwrite existing output file
  --server-version <text>   Override/set source.serverVersion (offline default from file)
  --lufs-reference-hint     Include informational source.lufsDerivedReferenceLufs=-18
  --user-id <guid>          Required for --api (Jellyfin /Items userId)
  --help                    Show help

Security:
  API token is read from a masked prompt into RAM only (never CLI args, config, log, or disk).
  Base URL must not contain credentials. HTTP redirects are refused.
  Export never contains server URL, token, username, or private absolute roots.
""");
    return 2;
}

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    return Usage();
}

try
{
    if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
    {
        var fixtures = GetOpt(args, "--fixtures") ?? FindDefaultFixtures();
        return SelfTest.Run(fixtures);
    }

    var offline = GetOpt(args, "--offline");
    var api = args.Contains("--api", StringComparer.OrdinalIgnoreCase);
    if (offline is null && !api)
    {
        Console.Error.WriteLine("Specify --offline <file>, --api, or --selftest.");
        return Usage();
    }

    if (offline is not null && api)
    {
        Console.Error.WriteLine("Use only one of --offline or --api.");
        return 2;
    }

    var output = GetOpt(args, "--output");
    if (output is null)
    {
        Console.Error.WriteLine("--output is required.");
        return 2;
    }

    var rootArgs = GetOpts(args, "--root");
    if (rootArgs.Count == 0)
    {
        // Default id music requires explicit path still — require at least one --root.
        Console.Error.WriteLine("At least one --root id=WindowsPath is required (default id is typically music).");
        return 2;
    }

    var roots = PathMapper.ParseRoots(rootArgs);
    var partial = args.Contains("--partial", StringComparer.OrdinalIgnoreCase);
    var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    var lufsHint = args.Contains("--lufs-reference-hint", StringComparer.OrdinalIgnoreCase);
    var serverVersionOverride = GetOpt(args, "--server-version");

    string serverVersion;
    List<SourceItem> items;

    if (offline is not null)
    {
        (serverVersion, items) = OfflineItemsReader.Read(offline);
        if (serverVersionOverride is not null)
        {
            serverVersion = serverVersionOverride;
        }
    }
    else
    {
        var baseUrl = GetOpt(args, "--base-url");
        var userId = GetOpt(args, "--user-id");
        if (baseUrl is null || userId is null)
        {
            Console.Error.WriteLine("--api requires --base-url and --user-id.");
            return 2;
        }

        var token = JellyfinApiClient.ReadTokenMasked();
        try
        {
            using var client = new JellyfinApiClient(baseUrl, token);
            serverVersion = serverVersionOverride ?? await client.GetServerVersionAsync(CancellationToken.None).ConfigureAwait(false);
            items = await client.FetchAudioItemsAsync(userId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            token = string.Empty;
        }
    }

    var builder = new ExportBuilder(roots, partial, serverVersion, lufsHint);
    var result = builder.Build(items);
    if (!result.Success || result.Document is null)
    {
        Console.Error.WriteLine($"Export blocked ({result.FailureCode}). Use --partial to write a partial file.");
        Console.Error.WriteLine(
            $"coverage: seen={result.Coverage.ItemsSeen} unmatched={result.Coverage.Unmatched} collisions={result.Coverage.Collisions} rejected={result.Coverage.Rejected}");
        return 1;
    }

    ExportWriter.WriteAtomic(result.Document, output, force);
    Console.WriteLine(
        $"Wrote {result.Document.Coverage.EntriesExported} tracks (partial={result.Document.Coverage.Partial}) to {Path.GetFileName(Path.GetFullPath(output))}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
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
        if (Directory.Exists(full))
        {
            return full;
        }
    }

    throw new DirectoryNotFoundException("Could not locate tests/fixtures. Pass --fixtures <dir>.");
}
