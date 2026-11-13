# Portable loudness export format (schemaVersion 1)

`audioHashAlgorithm`, `audioSha256`, `coverage.hashed` and
`coverage.hashUnavailable` are optional additions to schemaVersion 1. Readers
that do not know them ignore them; `relativePath` stays required.

Encoding: **UTF-8 without BOM**.

SYNTHETIC examples use fake IDs and paths. Readers must ignore **unknown fields on every object** (additive).

## Top-level object

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `format` | string | yes | Always `jellyfin-loudness-export` |
| `schemaVersion` | integer | yes | `1` |
| `exportedAt` | string | yes | ISO-8601 UTC ending in `Z` |
| `collisionKeyAlgorithm` | string | yes (this exporter) | `jf-ck-v1` |
| `collisionKey` | object | yes (this exporter) | Documents case-fold / NFC / rootId rules |
| `audioHashAlgorithm` | string | no | `jle-audio-v1`. Present exactly when the exporter ran with `--audio-hash`; absent otherwise. See [Audio hash jle-audio-v1](#audio-hash-jle-audio-v1) |
| `source` | object | yes | Provenance for gains |
| `roots` | array | yes | Root labels only (`id`) |
| `tracks` | array | yes | Portable entries |
| `coverage` | object | yes | Counts and safe error codes |

## Primary matching identity

Consumers match by **ordinal-exact `rootId` plus the exact original `relativePath`**,
including case and Unicode form after separator conversion and root stripping.
The collision key below is validation only; it is **not playback identity** and
must not replace exact-path matching. Never match on basename alone.

## Collision keys (`jf-ck-v1`)

Every producer claiming `collisionKeyAlgorithm="jf-ck-v1"` must apply this same
collision gate and omit all members of a colliding group (or abort). Consumers
retain exact-path primary matching regardless of this validation key.
For each successfully mapped track:

1. Store `relativePath` exactly (after `\`→`/` strip of the chosen root). **Do not** rewrite case or Unicode form of the stored path.
2. Build collision key: `rootId` (**ordinal exact**, never case-folded) + `U+001F` + `FullCaseFold(NFC(relativePath))`.
3. **Case fold:** Unicode 15.1.0 full default mapping, status **C+F only** (non-Turkic; status **T** excluded). Table vendored from https://www.unicode.org/Public/15.1.0/ucd/CaseFolding.txt
4. **NFC:** performed by the **.NET runtime** `string.Normalize(FormC)`. This is **not** a pinned cross-platform normalization table. Different Unicode versions / platforms may NFC rare sequences differently. Do **not** claim bit-identical NFC across all platforms. Android may later pin its own NFC data; until then treat NFC as “runtime NFC” under `collisionKey.nfc`.
5. There is **no NFC normalization after folding**. Malformed UTF-16 (lone surrogates) fails closed.
6. Shared vectors: `tests/fixtures/collision-key-vectors.json` (SYNTHETIC).

`collisionKey` object fields:

| Field | Example value |
| --- | --- |
| `caseFold` | `unicode-15.1-full-C+F-non-Turkic` |
| `nfc` | `runtime-dotnet-Normalize-FormC` |
| `rootId` | `ordinal-exact` |
| `relativePath` | human-readable rule summary |

## `source` (Jellyfin exporter)

| Field | Required | Notes |
| --- | --- | --- |
| `kind` | yes | `jellyfin-api` for the HTTP reader, or `jellyfin-database` for a read-only local `jellyfin.db`. Both use the same gain rule: LUFS when present (`-18` minus LUFS), otherwise the stored normalization gain. Album gain is the parent album's value. |
| `gainOrigin` | yes | `lufs-or-replaygain-unspecified` |
| `serverVersion` | no | Actual version when known; **omit** if unknown (never fabricate) |
| `gainUnits` | yes | `dB` |
| `lufsDerivedReferenceLufs` | no | Informational `-18` only; not a universal playback target |

### Android producer extension — RESERVED, not frozen

Exporter-only v1 does not implement Android importing or scanning. The proposed
`android-ebur128` / `measured-lufs` producer, its producer/version fields,
`referenceLufs`, and per-track provenance are **RESERVED and not a frozen Android
schema**. No scanner support is claimed. A future Android producer would use dB,
omit Jellyfin `serverVersion`, and specify its own measurement provenance before
this extension is finalized. Consumers ignore unknown fields at all levels.

`sourceItemId` is **required for Jellyfin** exports; optional for non-Jellyfin producers. It is **not** the match identity (match uses `rootId` + `relativePath`).

## `roots`

`{ "id": "music" }` only. Duplicate ids / unknown `rootId` on tracks / empty / absolute / traversal in relative paths are invalid. Absolute Windows roots stay local to the exporter machine.

## `tracks`

| Field | Required | Notes |
| --- | --- | --- |
| `rootId` | yes | |
| `relativePath` | yes | `/`-separated, no leading slash |
| `sourceItemId` | Jellyfin: yes | |
| `sourceAlbumId` | no | |
| `trackGainDb` / `albumGainDb` | no | Omit if missing; finite only; not clamped |
| `audioSha256` | no | 64 lowercase hex characters: the `jle-audio-v1` hash of this track's audio payload. Only present when top-level `audioHashAlgorithm` is present. Omitted (never `null`, never guessed) when the file is unsupported, malformed, or unreadable |
| `measuredLufs` | no; RESERVED | Not emitted or implemented by this exporter; Android extension not frozen |
| `truePeakDbtp` | no; RESERVED | Not emitted or implemented by this exporter; Android extension not frozen |
| `source` | no; RESERVED | Optional per-track provenance; not emitted; Android extension not frozen |

## `coverage`

| Field | Meaning |
| --- | --- |
| `partial` | `true` only when exporter ran with explicit `--partial` |
| `itemsSeen` | Source items considered |
| `entriesExported` | Length of `tracks` (denominator for “exported” stats) |
| `missingTrackGain` / `missingAlbumGain` / `missingBothGains` | Counted **only** among final exported tracks |
| `unmatched` | Paths matching no root |
| `collisions` | Total colliding **source items** (order-independent; 3 items sharing a key ⇒ 3). Colliding keys never appear in `tracks` |
| `outOfRangeGains` | Exported tracks with a finite gain outside `[-30,+30]` (value still exported) |
| `rejected` | Invalid path residual, nonfinite/invalid gain type, etc. |
| `hashed` | Present only with `audioHashAlgorithm`. Exported tracks that have `audioSha256` |
| `hashUnavailable` | Present only with `audioHashAlgorithm`. Exported tracks without `audioSha256`. `hashed + hashUnavailable = entriesExported` |
| `errors` | Up to 200 `{code, sourceItemId?}` — no absolute paths |

Missing-gains-only complete exports keep `partial=false`. A missing hash never
makes an export partial, never blocks a complete export, and adds no `errors` entry.

## Item-level vs document-level JSON errors

| Condition | Complete mode | Partial mode |
| --- | --- | --- |
| Malformed JSON / duplicate property names / logical alias dupes (`NormalizationGain`+`trackGainDb`) | Abort | Abort |
| Per-item invalid gain type / nonfinite (when represented) / bad residual | Abort | Skip item (`rejected`) |
| Unmatched / collisions | Abort | Continue; set counts |

Valid exports never contain JSON `NaN` / `Infinity` numbers.

API fetching fails closed if any requested `TotalRecordCount` is missing, invalid,
negative, changed across pages, or inconsistent with fetched items. An HTTP
request deadline covers both headers and the entire bounded response body.
Stable sorting and count checks do not provide a server-side snapshot: avoid
library changes while exporting. No live-server snapshot guarantee is claimed.

## Audio hash jle-audio-v1

Status: **frozen**. Any change to these rules needs a new algorithm id.

`audioSha256` identifies a track by its audio bytes so a consumer can match a
file after it was moved or renamed. Tags and cover art are excluded, so editing
them does not change the hash. It is a lowercase hex SHA-256 (FIPS 180-4) digest
of a byte sequence called the **hashed payload**, defined per container below.

Matching guidance for consumers: match by `audioSha256` first, then fall back to
`rootId` + `relativePath`. Two exported tracks may share a hash (byte-identical
duplicate rips); both are listed. Different rips or encodes of the same
recording do **not** share a hash.

### General rules

1. Input is the whole file as a byte string of length `L`, offsets `0 .. L-1`.
   The exporter opens it read-only and streams it; nothing is written.
2. All offsets and lengths are unsigned and must be computed without overflow
   (use 64-bit arithmetic; MP4 sizes need unsigned 64-bit). "Range `[a, b)`"
   means bytes `a .. b-1`.
3. `BE`/`LE` mean big-/little-endian unsigned integers of the stated width.
   `"XYZ"` means the ASCII bytes, compared exactly (case-sensitive).
4. A rule that says **no hash** ends processing: the track gets no
   `audioSha256` (it is counted in `coverage.hashUnavailable`). Never fall back
   to a whole-file hash or to another container's rules.
5. If the hashed payload is empty (0 bytes), **no hash**. (For Ogg the payload
   is never empty when the packet-count rule below holds.)
6. A file that cannot be opened or read completely gives **no hash**.
7. Container detection uses magic bytes only, never the file extension, and is
   evaluated in this order:
   1. `L >= 12`, bytes 0-3 `"RIFF"` and bytes 8-11 `"WAVE"`: [WAV](#wav-riffwave).
   2. `L >= 4`, bytes 0-3 `"OggS"`: [Ogg](#ogg-vorbis-and-opus).
   3. `L >= 8`, bytes 4-7 `"ftyp"`: [MP4](#mp4--m4a).
   4. Otherwise skip leading ID3v2 tags (below) to offset `P`. If bytes
      `P .. P+3` are `"fLaC"`: [FLAC](#flac).
   5. Otherwise if `P+4 <= L` and bytes `P .. P+3` are a valid MPEG audio
      Layer III frame header (below): [MP3](#mp3).
   6. Otherwise **no hash** (unsupported). This includes RF64, AIFF, WMA, APE,
      WavPack, raw AAC/ADTS, MPEG Layer I/II, FLAC-in-Ogg, Speex, and an MP3 whose
      first frame does not start right after its leading ID3v2 tags (for example
      junk or zero padding between the tag and the first frame).

### Leading ID3v2 skip (MP3 and FLAC)

Start with `P = 0`. Repeat:

- Read the 10 bytes at `P` (if fewer than 10 remain, stop). They form an ID3v2
  header when: bytes 0-2 are `"ID3"`, byte 3 != `0xFF`, byte 4 != `0xFF`, and
  each of bytes 6-9 is `< 0x80`. If not, stop.
- `size = b6<<21 | b7<<14 | b8<<7 | b9` (syncsafe). Tag length
  `T = 10 + size`, plus 10 more if flag bit `0x10` (footer present) is set in
  byte 5. This applies to any major version byte.
- If `P + T > L`: **no hash**. Otherwise `P = P + T` and repeat.

### Trailer loop (MP3 and FLAC)

Input range `[S, E)` with `S` = start of the audio container (`P` for MP3, the
offset of `"fLaC"` for FLAC) and `E = L`. Repeat until a full pass matches
nothing; within a pass, test in this order and restart the pass after any match.
`A = E - S` is the remaining length at the time of each test.

1. **ID3v1**: `A >= 128` and bytes `E-128 .. E-126` are `"TAG"`. Set
   `E = E - 128`. Then, if `E - S >= 227` and bytes `E-227 .. E-224` are
   `"TAG+"` (enhanced tag), set `E = E - 227`.
2. **APEv1/APEv2 footer**: `A >= 32` and bytes `E-32 .. E-25` are `"APETAGEX"`.
   `size` = LE32 at `E-20`, `flags` = LE32 at `E-12`. If `size < 32`: **no hash**.
   Tag length `T = size`, plus 32 if bit 31 of `flags` (header present) is set.
   If `T > A`: **no hash**. Set `E = E - T`. The version field is not checked.
3. **Lyrics3 v2**: `A >= 15` and bytes `E-9 .. E-1` are `"LYRICS200"`. The six
   bytes `E-15 .. E-10` must all be ASCII `'0'..'9'`, else **no hash**; they are
   the decimal `size`. `T = size + 15`. If `T > A`, or bytes `E-T .. E-T+10` are
   not `"LYRICSBEGIN"`: **no hash**. Set `E = E - T`. (Lyrics3 v1, ending in
   `"LYRICSEND"`, is not recognized and stays in the payload.)
4. **Appended ID3v2 with footer**: `A >= 10` and bytes `E-10 .. E-8` are `"3DI"`.
   Byte `E-7` and byte `E-6` must not be `0xFF`, and each of bytes `E-4 .. E-1`
   must be `< 0x80`, else **no hash**. `size` is the syncsafe value of bytes
   `E-4 .. E-1`; `T = size + 20`. If `T > A`, or bytes `E-T .. E-T+2` are not
   `"ID3"`: **no hash**. Set `E = E - T`.

An appended ID3v2 tag without a footer is not detectable from the end and stays
in the payload.

### MPEG audio Layer III frame header

Four bytes `b0 b1 b2 b3`. Valid when all hold:

- `b0 == 0xFF` and `(b1 & 0xE0) == 0xE0` (11-bit sync).
- Version `v = (b1 >> 3) & 3`: `3` = MPEG-1, `2` = MPEG-2, `0` = MPEG-2.5;
  `1` (reserved) is invalid.
- Layer `(b1 >> 1) & 3` must be `1` (Layer III). Layers I and II are invalid.
- Bitrate index `bi = b2 >> 4` must not be `0` (free format) or `15`.
- Sample-rate index `si = (b2 >> 2) & 3` must not be `3`.

Not checked: protection bit (`b1 & 1`), private bit, mode extension, copyright,
original, emphasis.

Derived values (`pad = (b2 >> 1) & 1`, mono when `(b3 >> 6) == 3`):

| | bitrate kbit/s for `bi` = 1..14 | sample rate Hz for `si` = 0, 1, 2 |
| --- | --- | --- |
| MPEG-1 | 32 40 48 56 64 80 96 112 128 160 192 224 256 320 | 44100 48000 32000 |
| MPEG-2 | 8 16 24 32 40 48 56 64 80 96 112 128 144 160 | 22050 24000 16000 |
| MPEG-2.5 | same as MPEG-2 | 11025 12000 8000 |

Frame length in bytes (integer division, includes the 4-byte header):

- MPEG-1: `FL = floor(144000 * kbps / rate) + pad`
- MPEG-2 and MPEG-2.5: `FL = floor(72000 * kbps / rate) + pad`

Side-information length `SI`:

| | stereo, joint stereo, dual channel | mono |
| --- | --- | --- |
| MPEG-1 | 32 | 17 |
| MPEG-2 / MPEG-2.5 | 17 | 9 |

### MP3

1. `S = P` (after the leading ID3v2 skip). The header at `S` is valid (detection).
2. Run the trailer loop on `[S, L)` to get `E`.
3. Compute `FL` and `SI` from the header at `S`. If `FL > E - S`: **no hash**
   (file ends inside the first frame).
4. The first frame `[S, S+FL)` is a **tag frame** when either holds:
   - **Xing/Info**: `X = 4 + SI`, `X + 4 <= FL`, and bytes `S+X .. S+X+3` are
     `"Xing"` or `"Info"`.
   - **VBRI**: `40 <= FL` and bytes `S+36 .. S+39` are `"VBRI"`.
5. If it is a tag frame, `S = S + FL`. Only the first frame is examined, once.
6. Hashed payload: `[S, E)`, raw bytes (frame headers, CRCs, side information
   and ancillary data included).

**CRC rule.** The Xing/Info offset is `4 + SI` regardless of the protection bit;
when the CRC is present (protection bit `0`) the offset is **not** shifted by 2,
and offset `4 + SI + 2` is **not** checked. This matches how the tag is actually
written and read: LAME's `lame_get_lametag_frame()` (`libmp3lame/VbrTag.c`)
subtracts the 2 CRC bytes so the tag "still starts at the same offset"; FFmpeg's
reader (`libavformat/mp3dec.c`, `xing_offtbl`) and writer (`mp3enc.c`, which
always clears the CRC) use `4 + SI`; mpg123 (`libmpg123/parse.c`,
`check_lame_tag`) notes "CRC or not, that does not matter here". The VBRI offset
is fixed at 36 from the frame start in all cases.

Limitations: MP3Gain/AACGain-style tools that rewrite `global_gain` inside the
audio frames change the hashed payload, so the hash changes. Undo information
those tools store in APEv2 is excluded like any other APE tag. Truncation inside
the audio frames after the first frame is not detected.

### FLAC

1. `S` = offset of `"fLaC"` (after the leading ID3v2 skip).
2. Run the trailer loop on `[S, L)` to get `E`.
3. Walk metadata blocks from `pos = S + 4`. For each block: if `pos + 4 > E`:
   **no hash**. Header byte `h0` = byte `pos`; last-block flag `h0 & 0x80`; type
   `h0 & 0x7F`; length = BE24 at `pos+1`. Type `127` gives **no hash**. The first
   block must be type `0` (STREAMINFO), else **no hash**. Set
   `pos = pos + 4 + length`; if `pos > E`: **no hash**. Stop after the block whose
   last-block flag is set.
4. Hashed payload: `[pos, E)` (all audio frames, raw). Frames are not parsed;
   truncation inside the audio frames is not detected.

All metadata blocks (STREAMINFO, PADDING, VORBIS_COMMENT, PICTURE, SEEKTABLE,
APPLICATION, CUESHEET) are excluded.

### MP4 / M4A

1. Walk top-level boxes from `pos = 0` while `pos < L`:
   - If `pos + 8 > L`: **no hash**. `size` = BE32 at `pos`; `type` = bytes
     `pos+4 .. pos+7`.
   - `size == 1`: if `pos + 16 > L`: **no hash**; `size` = BE64 at `pos+8`
     (unsigned); header length `H = 16`; if `size < 16`: **no hash**.
   - `size == 0`: the box runs to end of file: `size = L - pos`, `H = 8`.
   - otherwise `H = 8`; if `size < 8`: **no hash**.
   - If `size > L - pos`: **no hash**.
   - If `type` is `"mdat"`, append range `[pos + H, pos + size)`.
   - `pos = pos + size`.
2. If no `mdat` box was found: **no hash**.
3. Hashed payload: the concatenation of all `mdat` payload ranges in file order
   (box headers excluded; an empty `mdat` contributes nothing).

Nested boxes are not inspected (`moov`, `udta`, `meta`, `ilst`, `covr`, `free`,
`skip`, `uuid` and all others are excluded). Moving `moov` before or after `mdat`
does not change the hash.

### WAV (RIFF/WAVE)

1. `riffSize` = LE32 at 4; `extent = 8 + riffSize`. If `extent > L`: **no hash**.
   Bytes after `extent` are ignored.
2. Walk chunks from `pos = 12` while `pos + 8 <= extent`: `id` = bytes
   `pos .. pos+3`; `size` = LE32 at `pos+4`; `end = pos + 8 + size`. If
   `end > extent`: **no hash**. If `id` is `"data"`: if a data chunk was already
   seen, **no hash**; otherwise remember `[pos+8, end)`. Then
   `pos = end + (size & 1)` (odd-sized chunks are followed by one pad byte, which
   is never hashed). A final 1-7 bytes before `extent` that cannot hold a chunk
   header are ignored.
3. If no `data` chunk was found: **no hash**.
4. Hashed payload: the `data` chunk payload `[pos+8, end)`.

`fmt `, `LIST`/`INFO`, `id3 `/`ID3 `, `bext` and all other chunks are excluded.
The format chunk is not checked, so two files with identical sample bytes but
different `fmt ` parameters share a hash.

### Ogg (Vorbis and Opus)

**Pages.** Parse pages back to back from offset 0 to `L`. For page number `k`
(0-based) at `pos`:

- If `pos + 27 > L`, or bytes `pos .. pos+3` are not `"OggS"`, or the version
  byte at `pos+4` is not `0`: **no hash**.
- `flags` = byte `pos+5` (`0x01` continued, `0x02` beginning of stream (BOS),
  `0x04` end of stream (EOS)); `serial` = LE32 at `pos+14`; `seq` = LE32 at
  `pos+18`; `n` = byte `pos+26`; lacing values = bytes `pos+27 .. pos+26+n`;
  body length `B` = sum of the lacing values. If `pos + 27 + n + B > L`:
  **no hash**.
- The granule position and the CRC field are ignored (the CRC is not verified).
- Page 0 must have BOS set. Every later page must have BOS clear and the same
  serial as page 0 (otherwise **no hash**: chained or multiplexed streams are
  not supported), and `seq` must equal `(seq of page 0 + k) mod 2^32`
  (otherwise **no hash**).
- If a page follows a page with EOS set: **no hash** (chained).
- The last page in the file must have EOS set, else **no hash** (truncated).
- No trailing bytes after the last page are allowed (they would fail the
  `"OggS"` check).

**Packets.** Concatenate segments in order. Each lacing value contributes that
many body bytes to the current packet; a value `< 255` ends the packet (a lone
`0` ends a packet, possibly a zero-length one). A value of `255` as the last
lacing value of a page means the packet continues on the next page.

- A page's continued flag must be set exactly when a packet is still open from
  the previous page; otherwise **no hash** (this also rejects a continued flag
  on page 0).
- If a packet is still open after the last page: **no hash**.
- Packets are numbered from 0 in stream order.

**Codec.**

- **Vorbis**: packet 0 starts with `01 "vorbis"`. Packet 1 must start with
  `03 "vorbis"` and packet 2 with `05 "vorbis"`; at least 4 packets; else
  **no hash**.
- **Opus**: packet 0 starts with `"OpusHead"` and is at least 19 bytes long.
  Packet 1 must start with `"OpusTags"`; at least 3 packets; else **no hash**.
- Any other packet 0 (including one shorter than 8 bytes): **no hash**
  (unsupported).

**Hashed payload.** For every packet in order **except packet 1** (Vorbis
comment header / OpusTags, which holds tags and `METADATA_BLOCK_PICTURE`
cover art), append BE64 of the packet length followed by the packet bytes. For
Opus, packet 0 is hashed with its bytes 16 and 17 (the output gain, LE16) set
to `0x00`, so editing the output gain does not change the hash. The Vorbis
identification and setup headers (packets 0 and 2) are included unchanged.

**Packet size limit.** Any included packet longer than 16777216 bytes:
**no hash**. Packet 1 has no limit (it is skipped, not buffered).

Re-paging the same packets (different page boundaries, granule positions, CRCs,
or serial number) does not change the hash.

### Reference implementation and vectors

- `tests/tools/jle_audio_hash.py`: independent Python 3 reference (standard
  library only). `python3 tests/tools/jle_audio_hash.py FILE...`
- `jellyfin-loudness-exporter --audio-hash-file FILE...` prints the exporter's
  own result per file.
- `tests/fixtures/audio/vectors.json`: expected results for the SYNTHETIC
  fixture tones (`audioSha256: null` means no hash). Files in the same `group`
  must have the same hash.

## Example (SYNTHETIC)

See `tests/fixtures/shared-valid-export.json`.

## Jellyfin source pins

- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1295-L1325
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1827-L1836
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/ScheduledTasks/Tasks/AudioNormalizationTask.cs

## Privacy

Relative paths can reveal library structure. Absolute roots, server URLs, usernames, and tokens must never appear in the export.
