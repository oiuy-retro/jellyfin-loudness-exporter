using System.Runtime.InteropServices;
using System.Text;

namespace JellyfinLoudnessExporter;

/// <summary>
/// Reads loudness from a local Jellyfin sqlite database. The connection is read-only.
/// Track and album gain follow Jellyfin: when LUFS is present, gain is -18 minus that
/// LUFS; otherwise the stored NormalizationGain is used. Album gain comes from the
/// parent MusicAlbum row, walking ParentId.
/// </summary>
internal static class JellyfinDatabaseReader
{
    private const string AudioSuffix = ".Audio";
    private const string AlbumSuffix = "MusicAlbum";

    internal static List<SourceItem> Read(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new SafeErrorException(SafeError.Category.Usage, "database_required");
        }

        using var db = SqliteDb.OpenReadOnly(databasePath);
        List<Row> rows;
        try
        {
            rows = db.ReadBaseItems();
        }
        catch (SafeErrorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SafeErrorException(SafeError.Category.Io, "database_read");
        }

        var byId = new Dictionary<string, Row>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id)) continue;
            byId.TryAdd(row.Id, row);
        }

        var items = new List<SourceItem>();
        foreach (var row in rows)
        {
            if (!IsAudio(row.Type) || string.IsNullOrEmpty(row.Id)) continue;

            var track = DeriveGain(row.Lufs, row.LufsStatus, row.Normalization, row.NormalizationStatus);
            var album = FindAlbumGain(row, byId);
            items.Add(new SourceItem
            {
                Id = row.Id,
                AlbumId = album.AlbumId,
                Path = row.Path ?? "",
                NormalizationGain = track.Value,
                AlbumNormalizationGain = album.Value,
                TrackGainStatus = track.Status,
                AlbumGainStatus = album.Status
            });
        }

        return items;
    }

    internal static void CreateFixture(string databasePath, string sql)
    {
        using var db = SqliteDb.OpenReadWriteCreate(databasePath);
        db.Exec(sql);
    }

    private static bool IsAudio(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (type.Contains("AudioBook", StringComparison.Ordinal) || type.Contains("Book", StringComparison.Ordinal))
        {
            return false;
        }

        return type == "Audio" || type.EndsWith(AudioSuffix, StringComparison.Ordinal);
    }

    private static bool IsAlbum(string? type)
    {
        return type == "MusicAlbum" || (type?.EndsWith(AlbumSuffix, StringComparison.Ordinal) ?? false);
    }

    private static (double? Value, GainStatus Status) DeriveGain(
        double? lufs, GainStatus lufsStatus, double? normalization, GainStatus normalizationStatus)
    {
        if (lufsStatus is GainStatus.InvalidType or GainStatus.NonFinite)
        {
            return (null, lufsStatus);
        }

        if (lufsStatus == GainStatus.Finite && lufs is double measured)
        {
            // Same float subtraction Jellyfin uses when it builds NormalizationGain from LUFS.
            var computed = -18f - (float)measured;
            if (!float.IsFinite(computed)) return (null, GainStatus.NonFinite);
            return (computed, GainStatus.Finite);
        }

        if (normalizationStatus == GainStatus.Finite && normalization is double stored)
        {
            return (stored, GainStatus.Finite);
        }

        if (normalizationStatus is GainStatus.InvalidType or GainStatus.NonFinite)
        {
            return (null, normalizationStatus);
        }

        return (null, GainStatus.Missing);
    }

    private static (string? AlbumId, double? Value, GainStatus Status) FindAlbumGain(
        Row start, Dictionary<string, Row> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parentId = start.ParentId;
        for (var depth = 0; depth < 32 && !string.IsNullOrEmpty(parentId); depth++)
        {
            if (!seen.Add(parentId)) break;
            if (!byId.TryGetValue(parentId, out var parent)) break;
            if (IsAlbum(parent.Type))
            {
                var gain = DeriveGain(parent.Lufs, parent.LufsStatus, parent.Normalization, parent.NormalizationStatus);
                return (parent.Id, gain.Value, gain.Status);
            }

            parentId = parent.ParentId;
        }

        return (null, null, GainStatus.Missing);
    }

    private sealed class Row
    {
        public required string Id { get; init; }
        public string? ParentId { get; init; }
        public string? Type { get; init; }
        public string? Path { get; init; }
        public double? Lufs { get; init; }
        public GainStatus LufsStatus { get; init; }
        public double? Normalization { get; init; }
        public GainStatus NormalizationStatus { get; init; }
    }

    private sealed class SqliteDb : IDisposable
    {
        private const int OpenReadonly = 0x00000001;
        private const int OpenReadWrite = 0x00000002;
        private const int OpenCreate = 0x00000004;
        private const int Ok = 0;
        private const int Busy = 5;
        private const int Locked = 6;
        private const int CantOpen = 14;
        private const int NotADb = 26;
        private const int Corrupt = 11;
        private const int Row = 100;
        private const int Done = 101;
        private const int NullType = 5;
        private const int IntegerType = 1;
        private const int FloatType = 2;

        private const string ItemSql =
            "SELECT CAST(Id AS TEXT), CAST(ParentId AS TEXT), Type, Path, LUFS, NormalizationGain FROM BaseItems";

        private IntPtr _db;

        private SqliteDb(IntPtr db) => _db = db;

        public static SqliteDb OpenReadOnly(string path) => Open(path, OpenReadonly);

        public static SqliteDb OpenReadWriteCreate(string path) => Open(path, OpenReadWrite | OpenCreate);

        public List<Row> ReadBaseItems()
        {
            var stmt = Prepare(ItemSql);
            try
            {
                var rows = new List<Row>();
                while (true)
                {
                    var rc = sqlite3_step(stmt);
                    if (rc == Done) break;
                    if (rc != Row) throw Map(rc);
                    rows.Add(new Row
                    {
                        Id = ColumnText(stmt, 0) ?? "",
                        ParentId = ColumnText(stmt, 1),
                        Type = ColumnText(stmt, 2),
                        Path = ColumnText(stmt, 3),
                        Lufs = ColumnDouble(stmt, 4, out var lufsStatus),
                        LufsStatus = lufsStatus,
                        Normalization = ColumnDouble(stmt, 5, out var normStatus),
                        NormalizationStatus = normStatus
                    });
                }

                return rows;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        public void Exec(string sql)
        {
            var bytes = Encoding.UTF8.GetBytes(sql + "\0");
            var rc = sqlite3_exec(_db, bytes, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc != Ok) throw Map(rc);
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero)
            {
                sqlite3_close(_db);
                _db = IntPtr.Zero;
            }
        }

        private static SqliteDb Open(string path, int flags)
        {
            var bytes = Encoding.UTF8.GetBytes(path + "\0");
            var rc = sqlite3_open_v2(bytes, out var db, flags, IntPtr.Zero);
            if (rc != Ok || db == IntPtr.Zero)
            {
                if (db != IntPtr.Zero) sqlite3_close(db);
                throw Map(rc == Ok ? CantOpen : rc);
            }

            sqlite3_busy_timeout(db, 5000);
            return new SqliteDb(db);
        }

        private IntPtr Prepare(string sql)
        {
            var bytes = Encoding.UTF8.GetBytes(sql + "\0");
            var rc = sqlite3_prepare_v2(_db, bytes, bytes.Length, out var stmt, IntPtr.Zero);
            if (rc != Ok || stmt == IntPtr.Zero)
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                throw new SafeErrorException(SafeError.Category.Io, rc is Busy or Locked ? "database_busy" : "database_schema");
            }

            return stmt;
        }

        private static string? ColumnText(IntPtr stmt, int index)
        {
            if (sqlite3_column_type(stmt, index) == NullType) return null;
            var ptr = sqlite3_column_text(stmt, index);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        }

        private static double? ColumnDouble(IntPtr stmt, int index, out GainStatus status)
        {
            var type = sqlite3_column_type(stmt, index);
            if (type == NullType)
            {
                status = GainStatus.Missing;
                return null;
            }

            if (type is not (IntegerType or FloatType))
            {
                status = GainStatus.InvalidType;
                return null;
            }

            var value = sqlite3_column_double(stmt, index);
            if (!double.IsFinite(value))
            {
                status = GainStatus.NonFinite;
                return null;
            }

            status = GainStatus.Finite;
            return value;
        }

        private static SafeErrorException Map(int rc) => rc switch
        {
            Busy or Locked => new SafeErrorException(SafeError.Category.Io, "database_busy"),
            NotADb or Corrupt => new SafeErrorException(SafeError.Category.Io, "database_invalid"),
            CantOpen => new SafeErrorException(SafeError.Category.Io, "database_open"),
            _ => new SafeErrorException(SafeError.Category.Io, "database_read")
        };

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int numBytes, out IntPtr stmt, IntPtr tail);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, IntPtr errmsg);
    }
}
