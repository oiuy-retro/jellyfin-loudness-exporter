using System.Globalization;
using System.Text;

namespace JellyfinLoudnessExporter;

internal static class PathMapper
{
    public static List<RootMapping> ParseRoots(IEnumerable<string> rootArgs)
    {
        var roots = new List<RootMapping>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var arg in rootArgs)
        {
            var eq = arg.IndexOf('=');
            if (eq <= 0 || eq == arg.Length - 1)
            {
                throw new ArgumentException($"Root must be id=WindowsPath, got: {SanitizeForMessage(arg)}");
            }

            var id = arg[..eq].Trim();
            var path = arg[(eq + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Root id must be non-empty.");
            }

            if (!ids.Add(id))
            {
                throw new ArgumentException($"Duplicate root id: {id}");
            }

            ValidateRootId(id);
            var normalized = NormalizeWindowsAbsoluteRoot(path);
            roots.Add(new RootMapping
            {
                Id = id,
                AbsoluteWindowsPath = path,
                NormalizedPrefix = normalized
            });
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one root is required (example: music=C:\\Music).");
        }

        DetectOverlappingRoots(roots);
        return roots;
    }

    public static void ValidateRootId(string id)
    {
        if (id.Length > 64)
        {
            throw new ArgumentException("Root id is too long.");
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new ArgumentException($"Root id contains invalid character: {id}");
            }
        }
    }

    public static string NormalizeWindowsAbsoluteRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Root path is empty.");
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Root path contains NUL.");
        }

        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (!IsWindowsAbsolute(trimmed))
        {
            throw new ArgumentException("Root path must be a Windows drive or UNC absolute path.");
        }

        // Boundary-aware prefix uses trailing backslash after normalization to slash form for compare.
        var unified = UnifySeparators(trimmed);
        return unified.TrimEnd('/') + "/";
    }

    public static bool IsWindowsAbsolute(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        {
            return true;
        }

        // UNC \\server\share
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            var rest = path.TrimStart('\\', '/');
            var parts = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2;
        }

        return false;
    }

    public static string UnifySeparators(string path)
    {
        return path.Replace('\\', '/');
    }

    public static void DetectOverlappingRoots(IReadOnlyList<RootMapping> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var a = roots[i].NormalizedPrefix;
                var b = roots[j].NormalizedPrefix;
                if (HasPrefixBoundary(a, b) || HasPrefixBoundary(b, a) ||
                    string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Overlapping or identical roots are not allowed: '{roots[i].Id}' and '{roots[j].Id}'.");
                }
            }
        }
    }

    private static bool HasPrefixBoundary(string prefixWithSlash, string otherWithSlash)
    {
        // prefix C:/Music/ matches C:/Music/Album/ but not C:/MusicBackup/
        return otherWithSlash.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    public static MapResult MapPath(string sourcePath, IReadOnlyList<RootMapping> roots)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "empty_path" };
        }

        if (sourcePath.Contains('\0', StringComparison.Ordinal))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "nul_in_path" };
        }

        if (!IsWindowsAbsolute(sourcePath.Trim()))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "source_not_absolute" };
        }

        var unified = UnifySeparators(sourcePath.Trim());
        RootMapping? matched = null;
        foreach (var root in roots)
        {
            if (unified.StartsWith(root.NormalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(unified.TrimEnd('/'), root.NormalizedPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                if (matched is not null)
                {
                    return new MapResult { Status = MapStatus.AmbiguousRoot, ErrorCode = "ambiguous_root" };
                }

                matched = root;
            }
        }

        if (matched is null)
        {
            return new MapResult { Status = MapStatus.Unmatched, ErrorCode = "unmatched_root" };
        }

        string relative;
        var prefix = matched.NormalizedPrefix;
        if (string.Equals(unified.TrimEnd('/'), prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return new MapResult { Status = MapStatus.InvalidRelative, ErrorCode = "empty_relative" };
        }

        if (!unified.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            // Exact folder match without child — already handled; otherwise fail closed.
            return new MapResult { Status = MapStatus.Unmatched, ErrorCode = "unmatched_root" };
        }

        // Preserve exact case/Unicode from the source path residual (after slash unify only).
        // Use the source unified string's residual slice so casing of the residual is preserved.
        // Prefix match was case-insensitive; take residual by length of matched absolute form in source.
        relative = ExtractRelativePreservingCase(unified, prefix);
        var validation = ValidateRelative(relative);
        if (validation is not null)
        {
            return new MapResult { Status = MapStatus.InvalidRelative, ErrorCode = validation };
        }

        var collisionKey = BuildCollisionKey(matched.Id, relative);
        return new MapResult
        {
            Status = MapStatus.Ok,
            RootId = matched.Id,
            RelativePath = relative,
            CollisionKey = collisionKey
        };
    }

    internal static string ExtractRelativePreservingCase(string unifiedSource, string normalizedPrefix)
    {
        // Find prefix length in a case-insensitive way, then slice original unified source.
        if (unifiedSource.Length < normalizedPrefix.Length)
        {
            throw new InvalidOperationException("Internal path prefix length mismatch.");
        }

        // Walk comparing ignore-case; require boundary already ensured by caller.
        for (var i = 0; i < normalizedPrefix.Length; i++)
        {
            var a = char.ToLowerInvariant(unifiedSource[i]);
            var b = char.ToLowerInvariant(normalizedPrefix[i]);
            if (a != b)
            {
                throw new InvalidOperationException("Internal path prefix mismatch.");
            }
        }

        return unifiedSource[normalizedPrefix.Length..];
    }

    public static string? ValidateRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return "empty_relative";
        }

        if (relative.Contains('\0', StringComparison.Ordinal))
        {
            return "nul_in_relative";
        }

        if (relative.StartsWith('/'))
        {
            return "absolute_residual";
        }

        // Drive residual e.g. C:/...
        if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            return "drive_residual";
        }

        // UNC residual
        if (relative.StartsWith("//", StringComparison.Ordinal))
        {
            return "unc_residual";
        }

        var segments = relative.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return "empty_segment";
            }

            if (segment is "." or "..")
            {
                return "dot_segment";
            }
        }

        return null;
    }

    public static string BuildCollisionKey(string rootId, string relativePath)
    {
        var norm = relativePath.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{rootId.ToLowerInvariant()}\u001f{norm}");
    }

    public static string SanitizeForMessage(string value)
    {
        if (value.Length > 80)
        {
            return value[..80] + "…";
        }

        return value;
    }
}
