#!/usr/bin/env python3
"""Regenerate the SYNTHETIC audio fixtures in tests/fixtures/audio/.

Every clip is a short sine tone made by FFmpeg's lavfi `sine` source. Tags,
cover art, ID3v1/APEv2/Lyrics3v2 trailers, Xing/VBRI frames, Ogg gain edits
and malformed copies are produced by FFmpeg stream copy or by the byte
helpers below. Expected hashes come from tests/tools/jle_audio_hash.py.

Re-running this script with a different FFmpeg build can produce different
encoded bytes (and so different hashes). The committed files and
vectors.json are the reference; this script documents how they were made.

Usage: make_audio_fixtures.py [OUTPUT_DIR]
"""
import base64
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import jle_audio_hash as ref  # noqa: E402

OUT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else
                      os.path.join(HERE, "..", "fixtures", "audio"))
TMP = tempfile.mkdtemp(prefix="jle-fx-")
BITEXACT = ["-fflags", "+bitexact", "-flags:a", "+bitexact"]


def ff(*args):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)


def out(name):
    return os.path.join(OUT, name)


def tmp(name):
    return os.path.join(TMP, name)


def rd(p):
    with open(p, "rb") as f:
        return f.read()


def wr(p, b):
    with open(p, "wb") as f:
        f.write(b)


# ------------------------------------------------------------ tag builders

def syncsafe(n):
    return bytes([(n >> 21) & 0x7F, (n >> 14) & 0x7F, (n >> 7) & 0x7F, n & 0x7F])


def id3v23_frame(fid, data):
    return fid.encode() + struct.pack(">I", len(data)) + b"\x00\x00" + data


def id3v2_tag(title, picture=None, version=3, footer=False, padding=0):
    frames = id3v23_frame("TIT2", b"\x00" + title.encode("latin-1"))
    frames += id3v23_frame("COMM", b"\x00eng\x00SYNTHETIC test tone")
    if picture:
        frames += id3v23_frame("APIC", b"\x00image/png\x00\x03\x00" + picture)
    if version == 4:
        # v2.4 frames use syncsafe frame sizes.
        def f4(fid, data):
            return fid.encode() + syncsafe(len(data)) + b"\x00\x00" + data
        frames = f4("TIT2", b"\x03" + title.encode()) + f4("COMM", b"\x03eng\x00SYNTHETIC test tone")
        if picture:
            frames += f4("APIC", b"\x00image/png\x00\x03\x00" + picture)
    body = frames + b"\x00" * padding
    flags = 0x10 if footer else 0
    hdr = b"ID3" + bytes([version, 0, flags]) + syncsafe(len(body))
    tag = hdr + body
    if footer:
        tag += b"3DI" + bytes([version, 0, flags]) + syncsafe(len(body))
    return tag


def id3v1(title):
    t = b"TAG" + title.encode("latin-1")[:30].ljust(30, b"\x00")
    t += b"SYNTHETIC".ljust(30, b"\x00") + b"Tones".ljust(30, b"\x00") + b"2026"
    t += b"test tone".ljust(28, b"\x00") + b"\x00\x01" + b"\x0c"
    assert len(t) == 128
    return t


def id3v1_enhanced(title):
    t = b"TAG+" + title.encode("latin-1").ljust(60, b"\x00") + b"SYNTHETIC".ljust(60, b"\x00")
    t += b"Tones".ljust(60, b"\x00") + b"\x00" + b"Test".ljust(30, b"\x00") + b"000:00" + b"000:01"
    assert len(t) == 227
    return t


def apev2(items, header=True):
    body = b""
    for k, v in items:
        vb = v.encode()
        body += struct.pack("<II", len(vb), 0) + k.encode() + b"\x00" + vb
    size = len(body) + 32
    def hf(is_header):
        flags = (0x80000000 if header else 0) | (0x20000000 if is_header else 0)
        return b"APETAGEX" + struct.pack("<IIII", 2000, size, len(items), flags) + b"\x00" * 8
    return (hf(True) if header else b"") + body + hf(False)


def lyrics3v2(text):
    fields = b"IND00002" + b"00" + b"LYR" + ("%05d" % len(text)).encode() + text.encode()
    content = b"LYRICSBEGIN" + fields
    return content + ("%06d" % len(content)).encode() + b"LYRICS200"


def flac_picture_block(png):
    mime = b"image/png"
    desc = b"SYNTHETIC cover"
    return (struct.pack(">II", 3, len(mime)) + mime + struct.pack(">I", len(desc)) + desc +
            struct.pack(">IIIII", 16, 16, 24, 0, len(png)) + png)


# ------------------------------------------------------------ Ogg helpers

def _crc_table():
    t = []
    for i in range(256):
        r = i << 24
        for _ in range(8):
            r = ((r << 1) ^ 0x04C11DB7) if r & 0x80000000 else (r << 1)
        t.append(r & 0xFFFFFFFF)
    return t


CRC = _crc_table()


def ogg_crc(page):
    c = 0
    for b in page:
        c = ((c << 8) & 0xFFFFFFFF) ^ CRC[((c >> 24) & 0xFF) ^ b]
    return c


def ogg_pages(data):
    pos = 0
    pages = []
    while pos < len(data):
        nseg = data[pos + 26]
        blen = sum(data[pos + 27:pos + 27 + nseg])
        end = pos + 27 + nseg + blen
        pages.append(bytearray(data[pos:end]))
        pos = end
    return pages


def ogg_fix_crc(page):
    page[22:26] = b"\x00\x00\x00\x00"
    page[22:26] = struct.pack("<I", ogg_crc(bytes(page)))
    return page


def opus_set_gain(data, gain_q8):
    pages = ogg_pages(data)
    p = pages[0]
    nseg = p[26]
    body = 27 + nseg
    assert p[body:body + 8] == b"OpusHead"
    p[body + 16:body + 18] = struct.pack("<h", gain_q8)
    ogg_fix_crc(p)
    return b"".join(bytes(x) for x in pages)


def ogg_set_serial(data, serial):
    pages = ogg_pages(data)
    for p in pages:
        p[14:18] = struct.pack("<I", serial)
        ogg_fix_crc(p)
    return b"".join(bytes(x) for x in pages)


# ------------------------------------------------------------ MP3 helpers

def first_frame(data, start):
    hdr = ref.mpeg_l3_header(data[start:start + 4])
    assert hdr, "no MPEG frame at %d" % start
    return hdr


def mp3_audio_start(data):
    pos = 0
    while data[pos:pos + 3] == b"ID3":
        pos += ref.id3v2_len(data[pos:pos + 10])
    return pos


def make_tag_frame(template_hdr4, flen, magic, offset, crc=False):
    h = bytearray(template_hdr4)
    if crc:
        h[1] &= 0xFE  # protection bit 0 -> CRC present
    else:
        h[1] |= 0x01
    frame = bytearray(flen)
    frame[0:4] = h
    frame[offset:offset + 4] = magic
    return bytes(frame)


# ------------------------------------------------------------ generation

def main():
    if os.path.isdir(OUT):
        for n in os.listdir(OUT):
            if n.endswith((".flac", ".mp3", ".opus", ".ogg", ".oga", ".m4a", ".wav", ".bin")):
                os.remove(out(n))
    os.makedirs(OUT, exist_ok=True)
    ff("-f", "lavfi", "-i", "color=c=red:s=16x16", "-frames:v", "1", tmp("cover.png"))
    png = rd(tmp("cover.png"))
    sine = lambda d=1, r=44100, f=440: ["-f", "lavfi", "-i", f"sine=frequency={f}:duration={d}:sample_rate={r}"]
    md_a = ["-metadata", "title=SYNTHETIC tone A", "-metadata", "artist=SYNTHETIC"]
    md_b = ["-metadata", "title=SYNTHETIC tone B retagged", "-metadata", "artist=Someone Else",
            "-metadata", "album=SYNTHETIC album", "-metadata", "comment=different tags"]
    cases = []

    def case(name, group, notes, expect_hash=True):
        cases.append({"file": name, "group": group, "expectHash": expect_hash, "notes": notes})

    # ---------------- FLAC
    ff(*sine(0.5), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "flac", out("flac-base.flac"))
    ff("-i", out("flac-base.flac"), "-i", tmp("cover.png"), "-map", "0:a", "-map", "1:v", "-c", "copy",
       "-disposition:v", "attached_pic", *BITEXACT, "-map_metadata", "-1", *md_b, out("flac-retagged-cover.flac"))
    wr(out("flac-id3v2-id3v1.flac"), id3v2_tag("SYNTHETIC id3 on flac") + rd(out("flac-base.flac")) + id3v1("SYNTHETIC"))
    b = rd(out("flac-retagged-cover.flac"))
    wr(out("flac-truncated-metadata.flac"), b[:200])
    case("flac-base.flac", "flac", "FLAC 0.5 s mono tone, Vorbis comment")
    case("flac-retagged-cover.flac", "flac", "same frames, different Vorbis comment and a PICTURE block")
    case("flac-id3v2-id3v1.flac", "flac", "flac-base with a leading ID3v2.3 tag and a trailing ID3v1 tag")
    case("flac-truncated-metadata.flac", None, "cut inside the metadata blocks", False)

    # ---------------- MP3 MPEG-1 stereo, CBR (Info frame)
    ff(*sine(1), "-ac", "2", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "libmp3lame", "-b:a", "64k",
       "-id3v2_version", "4", out("mp3-mpeg1-stereo-info.mp3"))
    ff("-i", out("mp3-mpeg1-stereo-info.mp3"), "-i", tmp("cover.png"), "-map", "0:a", "-map", "1:v", "-c", "copy",
       "-disposition:v", "attached_pic", *BITEXACT, "-map_metadata", "-1", *md_b, "-id3v2_version", "3",
       "-write_id3v1", "1", out("mp3-mpeg1-stereo-retagged-cover.mp3"))
    ff("-i", out("mp3-mpeg1-stereo-info.mp3"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1",
       "-write_xing", "0", "-id3v2_version", "0", tmp("noxing.mp3"))
    raw = rd(tmp("noxing.mp3"))
    assert raw[:3] != b"ID3"
    wr(out("mp3-mpeg1-stereo-noxing-notags.mp3"), raw)
    hdr4 = raw[0:4]
    flen, side = first_frame(raw, 0)
    # ID3v2 + audio + Lyrics3v2 + APEv2 (with header) + ID3v1 + enhanced TAG+ before it.
    wr(out("mp3-all-trailers.mp3"),
       id3v2_tag("SYNTHETIC all trailers", png, padding=64) + raw + lyrics3v2("SYNTHETIC lyrics") +
       apev2([("Title", "SYNTHETIC ape"), ("Artist", "SYNTHETIC")]) + id3v1_enhanced("SYNTHETIC ext") + id3v1("SYNTHETIC v1"))
    # APEv2 without header, then appended ID3v2.4 with footer at the very end.
    wr(out("mp3-ape-noheader-appended-id3v24.mp3"),
       id3v2_tag("SYNTHETIC lead", version=4) + id3v2_tag("SYNTHETIC second lead", version=4, footer=True) + raw +
       apev2([("Title", "SYNTHETIC ape v1-style")], header=False) + id3v2_tag("SYNTHETIC appended", version=4, footer=True))
    # Synthetic VBRI frame and CRC-protected Xing frames.
    wr(out("mp3-vbri-frame.mp3"), make_tag_frame(hdr4, flen, b"VBRI", 36) + raw)
    wr(out("mp3-xing-crc-lame-offset.mp3"), make_tag_frame(hdr4, flen, b"Xing", 4 + side, crc=True) + raw)
    wr(out("mp3-xing-crc-plus2-not-excluded.mp3"), make_tag_frame(hdr4, flen, b"Xing", 4 + side + 2, crc=True) + raw)
    case("mp3-mpeg1-stereo-info.mp3", "mp3-mpeg1-stereo", "MPEG-1 stereo 64 kbit/s CBR, LAME Info frame, ID3v2.4")
    case("mp3-mpeg1-stereo-retagged-cover.mp3", "mp3-mpeg1-stereo", "stream copy: new Info frame, ID3v2.3 with APIC, ID3v1")
    case("mp3-mpeg1-stereo-noxing-notags.mp3", "mp3-mpeg1-stereo", "same frames, no Info frame, no tags")
    case("mp3-all-trailers.mp3", "mp3-mpeg1-stereo", "ID3v2.3+APIC, Lyrics3v2, APEv2 with header, TAG+, ID3v1")
    case("mp3-ape-noheader-appended-id3v24.mp3", "mp3-mpeg1-stereo", "two leading ID3v2.4 (second has footer), APEv2 without header, appended ID3v2.4 with 3DI footer")
    case("mp3-vbri-frame.mp3", "mp3-mpeg1-stereo", "synthetic first frame with VBRI at offset 36")
    case("mp3-xing-crc-lame-offset.mp3", "mp3-mpeg1-stereo", "CRC-protected first frame, Xing at 4+32 (LAME/FFmpeg/mpg123 position)")
    case("mp3-xing-crc-plus2-not-excluded.mp3", "mp3-xing-crc-plus2", "CRC-protected first frame, Xing only at 4+32+2: not a tag frame, hashed as audio")

    # ---------------- MP3 MPEG-1 mono VBR (Xing frame)
    ff(*sine(0.5), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "libmp3lame", "-q:a", "6",
       out("mp3-mpeg1-mono-vbr.mp3"))
    ff("-i", out("mp3-mpeg1-mono-vbr.mp3"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1",
       "-write_xing", "0", "-id3v2_version", "0", out("mp3-mpeg1-mono-noxing.mp3"))
    case("mp3-mpeg1-mono-vbr.mp3", "mp3-mpeg1-mono", "MPEG-1 mono VBR, Xing/Info at 4+17")
    case("mp3-mpeg1-mono-noxing.mp3", "mp3-mpeg1-mono", "same frames without the tag frame or tags")

    # ---------------- MP3 MPEG-2 mono and MPEG-2.5 stereo
    ff(*sine(0.5, 22050), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "libmp3lame", "-b:a", "32k",
       out("mp3-mpeg2-mono-info.mp3"))
    ff("-i", out("mp3-mpeg2-mono-info.mp3"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1",
       "-write_xing", "0", "-id3v2_version", "0", out("mp3-mpeg2-mono-noxing.mp3"))
    ff(*sine(0.5, 11025), "-ac", "2", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "libmp3lame", "-b:a", "16k",
       out("mp3-mpeg25-stereo-info.mp3"))
    ff("-i", out("mp3-mpeg25-stereo-info.mp3"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1",
       "-write_xing", "0", "-id3v2_version", "0", out("mp3-mpeg25-stereo-noxing.mp3"))
    r2 = rd(out("mp3-mpeg2-mono-noxing.mp3"))
    fl2, _ = first_frame(r2, 0)
    wr(out("mp3-mpeg2-mono-vbri-frame.mp3"), make_tag_frame(r2[0:4], fl2, b"VBRI", 36) + r2)
    case("mp3-mpeg2-mono-info.mp3", "mp3-mpeg2-mono", "MPEG-2 22.05 kHz mono, tag frame at 4+9")
    case("mp3-mpeg2-mono-vbri-frame.mp3", "mp3-mpeg2-mono", "synthetic VBRI first frame (offset 36, distinct from the Xing offset 13)")
    case("mp3-mpeg2-mono-noxing.mp3", "mp3-mpeg2-mono", "same frames without the tag frame")
    case("mp3-mpeg25-stereo-info.mp3", "mp3-mpeg25-stereo", "MPEG-2.5 11.025 kHz stereo, tag frame at 4+17")
    case("mp3-mpeg25-stereo-noxing.mp3", "mp3-mpeg25-stereo", "same frames without the tag frame")

    # ---------------- MP3 malformed
    m = rd(out("mp3-mpeg1-stereo-info.mp3"))
    a0 = mp3_audio_start(m)
    wr(out("mp3-truncated-first-frame.mp3"), m[:a0 + 100])
    bad = bytearray(id3v2_tag("SYNTHETIC")) + raw
    bad[6:10] = syncsafe(len(bad))  # ID3v2 size runs past end of file
    wr(out("mp3-id3v2-size-past-eof.mp3"), bytes(bad))
    bad_ape = raw + apev2([("Title", "SYNTHETIC")])
    bad_ape = bytearray(bad_ape)
    bad_ape[-32 + 12:-32 + 16] = struct.pack("<I", len(bad_ape) + 1000)
    wr(out("mp3-apev2-size-too-large.mp3"), bytes(bad_ape))
    case("mp3-truncated-first-frame.mp3", None, "file ends inside the first frame", False)
    case("mp3-id3v2-size-past-eof.mp3", None, "leading ID3v2 size points past end of file", False)
    case("mp3-apev2-size-too-large.mp3", None, "APEv2 footer size larger than the file", False)

    # ---------------- Ogg Opus
    opus_args = ["-c:a", "libopus", "-b:a", "24k", "-vbr", "off"]
    ff(*sine(1, 48000), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, *opus_args, out("opus-base.opus"))
    pic = base64.b64encode(flac_picture_block(png)).decode()
    ff("-i", out("opus-base.opus"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1", *md_b,
       "-metadata", "METADATA_BLOCK_PICTURE=" + pic, "-page_duration", "200000", out("opus-retagged-cover.opus"))
    wr(out("opus-output-gain-edited.opus"), opus_set_gain(rd(out("opus-base.opus")), 3 * 256))
    o = rd(out("opus-base.opus"))
    wr(out("opus-chained.opus"), o + ogg_set_serial(rd(out("opus-retagged-cover.opus")), 0x53594E54))
    wr(out("opus-truncated.opus"), o[:len(o) - 300])
    case("opus-base.opus", "opus", "Opus 1 s mono, OpusTags with title")
    case("opus-retagged-cover.opus", "opus", "stream copy: different OpusTags with METADATA_BLOCK_PICTURE, different paging")
    case("opus-output-gain-edited.opus", "opus", "OpusHead output gain set to +3 dB (bytes 16-17), page CRC recomputed")
    case("opus-chained.opus", None, "two logical streams concatenated (chained)", False)
    case("opus-truncated.opus", None, "last page cut short", False)

    # ---------------- Ogg Vorbis
    ff(*sine(1, 22050), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "libvorbis", "-q:a", "0",
       out("vorbis-base.ogg"))
    ff("-i", out("vorbis-base.ogg"), "-map", "0:a", "-c", "copy", *BITEXACT, "-map_metadata", "-1", *md_b,
       "-metadata", "METADATA_BLOCK_PICTURE=" + pic, out("vorbis-retagged-cover.ogg"))
    v = rd(out("vorbis-base.ogg"))
    wr(out("vorbis-truncated.ogg"), v[:len(v) // 2])
    case("vorbis-base.ogg", "vorbis", "Vorbis 1 s mono")
    case("vorbis-retagged-cover.ogg", "vorbis", "stream copy: different comment header with METADATA_BLOCK_PICTURE")
    case("vorbis-truncated.ogg", None, "cut in the middle of a page", False)

    # ---------------- MP4 / M4A
    ff(*sine(1, 22050), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "aac", "-b:a", "32k",
       out("m4a-base.m4a"))
    ff("-i", out("m4a-base.m4a"), "-i", tmp("cover.png"), "-map", "0:a", "-map", "1:v", "-c", "copy",
       "-disposition:v", "attached_pic", *BITEXACT, "-map_metadata", "-1", *md_b, "-movflags", "+faststart",
       out("m4a-retagged-cover-faststart.m4a"))
    mm = bytearray(rd(out("m4a-base.m4a")))
    # FFmpeg writes an 8-byte `free` box before `mdat`; turn free+mdat headers into one 64-bit mdat header.
    i = mm.find(b"free")
    assert i >= 4 and mm[i + 4 + 4:i + 4 + 8] == b"mdat"
    fstart = i - 4
    msize = struct.unpack(">I", mm[fstart + 8:fstart + 12])[0]
    mm[fstart:fstart + 16] = struct.pack(">I", 1) + b"mdat" + struct.pack(">Q", msize + 8)
    wr(out("m4a-mdat-largesize.m4a"), bytes(mm))
    fs = bytearray(rd(out("m4a-retagged-cover-faststart.m4a")))
    j = fs.rfind(b"mdat") - 4
    assert j + struct.unpack(">I", fs[j:j + 4])[0] == len(fs)
    fs[j:j + 4] = b"\x00\x00\x00\x00"
    wr(out("m4a-mdat-size0-to-eof.m4a"), bytes(fs))
    base = rd(out("m4a-base.m4a"))
    wr(out("m4a-truncated.m4a"), base[:len(base) - 500])
    case("m4a-base.m4a", "m4a", "AAC-LC 1 s mono in MP4, mdat after moov is not required")
    case("m4a-retagged-cover-faststart.m4a", "m4a", "stream copy: different ilst tags with covr, moov moved before mdat")
    case("m4a-mdat-largesize.m4a", "m4a", "same file with free+mdat rewritten as a 64-bit largesize mdat header")
    case("m4a-mdat-size0-to-eof.m4a", "m4a", "last-box mdat with size 0 (extends to end of file)")
    case("m4a-truncated.m4a", None, "cut short; the last box runs past end of file", False)

    # ---------------- WAV
    ff(*sine(1, 8000), "-ac", "1", *BITEXACT, "-map_metadata", "-1", *md_a, "-c:a", "pcm_s16le", out("wav-base.wav"))
    w = rd(out("wav-base.wav"))
    assert w[:4] == b"RIFF" and w[8:12] == b"WAVE"
    # Rebuild: fmt, an odd-sized junk chunk with pad byte, data, then an "id3 " chunk holding ID3v2 with APIC.
    pos, chunks = 12, []
    while pos + 8 <= len(w):
        cid, sz = w[pos:pos + 4], struct.unpack("<I", w[pos + 4:pos + 8])[0]
        chunks.append((cid, w[pos + 8:pos + 8 + sz]))
        pos += 8 + sz + (sz & 1)
    fmt = [c for c in chunks if c[0] == b"fmt "][0]
    data = [c for c in chunks if c[0] == b"data"][0]
    def ck(cid, payload):
        return cid + struct.pack("<I", len(payload)) + payload + (b"\x00" if len(payload) & 1 else b"")
    body = b"WAVE" + ck(*fmt) + ck(b"junk", b"SYNTHETIC odd") + ck(*data) + ck(b"id3 ", id3v2_tag("SYNTHETIC wav", png))
    wr(out("wav-retagged-cover.wav"), b"RIFF" + struct.pack("<I", len(body)) + body)
    nodata = b"WAVE" + ck(*fmt)
    wr(out("wav-no-data-chunk.wav"), b"RIFF" + struct.pack("<I", len(nodata)) + nodata)
    case("wav-base.wav", "wav", "PCM 16-bit 8 kHz mono with LIST/INFO")
    case("wav-retagged-cover.wav", "wav", "same samples; odd-sized junk chunk with pad byte, id3 chunk with APIC")
    case("wav-no-data-chunk.wav", None, "RIFF/WAVE without a data chunk", False)

    # ---------------- other
    wr(out("unsupported-text.bin"), b"SYNTHETIC not audio\n" * 8)
    case("unsupported-text.bin", None, "not a supported container", False)

    # ---------------- compute and check expectations
    groups = {}
    for c in cases:
        hx, why = ref.audio_hash(out(c["file"]))
        c["audioSha256"] = hx
        c["reason"] = why
        if c["expectHash"] != (hx is not None):
            raise SystemExit(f"unexpected result for {c['file']}: {hx} {why}")
        if c["group"]:
            groups.setdefault(c["group"], set()).add(hx)
    for g, hs in groups.items():
        if len(hs) != 1:
            raise SystemExit(f"group {g} hashes differ: {hs}")
    if groups["mp3-xing-crc-plus2"] == groups["mp3-mpeg1-stereo"]:
        raise SystemExit("CRC+2 frame should not be excluded")
    vectors = {
        "note": "SYNTHETIC self-generated test tones. Expected values from tests/tools/jle_audio_hash.py.",
        "algorithm": "jle-audio-v1",
        "cases": [{k: c[k] for k in ("file", "group", "audioSha256", "notes")} for c in cases],
    }
    with open(out("vectors.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(vectors, f, indent=2)
        f.write("\n")
    for c in cases:
        print(f"{(c['audioSha256'] or 'NONE')[:16]:16} {os.path.getsize(out(c['file'])):7}  {c['file']}  {c['reason']}")
    shutil.rmtree(TMP)


if __name__ == "__main__":
    main()
