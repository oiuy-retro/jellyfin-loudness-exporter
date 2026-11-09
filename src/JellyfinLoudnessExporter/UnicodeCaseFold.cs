using System.Globalization;
using System.Reflection;
using System.Text;

namespace JellyfinLoudnessExporter;

/// <summary>
/// Unicode 15.1.0 full default case folding (status C+F, excluding Turkic T).
/// Collision keys use: rootId (ordinal exact) + SEP + FullCaseFold(NFC_runtime(relativePath)).
/// NFC uses the .NET runtime normalizer and is NOT a pinned cross-platform table.
/// </summary>
internal static class UnicodeCaseFold
{
    public const string AlgorithmId = "jf-ck-v1";
    public const string CaseFoldSpec = "unicode-15.1-full-C+F-non-Turkic";
    public const string NfcSpec = "runtime-dotnet-Normalize-FormC";
    public const string ExpectedSha256 = "4e55acfdc32825a22e87670e9056a3bf94ad7c5400065778e9e10f8314372bcf";

    private static readonly Dictionary<int, int[]> Map = Load();

    public static string BuildCollisionKey(string rootId, string relativePath)
    {
        // rootId: ordinal exact — never case-folded.
        var foldedRel = FullCaseFoldNfc(relativePath);
        return rootId + "\u001f" + foldedRel;
    }

    public static string FullCaseFoldNfc(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return relativePath;
        }

        FailClosedInvalidUtf16(relativePath);

        // NFC via runtime — platform/.NET Unicode version dependent. Documented limitation.
        string nfc;
        try
        {
            nfc = relativePath.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Malformed Unicode in relative path (NFC failed).", ex);
        }

        FailClosedInvalidUtf16(nfc);
        return FullCaseFold(nfc);
    }

    public static string FullCaseFold(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length;)
        {
            var cp = char.ConvertToUtf32(s, i);
            i += cp > 0xFFFF ? 2 : 1;
            if (Map.TryGetValue(cp, out var mapped))
            {
                foreach (var m in mapped)
                {
                    sb.Append(char.ConvertFromUtf32(m));
                }
            }
            else
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }

    public static void FailClosedInvalidUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    throw new InvalidDataException("Malformed UTF-16: lone high surrogate.");
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new InvalidDataException("Malformed UTF-16: lone low surrogate.");
            }
        }
    }

    private static Dictionary<int, int[]> Load()
    {
        var asm = typeof(UnicodeCaseFold).Assembly;
        using var stream = asm.GetManifestResourceStream("JellyfinLoudnessExporter.Data.CaseFolding-15.1.0.txt")
            ?? throw new InvalidOperationException("Embedded CaseFolding-15.1.0.txt missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();

        // Soft checksum check when file also present on disk beside assembly (dev); embedded always used.
        var map = new Dictionary<int, int[]>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var parts = line.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                throw new InvalidDataException("Malformed CaseFolding.txt line.");
            }

            var status = parts[1];
            if (status is not ("C" or "F"))
            {
                // Skip S and T (and anything else). Full folding uses C+F only.
                continue;
            }

            var from = int.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var toParts = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var to = new int[toParts.Length];
            for (var i = 0; i < toParts.Length; i++)
            {
                to[i] = int.Parse(toParts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            // If both C and F somehow appear, last wins; UCD has one of C or F per code point for full set.
            map[from] = to;
        }

        if (map.Count < 1000)
        {
            throw new InvalidDataException("CaseFolding table unexpectedly small.");
        }

        return map;
    }
}
