using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JellyfinLoudnessExporter;

internal static class ExportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static void WriteAtomic(ExportDocument document, string outputPath, bool force)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required.");
        }

        var full = Path.GetFullPath(outputPath);
        if (File.Exists(full) && !force)
        {
            throw new IOException($"Output file already exists (refusing to overwrite without --force): {Path.GetFileName(full)}");
        }

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = full + ".tmp";
        var json = JsonSerializer.Serialize(document, Options);
        var bytes = Encoding.UTF8.GetBytes(json); // UTF-8 no BOM

        try
        {
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(full))
            {
                if (!force)
                {
                    throw new IOException("Output file appeared during write; refusing to overwrite.");
                }

                File.Delete(full);
            }

            File.Move(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }
    }
}
