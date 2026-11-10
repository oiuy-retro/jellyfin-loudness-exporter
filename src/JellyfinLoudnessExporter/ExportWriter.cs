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

    /// <summary>
    /// Write UTF-8 (no BOM) via an exclusively created unique same-directory temp file,
    /// then replace/move into place. Prior output is preserved if the final replace fails.
    /// Never uses a predictable ".tmp" sibling name.
    /// </summary>
    public static void WriteAtomic(ExportDocument document, string outputPath, bool force)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "Output path is required.");
        }

        var full = Path.GetFullPath(outputPath);
        RefuseSymlink(full, "output");

        if (File.Exists(full) && !force)
        {
            throw new SafeErrorException(SafeError.Category.Io, "Output file already exists (use --force to replace).");
        }

        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir))
        {
            throw new SafeErrorException(SafeError.Category.Io, "Output directory is invalid.");
        }

        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(document, Options);
        var bytes = Encoding.UTF8.GetBytes(json); // no BOM

        string? tmp = null;
        try
        {
            // Keep the exclusively created stream; never reopen a potentially replaced pathname.
            using (var fs = CreateExclusiveTempFile(dir))
            {
                tmp = fs.Name;
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // Re-check existence after write (race). Preserve prior file on failure.
            if (File.Exists(full))
            {
                if (!force)
                {
                    throw new SafeErrorException(SafeError.Category.Io, "Output file appeared during write; refusing to overwrite.");
                }

                // Same-directory overwrite move; never delete-then-move. Windows behavior still needs device verification.
                File.Move(tmp, full, overwrite: true);
                tmp = null;
            }
            else
            {
                File.Move(tmp, full);
                tmp = null;
            }
        }
        catch
        {
            // Prior output untouched if we never successfully moved over it.
            throw;
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }
    }

    internal static FileStream CreateExclusiveTempFile(string directory)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var name = Path.Combine(directory, $".jle-{Guid.NewGuid():N}.tmp");
            try
            {
                return new FileStream(
                    name,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.None);
            }
            catch (IOException)
            {
                // collision — retry
            }
        }

        throw new SafeErrorException(SafeError.Category.Io, "Could not create exclusive temp file.");
    }

    private static void RefuseSymlink(string path, string label)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                throw new SafeErrorException(SafeError.Category.Io, $"Refusing to use symlink as {label} path.");
            }
        }
        catch (FileNotFoundException)
        {
            // ok — does not exist yet
        }
        catch (DirectoryNotFoundException)
        {
            // ok
        }
    }
}
