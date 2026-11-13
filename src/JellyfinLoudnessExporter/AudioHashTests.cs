using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

// Offline tests for jle-audio-v1. Fixtures are SYNTHETIC self-generated tones in
// tests/fixtures/audio/; expected hashes come from tests/tools/jle_audio_hash.py.
internal static class AudioHashTests
{
    public static int Run(string fixturesDir)
    {
        var failed = 0;
        failed += Check("audio_hash_vectors", () => Vectors(fixturesDir));
        failed += Check("audio_hash_ogg_packet_across_pages", OggPacketAcrossPages);
        failed += Check("audio_hash_ogg_bad_continuation", OggBadContinuation);
        failed += Check("audio_hash_ogg_multiplexed", OggMultiplexed);
        failed += Check("audio_hash_mp4_huge_sizes", Mp4HugeSizes);
        failed += Check("audio_hash_wav_streamed_64mib", WavStreamed);
        failed += Check("audio_hash_unreadable", () => Unreadable(fixturesDir));
        failed += Check("audio_hash_export_fields", () => ExportFields(fixturesDir));
        return failed;
    }

    private static int Check(string name, Action test)
    {
        try { test(); Console.WriteLine("  PASS " + name); return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }

    private static void Require(bool b, string what) { if (!b) throw new Exception(what); }

    private static string AudioDir(string fixturesDir) => Path.Combine(fixturesDir, "audio");

    private static void Vectors(string fixturesDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AudioDir(fixturesDir), "vectors.json")));
        Require(doc.RootElement.GetProperty("algorithm").GetString() == AudioHasher.AlgorithmId, "algorithm");
        var count = 0;
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var file = c.GetProperty("file").GetString()!;
            var expected = c.GetProperty("audioSha256").ValueKind == JsonValueKind.Null ? null : c.GetProperty("audioSha256").GetString();
            var actual = AudioHasher.HashFile(Path.Combine(AudioDir(fixturesDir), file));
            Require(actual.Sha256 == expected, $"{file}: expected {expected ?? "none"}, got {actual.Sha256 ?? actual.Status.ToString()}");
            Require((actual.Status == AudioHashStatus.Ok) == (expected is not null), file + " status");
            if (c.GetProperty("group").ValueKind == JsonValueKind.String && expected is not null)
            {
                var g = c.GetProperty("group").GetString()!;
                if (groups.TryGetValue(g, out var prev)) Require(prev == expected, "group " + g);
                else groups[g] = expected;
            }

            count++;
        }

        Require(count >= 30, "vector count");
    }

    // ------------------------------------------------------------ Ogg builders (CRC is ignored by the rules)

    private static byte[] OggPage(int flags, uint serial, uint seq, byte[] lacing, byte[] body)
    {
        var p = new byte[27 + lacing.Length + body.Length];
        Encoding.ASCII.GetBytes("OggS").CopyTo(p, 0);
        p[5] = (byte)flags;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(18), seq);
        p[26] = (byte)lacing.Length;
        lacing.CopyTo(p, 27);
        body.CopyTo(p, 27 + lacing.Length);
        return p;
    }

    private static byte[] OpusHead(short gain)
    {
        var h = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(h, 0);
        h[8] = 1; h[9] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(16), gain);
        return h;
    }

    private static byte[] Lacing(int length)
    {
        var l = new List<byte>();
        while (length >= 255) { l.Add(255); length -= 255; }
        l.Add((byte)length);
        return l.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    private static string ExpectedPackets(params byte[][] included)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var len = new byte[8];
        foreach (var p in included)
        {
            BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)p.Length);
            h.AppendData(len);
            h.AppendData(p);
        }

        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private static (byte[] File, string Expected) OggSpanning(bool breakContinuation)
    {
        var head = OpusHead(1234);
        var tags = Concat(Encoding.ASCII.GetBytes("OpusTags"), new byte[700]); // spans its own page boundary
        var audio = Enumerable.Range(0, 600).Select(i => (byte)(i * 7)).ToArray();
        var audio2 = new byte[] { 1, 2, 3 };
        // Page 1: OpusHead. Page 2: first 510 bytes of tags (lacing 255,255 -> continues).
        // Page 3: rest of tags + first 255 bytes of audio. Page 4: rest of audio + audio2, EOS.
        var p0 = OggPage(0x02, 7, 0, Lacing(head.Length), head);
        var p1 = OggPage(0x00, 7, 1, [255, 255], tags[..510]);
        var tagsRest = tags[510..];
        var p2 = OggPage(breakContinuation ? 0x00 : 0x01, 7, 2, Concat(Lacing(tagsRest.Length), [255]), Concat(tagsRest, audio[..255]));
        var restAudio = audio[255..];
        var p3 = OggPage(0x01 | 0x04, 7, 3, Concat(Lacing(restAudio.Length), Lacing(audio2.Length)), Concat(restAudio, audio2));
        var headZeroGain = (byte[])head.Clone();
        headZeroGain[16] = 0; headZeroGain[17] = 0;
        return (Concat(p0, p1, p2, p3), ExpectedPackets(headZeroGain, audio, audio2));
    }

    private static void OggPacketAcrossPages()
    {
        var (file, expected) = OggSpanning(false);
        var r = AudioHasher.HashSeekable(new MemoryStream(file));
        Require(r.Status == AudioHashStatus.Ok && r.Sha256 == expected, "spanning packet hash " + r.Status);
    }

    private static void OggBadContinuation()
    {
        var (file, _) = OggSpanning(true);
        Require(AudioHasher.HashSeekable(new MemoryStream(file)).Status == AudioHashStatus.Malformed, "missing continued flag");
    }

    private static void OggMultiplexed()
    {
        var head = OpusHead(0);
        var tags = Encoding.ASCII.GetBytes("OpusTags\0\0\0\0\0\0\0\0");
        var a = OggPage(0x02, 1, 0, Lacing(head.Length), head);
        var b = OggPage(0x00, 2, 0, Lacing(tags.Length), tags); // different serial
        var c = OggPage(0x04, 1, 1, [3], [9, 9, 9]);
        Require(AudioHasher.HashSeekable(new MemoryStream(Concat(a, b, c))).Status == AudioHashStatus.Unsupported, "multiplexed");
    }

    private static void Mp4HugeSizes()
    {
        var ftyp = Concat([0, 0, 0, 16], Encoding.ASCII.GetBytes("ftypM4A "), new byte[4]);
        var large = Concat([0, 0, 0, 1], Encoding.ASCII.GetBytes("mdat"), [0, 0, 0, 1, 64, 0, 0, 0], [1, 2, 3]); // 5 GiB claimed
        Require(AudioHasher.HashSeekable(new MemoryStream(Concat(ftyp, large))).Status == AudioHashStatus.Malformed, "5 GiB largesize");
        var max = Concat([0, 0, 0, 1], Encoding.ASCII.GetBytes("mdat"), Enumerable.Repeat((byte)0xFF, 8).ToArray());
        Require(AudioHasher.HashSeekable(new MemoryStream(Concat(ftyp, max))).Status == AudioHashStatus.Malformed, "2^64-1 largesize");
        var ok = Concat([0, 0, 0, 1], Encoding.ASCII.GetBytes("mdat"), [0, 0, 0, 0, 0, 0, 0, 19], [1, 2, 3]);
        var r = AudioHasher.HashSeekable(new MemoryStream(Concat(ftyp, ok)));
        Require(r.Sha256 == Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), "largesize payload");
    }

    /// <summary>Seekable read-only stream that synthesizes a RIFF/WAVE file without holding it in memory.</summary>
    private sealed class SyntheticWav(long dataLength) : Stream
    {
        private readonly byte[] _header = BuildHeader(dataLength);
        private long _pos;
        public static byte Pattern(long i) => (byte)(i * 31 + (i >> 9));
        private static byte[] BuildHeader(long n)
        {
            var h = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + n));
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
            Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)n);
            return h;
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 44 + dataLength;
        public override long Position { get => _pos; set => _pos = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Max(0, Math.Min(count, Length - _pos));
            for (var i = 0; i < n; i++)
            {
                var p = _pos + i;
                buffer[offset + i] = p < 44 ? _header[p] : Pattern(p - 44);
            }

            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => Length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void WavStreamed()
    {
        const long n = 64L * 1024 * 1024 + 3;
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        for (long done = 0; done < n;)
        {
            var k = (int)Math.Min(buf.Length, n - done);
            for (var i = 0; i < k; i++) buf[i] = SyntheticWav.Pattern(done + i);
            expected.AppendData(buf, 0, k);
            done += k;
        }

        var r = AudioHasher.HashSeekable(new SyntheticWav(n));
        Require(r.Sha256 == Convert.ToHexStringLower(expected.GetHashAndReset()), "streamed wav");
    }

    private static void Unreadable(string fixturesDir)
    {
        Require(AudioHasher.HashFile(Path.Combine(AudioDir(fixturesDir), "does-not-exist.flac")).Status == AudioHashStatus.Unreadable, "missing");
        Require(AudioHasher.HashFile(AudioDir(fixturesDir)).Status == AudioHashStatus.Unreadable, "directory");
        Require(AudioHasher.HashFile(@"\\server.invalid\share\x.flac").Status == AudioHashStatus.Unreadable, "network path");
    }

    private static void ExportFields(string fixturesDir)
    {
        var dir = AudioDir(fixturesDir);
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [@"C:\Music\a\01.flac"] = Path.Combine(dir, "flac-base.flac"),
            [@"C:\Music\a\02.mp3"] = Path.Combine(dir, "mp3-all-trailers.mp3"),
            [@"C:\Music\b\03.opus"] = Path.Combine(dir, "opus-chained.opus"),
            [@"C:\Music\b\04.flac"] = Path.Combine(dir, "flac-retagged-cover.flac"),
        };
        var items = new List<SourceItem>
        {
            new() { Id = "i1", Path = @"C:\Music\a\01.flac", NormalizationGain = -1, TrackGainStatus = GainStatus.Finite },
            new() { Id = "i2", Path = @"C:\Music\a\02.mp3", NormalizationGain = -2, TrackGainStatus = GainStatus.Finite },
            new() { Id = "i3", Path = @"C:\Music\b\03.opus" },
            new() { Id = "i4", Path = @"C:\Music\b\04.flac" },
            new() { Id = "i5", Path = @"C:\Music\b\05-missing.flac" },
        };
        var roots = PathMapper.ParseRoots(["music=C:\\Music"]);
        AudioHashResult Hasher(string p) => map.TryGetValue(p, out var real) ? AudioHasher.HashFile(real) : AudioHasher.HashFile(Path.Combine(dir, "missing", "x"));
        var result = new ExportBuilder(roots, false, null, false, "jellyfin-database", Hasher).Build(items);
        Require(result.Success && result.Document is not null, "build");
        var d = result.Document!;
        Require(d.AudioHashAlgorithm == "jle-audio-v1", "algorithm field");
        Require(d.Coverage.Hashed == 3 && d.Coverage.HashUnavailable == 2, "coverage counts");
        Require(!d.Coverage.Partial && d.Coverage.Errors.Count == 0, "hash failures must not block or add errors");
        var byId = d.Tracks.ToDictionary(t => t.SourceItemId!);
        Require(byId["i1"].AudioSha256 == byId["i4"].AudioSha256 && byId["i1"].AudioSha256 is { Length: 64 }, "flac same hash");
        Require(byId["i2"].AudioSha256 is { Length: 64 }, "mp3 hash");
        Require(byId["i3"].AudioSha256 is null && byId["i5"].AudioSha256 is null, "unavailable omitted");
        Require(result.HashUnavailableByStatus[AudioHashStatus.Unsupported] == 1 &&
                result.HashUnavailableByStatus[AudioHashStatus.Unreadable] == 1, "status summary");
        var json = JsonSerializer.SerializeToUtf8Bytes(d);
        ContractAssertions.Validate(json);
        var text = Encoding.UTF8.GetString(json);
        Require(!text.Contains(dir, StringComparison.Ordinal) && !text.Contains("C:", StringComparison.Ordinal), "no absolute paths");
        using var parsed = JsonDocument.Parse(json);
        Require(!parsed.RootElement.GetProperty("tracks")[2].TryGetProperty("audioSha256", out _), "field omitted, not null");

        var plain = new ExportBuilder(roots, false, null, false, "jellyfin-database").Build(items).Document!;
        var plainText = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(plain));
        Require(!plainText.Contains("audioHashAlgorithm") && !plainText.Contains("audioSha256") && !plainText.Contains("hashUnavailable"), "disabled by default");
    }
}
