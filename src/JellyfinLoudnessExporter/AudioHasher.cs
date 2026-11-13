using System.Buffers.Binary;
using System.Security.Cryptography;

namespace JellyfinLoudnessExporter;

internal enum AudioHashStatus
{
    Ok,
    Unsupported,
    Malformed,
    Unreadable
}

internal readonly record struct AudioHashResult(AudioHashStatus Status, string? Sha256)
{
    public static AudioHashResult Ok(string hex) => new(AudioHashStatus.Ok, hex);
    public static AudioHashResult None(AudioHashStatus status) => new(status, null);
}

/// <summary>
/// jle-audio-v1: SHA-256 over the audio payload only (tags and cover art excluded).
/// The exact rules are frozen in docs/FORMAT.md, section "Audio hash jle-audio-v1".
/// Files are opened read-only and streamed; nothing is written.
/// </summary>
internal static class AudioHasher
{
    public const string AlgorithmId = "jle-audio-v1";
    public const int OggMaxPacket = 16 * 1024 * 1024;
    private const int ChunkSize = 1 << 20;

    private sealed class NoHashException(AudioHashStatus status) : Exception
    {
        public AudioHashStatus Status { get; } = status;
    }

    private static void Need(bool condition, AudioHashStatus status = AudioHashStatus.Malformed)
    {
        if (!condition) throw new NoHashException(status);
    }

    public static AudioHashResult HashFile(string path)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.SequentialScan,
                BufferSize = 0
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return AudioHashResult.None(AudioHashStatus.Unreadable);
        }

        using (fs)
        {
            try
            {
                return AudioHashResult.Ok(HashStream(fs));
            }
            catch (NoHashException e)
            {
                return AudioHashResult.None(e.Status);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return AudioHashResult.None(AudioHashStatus.Unreadable);
            }
        }
    }

    /// <summary>Stream must be readable and seekable.</summary>
    internal static AudioHashResult HashSeekable(Stream s)
    {
        try { return AudioHashResult.Ok(HashStream(s)); }
        catch (NoHashException e) { return AudioHashResult.None(e.Status); }
    }

    private static string HashStream(Stream s)
    {
        var r = new Reader(s);
        long length = r.Length;
        var head = r.ReadAt(0, 12);
        if (head.Length >= 12 && Ascii(head, 0, "RIFF") && Ascii(head, 8, "WAVE")) return HashWav(r, length);
        if (head.Length >= 4 && Ascii(head, 0, "OggS")) return HashOgg(r, length);
        if (head.Length >= 8 && Ascii(head, 4, "ftyp")) return HashMp4(r, length);

        long start = SkipLeadingId3v2(r, length);
        var magic = r.ReadAt(start, 4);
        if (magic.Length == 4 && Ascii(magic, 0, "fLaC")) return HashFlac(r, start, length);

        var hdr = Mp3Header(r.ReadAt(start, 4));
        Need(hdr is not null, AudioHashStatus.Unsupported);
        long end = StripTrailers(r, start, length);
        return HashMp3(r, start, end, hdr!.Value);
    }

    // ------------------------------------------------------------ helpers

    private sealed class Reader(Stream s)
    {
        public long Length { get; } = s.Length;

        public byte[] ReadAt(long offset, int count)
        {
            if (offset < 0 || offset >= Length || count <= 0) return [];
            var n = (int)Math.Min(count, Length - offset);
            var buf = new byte[n];
            s.Seek(offset, SeekOrigin.Begin);
            s.ReadExactly(buf, 0, n);
            return buf;
        }

        public void HashRange(IncrementalHash h, long start, long end, byte[] buf)
        {
            s.Seek(start, SeekOrigin.Begin);
            long left = end - start;
            while (left > 0)
            {
                var want = (int)Math.Min(buf.Length, left);
                var got = s.Read(buf, 0, want);
                Need(got > 0);
                h.AppendData(buf, 0, got);
                left -= got;
            }
        }
    }

    private static bool Ascii(byte[] b, int offset, string text)
    {
        if (b.Length < offset + text.Length) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (b[offset + i] != (byte)text[i]) return false;
        }

        return true;
    }

    private static string HashRanges(Reader r, IReadOnlyList<(long Start, long End)> ranges)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var buf = new byte[ChunkSize];
        foreach (var (start, end) in ranges)
        {
            Need(start >= 0 && end >= start && end <= r.Length);
            r.HashRange(h, start, end, buf);
            total += end - start;
        }

        Need(total > 0);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private static int SyncSafe(byte[] b, int offset) =>
        (b[offset] << 21) | (b[offset + 1] << 14) | (b[offset + 2] << 7) | b[offset + 3];

    private static bool SyncSafeValid(byte[] b, int offset) =>
        ((b[offset] | b[offset + 1] | b[offset + 2] | b[offset + 3]) & 0x80) == 0;

    // ------------------------------------------------------------ ID3v2 / trailers

    /// <summary>Total length of an ID3v2 tag starting with these 10 bytes, or null.</summary>
    private static long? Id3v2Length(byte[] h)
    {
        if (h.Length < 10 || !Ascii(h, 0, "ID3") || h[3] == 0xFF || h[4] == 0xFF || !SyncSafeValid(h, 6)) return null;
        long total = 10L + SyncSafe(h, 6);
        if ((h[5] & 0x10) != 0) total += 10;
        return total;
    }

    private static long SkipLeadingId3v2(Reader r, long length)
    {
        long pos = 0;
        while (Id3v2Length(r.ReadAt(pos, 10)) is long n)
        {
            Need(pos + n <= length);
            pos += n;
        }

        return pos;
    }

    private static long StripTrailers(Reader r, long start, long end)
    {
        while (true)
        {
            long avail = end - start;
            if (avail >= 128 && Ascii(r.ReadAt(end - 128, 3), 0, "TAG"))
            {
                end -= 128;
                if (end - start >= 227 && Ascii(r.ReadAt(end - 227, 4), 0, "TAG+")) end -= 227;
                continue;
            }

            if (avail >= 32)
            {
                var foot = r.ReadAt(end - 32, 32);
                if (Ascii(foot, 0, "APETAGEX"))
                {
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(foot.AsSpan(12));
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(foot.AsSpan(20));
                    Need(size >= 32);
                    long total = size + ((flags & 0x80000000u) != 0 ? 32 : 0);
                    Need(total <= avail);
                    end -= total;
                    continue;
                }
            }

            if (avail >= 15 && Ascii(r.ReadAt(end - 9, 9), 0, "LYRICS200"))
            {
                var digits = r.ReadAt(end - 15, 6);
                long size = 0;
                foreach (var d in digits)
                {
                    Need(d is >= (byte)'0' and <= (byte)'9');
                    size = size * 10 + (d - '0');
                }

                long total = size + 15;
                Need(total <= avail);
                Need(Ascii(r.ReadAt(end - total, 11), 0, "LYRICSBEGIN"));
                end -= total;
                continue;
            }

            if (avail >= 10)
            {
                var foot = r.ReadAt(end - 10, 10);
                if (Ascii(foot, 0, "3DI"))
                {
                    Need(foot[3] != 0xFF && foot[4] != 0xFF && SyncSafeValid(foot, 6));
                    long total = SyncSafe(foot, 6) + 20L;
                    Need(total <= avail);
                    Need(Ascii(r.ReadAt(end - total, 3), 0, "ID3"));
                    end -= total;
                    continue;
                }
            }

            return end;
        }
    }

    // ------------------------------------------------------------ MP3

    private static readonly int[] BitrateV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] BitrateV2L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    internal readonly record struct MpegFrame(int FrameLength, int SideInfoLength);

    internal static MpegFrame? Mp3Header(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return null;
        int version = (b[1] >> 3) & 3; // 0 = MPEG-2.5, 1 = reserved, 2 = MPEG-2, 3 = MPEG-1
        int layer = (b[1] >> 1) & 3;   // 1 = Layer III
        int bitrateIndex = b[2] >> 4;
        int rateIndex = (b[2] >> 2) & 3;
        int padding = (b[2] >> 1) & 1;
        bool mono = (b[3] >> 6) == 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;
        int sampleRate = version switch
        {
            3 => new[] { 44100, 48000, 32000 }[rateIndex],
            2 => new[] { 22050, 24000, 16000 }[rateIndex],
            _ => new[] { 11025, 12000, 8000 }[rateIndex]
        };
        if (version == 3)
            return new MpegFrame(144000 * BitrateV1L3[bitrateIndex] / sampleRate + padding, mono ? 17 : 32);
        return new MpegFrame(72000 * BitrateV2L3[bitrateIndex] / sampleRate + padding, mono ? 9 : 17);
    }

    private static string HashMp3(Reader r, long start, long end, MpegFrame frame)
    {
        Need(frame.FrameLength <= end - start);
        var f = r.ReadAt(start, frame.FrameLength);
        int xo = 4 + frame.SideInfoLength;
        bool tagFrame = (xo + 4 <= f.Length && (Ascii(f, xo, "Xing") || Ascii(f, xo, "Info")))
                        || (40 <= f.Length && Ascii(f, 36, "VBRI"));
        if (tagFrame) start += frame.FrameLength;
        return HashRanges(r, [(start, end)]);
    }

    // ------------------------------------------------------------ FLAC

    private static string HashFlac(Reader r, long start, long length)
    {
        long end = StripTrailers(r, start, length);
        long pos = start + 4;
        var first = true;
        while (true)
        {
            Need(pos + 4 <= end);
            var h = r.ReadAt(pos, 4);
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int blockLength = (h[1] << 16) | (h[2] << 8) | h[3];
            Need(type != 127);
            if (first)
            {
                Need(type == 0);
                first = false;
            }

            pos += 4L + blockLength;
            Need(pos <= end);
            if (last) break;
        }

        return HashRanges(r, [(pos, end)]);
    }

    // ------------------------------------------------------------ MP4

    private static string HashMp4(Reader r, long length)
    {
        long pos = 0;
        var ranges = new List<(long, long)>();
        while (pos < length)
        {
            Need(pos + 8 <= length);
            var h = r.ReadAt(pos, 8);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(h);
            int headerLength = 8;
            if (size == 1)
            {
                Need(pos + 16 <= length);
                size = BinaryPrimitives.ReadUInt64BigEndian(r.ReadAt(pos + 8, 8));
                headerLength = 16;
                Need(size >= 16);
            }
            else if (size == 0)
            {
                size = (ulong)(length - pos);
            }
            else
            {
                Need(size >= 8);
            }

            Need(size <= (ulong)(length - pos));
            if (Ascii(h, 4, "mdat")) ranges.Add((pos + headerLength, pos + (long)size));
            pos += (long)size;
        }

        Need(ranges.Count > 0);
        return HashRanges(r, ranges);
    }

    // ------------------------------------------------------------ WAV

    private static string HashWav(Reader r, long length)
    {
        long riffSize = BinaryPrimitives.ReadUInt32LittleEndian(r.ReadAt(4, 4));
        long extent = 8 + riffSize;
        Need(extent <= length);
        long pos = 12;
        (long, long)? data = null;
        while (pos + 8 <= extent)
        {
            var h = r.ReadAt(pos, 8);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            long payloadEnd = pos + 8 + size;
            Need(payloadEnd <= extent);
            if (Ascii(h, 0, "data"))
            {
                Need(data is null);
                data = (pos + 8, payloadEnd);
            }

            pos = payloadEnd + (size & 1);
        }

        Need(data is not null);
        return HashRanges(r, [data!.Value]);
    }

    // ------------------------------------------------------------ Ogg

    private static string HashOgg(Reader r, long length)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long pos = 0;
        uint serial = 0, seq0 = 0;
        long pageNo = 0;
        var eosSeen = false;
        var packetIndex = 0;
        MemoryStream? pending = null; // packet being assembled; null when no packet is pending
        long pendingLength = 0;
        var opus = false;
        var lenBuf = new byte[8];

        while (pos < length)
        {
            Need(!eosSeen, AudioHashStatus.Unsupported);
            Need(pos + 27 <= length);
            var ph = r.ReadAt(pos, 27);
            Need(Ascii(ph, 0, "OggS") && ph[4] == 0);
            int flags = ph[5];
            uint pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(ph.AsSpan(14));
            uint pageSeq = BinaryPrimitives.ReadUInt32LittleEndian(ph.AsSpan(18));
            int segments = ph[26];
            Need(pos + 27 + segments <= length);
            var lacing = r.ReadAt(pos + 27, segments);
            long bodyLength = 0;
            foreach (var lv in lacing) bodyLength += lv;
            Need(pos + 27 + segments + bodyLength <= length);
            if (pageNo == 0)
            {
                Need((flags & 0x02) != 0);
                serial = pageSerial;
                seq0 = pageSeq;
            }
            else
            {
                Need(pageSerial == serial, AudioHashStatus.Unsupported);
                Need((flags & 0x02) == 0, AudioHashStatus.Unsupported);
                Need(pageSeq == unchecked(seq0 + (uint)pageNo));
            }

            Need(((flags & 0x01) != 0) == (pending is not null));
            var body = r.ReadAt(pos + 27 + segments, (int)bodyLength);
            var bpos = 0;
            foreach (var lv in lacing)
            {
                pending ??= new MemoryStream();
                pendingLength += lv;
                if (packetIndex == 1)
                {
                    var keep = (int)Math.Min(lv, Math.Max(0, 8 - pending.Length));
                    pending.Write(body, bpos, keep);
                }
                else
                {
                    Need(pendingLength <= OggMaxPacket);
                    pending.Write(body, bpos, lv);
                }

                bpos += lv;
                if (lv < 255)
                {
                    var bytes = pending.ToArray();
                    if (packetIndex == 0)
                    {
                        if (Ascii(bytes, 0, "\u0001vorbis")) opus = false;
                        else if (Ascii(bytes, 0, "OpusHead")) { opus = true; Need(bytes.Length >= 19); }
                        else throw new NoHashException(AudioHashStatus.Unsupported);
                        if (opus) { bytes[16] = 0; bytes[17] = 0; }
                    }
                    else if (packetIndex == 2 && !opus)
                    {
                        Need(Ascii(bytes, 0, "\u0005vorbis"));
                    }

                    if (packetIndex == 1)
                    {
                        Need(opus ? Ascii(bytes, 0, "OpusTags") : Ascii(bytes, 0, "\u0003vorbis"));
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(lenBuf, (ulong)pendingLength);
                        h.AppendData(lenBuf);
                        h.AppendData(bytes);
                    }

                    packetIndex++;
                    pending = null;
                    pendingLength = 0;
                }
            }

            if ((flags & 0x04) != 0) eosSeen = true;
            pos += 27 + segments + bodyLength;
            pageNo++;
        }

        Need(pageNo > 0 && eosSeen && pending is null);
        Need(packetIndex >= (opus ? 3 : 4));
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
