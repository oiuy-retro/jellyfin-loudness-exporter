# SYNTHETIC audio hash fixtures (jle-audio-v1)

Every file here is a short sine tone (0.5 to 1 second) generated for this
project. No recorded music is included. Tag text is synthetic.

`vectors.json` lists each file, its expected `audioSha256` (`null` means the
file must get no hash), and a `group`. Files in the same group carry the same
audio with different tags, cover art, tag-frame or trailer layout, and must
have the same hash. The exporter self-test checks every entry.

Expected hashes were computed with the independent Python reference
`tests/tools/jle_audio_hash.py`, and the C# exporter matches them.

## How they were made

`tests/tools/make_audio_fixtures.py` (Python 3.13.5) runs FFmpeg's `lavfi`
`sine` source and stream copy, then edits bytes for cases FFmpeg cannot
produce (ID3v1, enhanced TAG+, APEv2, Lyrics3 v2, appended ID3v2 with footer,
VBRI and CRC-protected Xing frames, Opus output gain, 64-bit and size-0 `mdat`
headers, odd-sized RIFF chunks, chained and truncated files).

Tool versions:

| Tool | Version |
| --- | --- |
| FFmpeg | 7.1.5 (Debian `7:7.1.5-0+deb13u1`), with `-fflags +bitexact -flags:a +bitexact` |
| MP3 encoder | LAME 3.100 (`libmp3lame0 3.100-6+b3`) through FFmpeg |
| Opus encoder | libopus 1.5.2 through FFmpeg |
| Vorbis encoder | libvorbis 1.3.7 through FFmpeg |
| FLAC, AAC, PCM | FFmpeg native encoders |

Another FFmpeg or encoder version can produce different bytes. The committed
files and `vectors.json` are the reference.

## Coverage

- FLAC: Vorbis comment vs. different comment plus PICTURE block; leading ID3v2
  plus trailing ID3v1; cut inside metadata (no hash).
- MP3: MPEG-1 stereo (Info frame, offset 4+32), MPEG-1 mono VBR (Xing, 4+17),
  MPEG-2 mono (4+9), MPEG-2.5 stereo (4+17), each with and without the tag
  frame; ID3v2.3 with APIC and ID3v1; ID3v2 + Lyrics3 v2 + APEv2 with header +
  TAG+ + ID3v1; two leading ID3v2.4 tags, APEv2 without header and appended
  ID3v2.4 with footer; synthetic VBRI frames; a CRC-protected Xing frame at the
  LAME offset (excluded) and at offset +2 (not excluded, different hash);
  truncated first frame, ID3v2 size past end of file, APEv2 size too large
  (no hash).
- Opus: different OpusTags with METADATA_BLOCK_PICTURE and different paging;
  output gain edited to +3 dB; chained and truncated (no hash).
- Vorbis: different comment header with picture; truncated (no hash).
- MP4/M4A: different `ilst` with `covr` and `moov` moved before `mdat`;
  64-bit largesize `mdat`; size-0 `mdat`; truncated (no hash).
- WAV: odd-sized chunk with pad byte and an `id3 ` chunk with APIC; missing
  `data` chunk (no hash).
- A text file (unsupported, no hash).
