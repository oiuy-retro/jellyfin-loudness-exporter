# Known issues

## Audio hash limits (`--audio-hash`)

- The hash changes if the audio bytes change: re-encoding, MP3Gain/AACGain volume edits inside MP3 frames, or a different rip of the same CD.
- Not hashed: AIFF, WMA, APE, WavPack, RF64, raw AAC, FLAC inside Ogg, chained or multiplexed Ogg, and MP3 files with junk or padding between the ID3v2 tag and the first audio frame.
- Truncation inside FLAC or MP3 audio frames is not detected; such a file still gets a hash of the bytes that are present.
- Lyrics3 v1 and an appended ID3v2 tag without a footer are not recognised and stay inside the hashed MP3 bytes.
- In `--api` mode, paths reported by the server may not exist on the machine running the tool; those tracks get no hash.
- Not yet run against a real Windows library; tests use short synthetic tones only.

## The exporter could only reach loudness through the HTTP API

[Issue #3](https://github.com/oiuy-retro/jellyfin-loudness-exporter/issues/3). A local Jellyfin database already stores LUFS, and this tool did not read it. `--database` opens `jellyfin.db` read-only and does not ask for a token. When LUFS is set, gain is -18 minus that LUFS. Otherwise the stored normalization gain is used. Album gain comes from the parent album.

## Connection failures were reported as an internal error

[Issue #2](https://github.com/oiuy-retro/jellyfin-loudness-exporter/issues/2). A base URL that cannot be reached printed `internal_error:unexpected`. It now prints `http_error:unreachable` and does not include the host.
