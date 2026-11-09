using System.Text;

namespace JellyfinLoudnessExporter;

internal static class PathMapper
{
    // Windows-invalid filename characters (excluding separators handled separately).
    private static readonly char[] InvalidFileChars = ['<', '>', '"', '|', '?', '*'];

    public static List<RootMapping> ParseRoots(IEnumerable<string> rootArgs)
    {
        var roots = new List<RootMapping>();
        var ids = new HashSet<string>(StringComparer.Ordinal); // exact ordinal — case-sensitive IDs

        foreach (var arg in rootArgs)
        {
            var eq = arg.IndexOf('=');
            if (eq <= 0 || eq == arg.Length - 1)
            {
                throw new SafeErrorException(SafeError.Category.Usage, "root_format");
            }

            var id = arg[..eq]; // do not trim id beyond exact token — but allow trim of whitespace-only mistakes
            id = id.Trim();
            var path = arg[(eq + 1)..]; // do NOT Trim the Windows path (trailing space is invalid and must fail closed)

            if (string.IsNullOrEmpty(id))
            {
                throw new SafeErrorException(SafeError.Category.Usage, "root_id_empty");
            }

            if (!ids.Add(id))
            {
                throw new SafeErrorException(SafeError.Category.Usage, "root_id_duplicate");
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
            throw new SafeErrorException(SafeError.Category.Usage, "root_required");
        }

        DetectOverlappingRoots(roots);
        return roots;
    }

    public static void ValidateRootId(string id)
    {
        if (id.Length > 64)
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_id_too_long");
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new SafeErrorException(SafeError.Category.Usage, "root_id_invalid_char");
            }
        }
    }

    public static string NormalizeWindowsAbsoluteRoot(string path)
    {
        // Do not Trim — trailing spaces/dots are invalid Windows segments and must fail closed.
        if (string.IsNullOrEmpty(path))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_path_empty");
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_path_nul");
        }

        ValidateWindowsPathSyntax(path, isRoot: true);

        // Strip only trailing separators for prefix construction (not spaces/dots).
        var trimmedSep = path.TrimEnd('\\', '/');
        if (trimmedSep.Length == 0)
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_path_empty");
        }

        // Preserve drive root form: C: -> C:/  and C:\ -> C:/
        if (trimmedSep.Length == 2 && char.IsAsciiLetter(trimmedSep[0]) && trimmedSep[1] == ':')
        {
            return char.ToUpperInvariant(trimmedSep[0]) + ":/";
        }

        if (!IsWindowsAbsolute(trimmedSep))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_not_absolute");
        }

        // Reject . / .. segments in roots
        var unifiedCheck = UnifySeparators(trimmedSep);
        foreach (var seg in SplitSegments(unifiedCheck, isAbsolute: true))
        {
            if (seg is "." or "..")
            {
                throw new SafeErrorException(SafeError.Category.Usage, "root_dot_segment");
            }
        }

        // Reject device namespaces: \\.\ \\?\ \\.\Pipe\ etc.
        if (IsDeviceNamespace(path))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "root_device_namespace");
        }

        var unified = UnifySeparators(trimmedSep);
        // Normalize drive letter to upper for stable OrdinalIgnoreCase prefix compare source.
        if (unified.Length >= 2 && char.IsAsciiLetter(unified[0]) && unified[1] == ':')
        {
            unified = char.ToUpperInvariant(unified[0]) + unified[1..];
        }

        return unified.TrimEnd('/') + "/";
    }

    public static bool IsDeviceNamespace(string path)
    {
        // \\.\ or \\?\ or //./ //?/
        if (path.Length >= 4)
        {
            var a = path[0];
            var b = path[1];
            var c = path[2];
            var d = path[3];
            if ((a == '\\' || a == '/') && (b == '\\' || b == '/') && (c == '.' || c == '?') && (d == '\\' || d == '/'))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsWindowsAbsolute(string path)
    {
        if (IsDeviceNamespace(path))
        {
            return false; // treated as forbidden, not a usable root
        }

        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        {
            return true;
        }

        // Bare drive root C: or C:\
        if (path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return true;
        }

        if (path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
        {
            return true;
        }

        // UNC \\server\share
        if ((path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) &&
            !IsDeviceNamespace(path))
        {
            var rest = path.TrimStart('\\', '/');
            var parts = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2;
        }

        return false;
    }

    public static string UnifySeparators(string path) => path.Replace('\\', '/');

    public static void DetectOverlappingRoots(IReadOnlyList<RootMapping> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                var a = roots[i].NormalizedPrefix;
                var b = roots[j].NormalizedPrefix;
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                    HasPrefixBoundary(a, b) || HasPrefixBoundary(b, a))
                {
                    throw new SafeErrorException(SafeError.Category.Usage, "root_overlap");
                }
            }
        }
    }

    private static bool HasPrefixBoundary(string prefixWithSlash, string otherWithSlash) =>
        otherWithSlash.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase);

    public static MapResult MapPath(string sourcePath, IReadOnlyList<RootMapping> roots)
    {
        // Do NOT Trim source Path.
        if (sourcePath.Length == 0)
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "empty_path" };
        }

        if (sourcePath.Contains('\0', StringComparison.Ordinal))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "nul_in_path" };
        }

        if (IsDeviceNamespace(sourcePath))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "device_namespace" };
        }

        try
        {
            ValidateWindowsPathSyntax(sourcePath, isRoot: false);
        }
        catch (SafeErrorException)
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "invalid_path_syntax" };
        }

        if (!IsWindowsAbsolute(sourcePath))
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "source_not_absolute" };
        }

        var unified = UnifySeparators(sourcePath);
        if (unified.Length >= 2 && char.IsAsciiLetter(unified[0]) && unified[1] == ':')
        {
            unified = char.ToUpperInvariant(unified[0]) + unified[1..];
        }

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

        if (string.Equals(unified.TrimEnd('/'), matched.NormalizedPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return new MapResult { Status = MapStatus.InvalidRelative, ErrorCode = "empty_relative" };
        }

        if (!unified.StartsWith(matched.NormalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new MapResult { Status = MapStatus.Unmatched, ErrorCode = "unmatched_root" };
        }

        // Direct slice by prefix length after OrdinalIgnoreCase match (prefix lengths equal in UTF-16 for ASCII drive roots).
        // For safety, verify character-by-character ignore-case then slice original unified source.
        var prefix = matched.NormalizedPrefix;
        if (unified.Length < prefix.Length)
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "prefix_length" };
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!CharEqualsIgnoreCase(unified[i], prefix[i]))
            {
                return new MapResult { Status = MapStatus.Rejected, ErrorCode = "prefix_mismatch" };
            }
        }

        var relative = unified[prefix.Length..]; // preserve exact residual case/Unicode
        var validation = ValidateRelative(relative);
        if (validation is not null)
        {
            return new MapResult { Status = MapStatus.InvalidRelative, ErrorCode = validation };
        }

        string collisionKey;
        try
        {
            collisionKey = UnicodeCaseFold.BuildCollisionKey(matched.Id, relative);
        }
        catch (InvalidDataException)
        {
            return new MapResult { Status = MapStatus.Rejected, ErrorCode = "malformed_unicode" };
        }

        return new MapResult
        {
            Status = MapStatus.Ok,
            RootId = matched.Id,
            RelativePath = relative,
            CollisionKey = collisionKey
        };
    }

    private static bool CharEqualsIgnoreCase(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    public static string? ValidateRelative(string relative)
    {
        if (relative.Length == 0)
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

        if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            return "drive_residual";
        }

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

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return "trailing_dot_or_space_segment";
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                return "colon_in_segment"; // ADS / alternate stream
            }

            foreach (var ch in InvalidFileChars)
            {
                if (segment.Contains(ch))
                {
                    return "invalid_char";
                }
            }
        }

        return null;
    }

    public static void ValidateWindowsPathSyntax(string path, bool isRoot)
    {
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new SafeErrorException(SafeError.Category.Mapping, "nul_in_path");
        }

        // Repeated separators (except leading UNC //)
        var unified = UnifySeparators(path);
        var start = 0;
        if (unified.StartsWith("//", StringComparison.Ordinal) && !IsDeviceNamespace(path))
        {
            start = 2;
        }

        if (unified.AsSpan(start).Contains("//", StringComparison.Ordinal))
        {
            throw new SafeErrorException(SafeError.Category.Mapping, "repeated_separator");
        }

        foreach (var seg in SplitSegments(unified, isAbsolute: true))
        {
            if (seg.Length == 0)
            {
                continue;
            }

            // Drive letter segment like C: at start is ok — handled as absolute
            if (seg.Length == 2 && char.IsAsciiLetter(seg[0]) && seg[1] == ':')
            {
                continue;
            }

            if (seg.Contains(':', StringComparison.Ordinal))
            {
                throw new SafeErrorException(SafeError.Category.Mapping, "colon_in_segment");
            }

            if (seg.EndsWith(' ') || (seg.EndsWith('.') && seg is not "." and not ".."))
            {
                throw new SafeErrorException(SafeError.Category.Mapping, "trailing_dot_or_space_segment");
            }

            foreach (var ch in InvalidFileChars)
            {
                if (seg.Contains(ch))
                {
                    throw new SafeErrorException(SafeError.Category.Mapping, "invalid_char");
                }
            }

            foreach (var ch in seg)
            {
                if (ch < 32)
                {
                    throw new SafeErrorException(SafeError.Category.Mapping, "control_char");
                }
            }
        }

        if (isRoot)
        {
            foreach (var seg in SplitSegments(UnifySeparators(path.TrimEnd('\\', '/')), isAbsolute: true))
            {
                if (seg is "." or "..")
                {
                    throw new SafeErrorException(SafeError.Category.Usage, "root_dot_segment");
                }
            }
        }
    }

    private static IEnumerable<string> SplitSegments(string unified, bool isAbsolute)
    {
        var s = unified;
        if (s.StartsWith("//", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        return s.Split('/', StringSplitOptions.None);
    }
}
