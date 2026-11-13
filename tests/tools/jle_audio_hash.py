#!/usr/bin/env python3
"""Reference implementation of the jle-audio-v1 audio hash (docs/FORMAT.md).

Written separately from the C# exporter so the two can be cross-checked.
Standard library only. Reads the file; never modifies it.

Usage: jle_audio_hash.py FILE [FILE ...]
Prints "<hash-or-NONE>  <reason>  <file>" per file.
"""
import hashlib
import struct
import sys

OGG_MAX_PACKET = 16 * 1024 * 1024
CHUNK = 1 << 20


class NoHash(Exception):
    """The file gets no hash. The message is a short reason code."""


def need(cond, reason):
    if not cond:
        raise NoHash(reason)


def hash_ranges(f, ranges):
    h = hashlib.sha256()
    total = 0
    for start, end in ranges:
        f.seek(start)
        left = end - start
        while left > 0:
            b = f.read(min(CHUNK, left))
            need(len(b) > 0, "malformed")
            h.update(b)
            left -= len(b)
            total += len(b)
    need(total > 0, "malformed")
    return h.hexdigest()


def read_at(f, off, n):
    f.seek(off)
    b = f.read(n)
    return b


# ---------------------------------------------------------------- ID3v2

def id3v2_len(hdr):
    """hdr: 10 bytes. Return total tag length (header+body+footer) or None."""
    if len(hdr) < 10 or hdr[0:3] != b"ID3":
        return None
    if hdr[3] == 0xFF or hdr[4] == 0xFF:
        return None
    if any(x & 0x80 for x in hdr[6:10]):
        return None
    size = (hdr[6] << 21) | (hdr[7] << 14) | (hdr[8] << 7) | hdr[9]
    total = 10 + size
    if hdr[5] & 0x10:
        total += 10
    return total


def skip_leading_id3v2(f, length):
    pos = 0
    while True:
        n = id3v2_len(read_at(f, pos, 10))
        if n is None:
            return pos
        need(pos + n <= length, "malformed")
        pos += n


# ---------------------------------------------------------------- trailers

def strip_trailers(f, start, end):
    while True:
        avail = end - start
        if avail >= 128 and read_at(f, end - 128, 3) == b"TAG":
            end -= 128
            if end - start >= 227 and read_at(f, end - 227, 4) == b"TAG+":
                end -= 227
            continue
        if avail >= 32 and read_at(f, end - 32, 8) == b"APETAGEX":
            foot = read_at(f, end - 32, 32)
            size = struct.unpack_from("<I", foot, 12)[0]
            flags = struct.unpack_from("<I", foot, 20)[0]
            need(size >= 32, "malformed")
            total = size + (32 if flags & 0x80000000 else 0)
            need(total <= avail, "malformed")
            end -= total
            continue
        if avail >= 15 and read_at(f, end - 9, 9) == b"LYRICS200":
            digits = read_at(f, end - 15, 6)
            need(all(0x30 <= c <= 0x39 for c in digits), "malformed")
            total = int(digits.decode("ascii")) + 15
            need(total <= avail, "malformed")
            need(read_at(f, end - total, 11) == b"LYRICSBEGIN", "malformed")
            end -= total
            continue
        if avail >= 10 and read_at(f, end - 10, 3) == b"3DI":
            foot = read_at(f, end - 10, 10)
            need(foot[3] != 0xFF and foot[4] != 0xFF, "malformed")
            need(not any(x & 0x80 for x in foot[6:10]), "malformed")
            size = (foot[6] << 21) | (foot[7] << 14) | (foot[8] << 7) | foot[9]
            total = size + 20
            need(total <= avail, "malformed")
            need(read_at(f, end - total, 3) == b"ID3", "malformed")
            end -= total
            continue
        return end


# ---------------------------------------------------------------- MP3

BR_V1_L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
BR_V2_L3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
SR = {3: [44100, 48000, 32000], 2: [22050, 24000, 16000], 0: [11025, 12000, 8000]}


def mpeg_l3_header(b):
    """Return (frame_len, side_info_len) or None."""
    if len(b) < 4 or b[0] != 0xFF or (b[1] & 0xE0) != 0xE0:
        return None
    ver = (b[1] >> 3) & 3
    layer = (b[1] >> 1) & 3
    bri = b[2] >> 4
    sri = (b[2] >> 2) & 3
    pad = (b[2] >> 1) & 1
    mono = (b[3] >> 6) == 3
    if ver == 1 or layer != 1 or bri in (0, 15) or sri == 3:
        return None
    sr = SR[ver][sri]
    if ver == 3:
        flen = 144000 * BR_V1_L3[bri] // sr + pad
        side = 17 if mono else 32
    else:
        flen = 72000 * BR_V2_L3[bri] // sr + pad
        side = 9 if mono else 17
    return flen, side


def hash_mp3(f, start, end):
    hdr = mpeg_l3_header(read_at(f, start, 4))
    need(hdr is not None, "unsupported")
    flen, side = hdr
    need(flen <= end - start, "malformed")
    frame = read_at(f, start, flen)
    xo = 4 + side
    if (xo + 4 <= flen and frame[xo:xo + 4] in (b"Xing", b"Info")) or \
            (40 <= flen and frame[36:40] == b"VBRI"):
        start += flen
    return hash_ranges(f, [(start, end)])


# ---------------------------------------------------------------- FLAC

def hash_flac(f, start, length):
    end = strip_trailers(f, start, length)
    pos = start + 4
    first = True
    while True:
        need(pos + 4 <= end, "malformed")
        h = read_at(f, pos, 4)
        last = h[0] & 0x80
        btype = h[0] & 0x7F
        blen = (h[1] << 16) | (h[2] << 8) | h[3]
        need(btype != 127, "malformed")
        if first:
            need(btype == 0, "malformed")
            first = False
        pos += 4 + blen
        need(pos <= end, "malformed")
        if last:
            break
    return hash_ranges(f, [(pos, end)])


# ---------------------------------------------------------------- MP4

def hash_mp4(f, length):
    pos = 0
    ranges = []
    while pos < length:
        need(pos + 8 <= length, "malformed")
        h = read_at(f, pos, 8)
        size = struct.unpack(">I", h[0:4])[0]
        btype = h[4:8]
        if size == 1:
            need(pos + 16 <= length, "malformed")
            size = struct.unpack(">Q", read_at(f, pos + 8, 8))[0]
            hlen = 16
            need(size >= 16, "malformed")
        elif size == 0:
            size = length - pos
            hlen = 8
        else:
            hlen = 8
            need(size >= 8, "malformed")
        need(pos + size <= length, "malformed")
        if btype == b"mdat":
            ranges.append((pos + hlen, pos + size))
        pos += size
    need(ranges, "malformed")
    return hash_ranges(f, ranges)


# ---------------------------------------------------------------- WAV

def hash_wav(f, length):
    riff_size = struct.unpack("<I", read_at(f, 4, 4))[0]
    extent = 8 + riff_size
    need(extent <= length, "malformed")
    pos = 12
    data = None
    while pos + 8 <= extent:
        h = read_at(f, pos, 8)
        cid = h[0:4]
        size = struct.unpack("<I", h[4:8])[0]
        pend = pos + 8 + size
        need(pend <= extent, "malformed")
        if cid == b"data":
            need(data is None, "malformed")
            data = (pos + 8, pend)
        pos = pend + (size & 1)
    need(data is not None, "malformed")
    return hash_ranges(f, [data])


# ---------------------------------------------------------------- Ogg

def ogg_packets(f, length):
    """Return a list of (index, data, length). For packet 1, data holds only
    its first 8 bytes (it is never hashed, so it is not buffered)."""
    pos = 0
    serial = None
    seq0 = None
    page_no = 0
    pending = None
    pending_len = 0
    eos_seen = False
    index = 0
    packets = []
    while pos < length:
        need(not eos_seen, "unsupported")
        need(pos + 27 <= length, "malformed")
        h = read_at(f, pos, 27)
        need(h[0:4] == b"OggS", "malformed")
        need(h[4] == 0, "malformed")
        flags = h[5]
        pserial, pseq = struct.unpack_from("<II", h, 14)
        nseg = h[26]
        need(pos + 27 + nseg <= length, "malformed")
        lacing = read_at(f, pos + 27, nseg)
        body_len = sum(lacing)
        need(pos + 27 + nseg + body_len <= length, "malformed")
        if page_no == 0:
            need(flags & 0x02, "malformed")
            serial, seq0 = pserial, pseq
        else:
            need(pserial == serial, "unsupported")
            need(not (flags & 0x02), "unsupported")
            need(pseq == (seq0 + page_no) & 0xFFFFFFFF, "malformed")
        cont = bool(flags & 0x01)
        need(cont == (pending is not None), "malformed")
        body = read_at(f, pos + 27 + nseg, body_len)
        bpos = 0
        for lv in lacing:
            seg = body[bpos:bpos + lv]
            bpos += lv
            if pending is None:
                pending = bytearray()
                pending_len = 0
            pending_len += lv
            if index == 1:
                pending += seg[:max(0, 8 - len(pending))]
            else:
                need(pending_len <= OGG_MAX_PACKET, "malformed")
                pending += seg
            if lv < 255:
                packets.append((index, bytes(pending), pending_len))
                index += 1
                pending = None
        if flags & 0x04:
            eos_seen = True
        pos += 27 + nseg + body_len
        page_no += 1
    need(page_no > 0, "malformed")
    need(eos_seen, "malformed")
    need(pending is None, "malformed")
    return packets


def hash_ogg(f, length):
    packets = ogg_packets(f, length)
    need(len(packets) >= 1, "malformed")
    p0 = packets[0][1]
    if p0[:7] == b"\x01vorbis":
        need(len(packets) >= 4, "malformed")
        need(packets[1][1][:7] == b"\x03vorbis", "malformed")
        need(packets[2][1][:7] == b"\x05vorbis", "malformed")
        opus = False
    elif p0[:8] == b"OpusHead":
        need(len(p0) >= 19, "malformed")
        need(len(packets) >= 3, "malformed")
        need(packets[1][1][:8] == b"OpusTags", "malformed")
        opus = True
    else:
        raise NoHash("unsupported")
    h = hashlib.sha256()
    for idx, data, plen in packets:
        if idx == 1:
            continue
        if opus and idx == 0:
            data = data[:16] + b"\x00\x00" + data[18:]
        h.update(struct.pack(">Q", plen))
        h.update(data)
    return h.hexdigest()


def audio_hash(path):
    """Return (hex_or_None, reason)."""
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            length = f.tell()
            f.seek(0)
            head = f.read(12)
            if len(head) >= 12 and head[0:4] == b"RIFF" and head[8:12] == b"WAVE":
                return hash_wav(f, length), "ok"
            if head[0:4] == b"OggS":
                return hash_ogg(f, length), "ok"
            if len(head) >= 8 and head[4:8] == b"ftyp":
                return hash_mp4(f, length), "ok"
            start = skip_leading_id3v2(f, length)
            if read_at(f, start, 4) == b"fLaC":
                return hash_flac(f, start, length), "ok"
            end = strip_trailers(f, start, length)
            return hash_mp3(f, start, end), "ok"
    except NoHash as e:
        return None, str(e)
    except OSError:
        return None, "unreadable"


if __name__ == "__main__":
    rc = 0
    for p in sys.argv[1:]:
        hx, why = audio_hash(p)
        print(f"{hx or 'NONE'}  {why}  {p}")
    sys.exit(rc)
