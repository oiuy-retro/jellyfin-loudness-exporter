# Jellyfin Loudness Exporter

Command-line tool that reads Jellyfin audio loudness fields (or an offline fixture) and writes a portable UTF-8 (no BOM) JSON file for a library that mirrors the same relative folder tree elsewhere.

This is not affiliated with the Jellyfin project. Stock Jellyfin has no native import for this file format; none is promised.

A built copy is [`jellyfin-loudness-exporter.exe`](https://github.com/oiuy-retro/jellyfin-loudness-exporter/releases/latest/download/jellyfin-loudness-exporter.exe) on the [latest release](https://github.com/oiuy-retro/jellyfin-loudness-exporter/releases/latest).

## What it does

- Queries Jellyfin `GET /Items` for `Audio` with `fields=Path`, reads a local `jellyfin.db` read-only, or reads offline items JSON under `tests/fixtures/`.
- Maps each item `Path` onto named Windows roots passed as `--root id=WindowsPath`.
- Writes `format=jellyfin-loudness-export` / `schemaVersion=1` (see [docs/FORMAT.md](docs/FORMAT.md)).
- Collision keys use algorithm `jf-ck-v1`: ordinal `rootId` + Unicode 15.1 full case fold (C+F) after **runtime** NFC of the relative path. NFC is not a pinned cross-platform table; see FORMAT.md.
- API token from a masked prompt into RAM only (never CLI args, config, log, or disk).
- Atomic output via an exclusively created unique same-directory temp file; refuses silent overwrite (`--force` replaces without delete-then-move).
- Optional `--audio-hash`: reads each exported track's music file and adds `audioSha256`, a hash of the audio only (tags and cover art excluded), so a player can find a track after it was moved or renamed.

## Security and limitations

- Read-only HTTP against Jellyfin. No scans, tag writes, or DB changes.
- `--audio-hash` opens music files read-only. It never writes tags, files, or the database.
- Export omits server URL, credentials, usernames, and absolute roots. Relative paths can still reveal structure.
- Prefer `https`. `--allow-http` prints an explicit warning. Redirects are refused. Base URLs must not contain userinfo, query, or fragment; a missing trailing slash on an app prefix such as `/jellyfin` is normalized.
- Errors print safe category codes only (no roots/URLs/tokens).
- Automated tests do not open a live Jellyfin server. The published Windows executable's self-test is run before a release.
- Each HTTP request has a two-minute header-and-body deadline and a 64 MiB response cap. Missing/invalid/changed totals abort instead of claiming a complete export. Avoid library changes while exporting; count checks are not a server-side snapshot.
- Choose an output directory you control; do not use a directory writable by untrusted users. Keep the adjacent license notices when distributing builds.
- Exporter-only v1 does not implement Android importing or scanning. Reserved Android fields are not a frozen scanner contract.

## Run (offline)

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- \
  --offline tests/fixtures/input-normal.json \
  --root 'music=C:\Music' \
  --output /tmp/loudness.json \
  --lufs-reference-hint
```

### Windows PowerShell

```powershell
.\jellyfin-loudness-exporter.exe --offline .\tests\fixtures\input-normal.json --root "music=C:\Music" --output .\loudness.json --lufs-reference-hint
```

## Run (API)

```powershell
.\jellyfin-loudness-exporter.exe --api --base-url "https://jellyfin.example/jellyfin" --user-id "00000000-0000-0000-0000-000000000000" --root "music=C:\Music" --output .\loudness.json --lufs-reference-hint
```

`--user-id` is optional when the token can resolve `/Users/Me` (user session). API-key mode may omit it if the server accepts `/Items` without `userId`.

## Run (local database)

No server and no token. The database is opened read-only. `--root` must be the Windows path prefix stored in that database, not a different folder that happens to hold the same files.

```powershell
.\jellyfin-loudness-exporter.exe --database .\jellyfin.db --root "music=C:\Music" --output .\loudness.json --lufs-reference-hint
```

## Audio hashes (optional)

Add `--audio-hash` to any mode. For every exported track the tool opens the file at the path Jellyfin recorded for it, reads it from start to end, and writes `audioSha256` next to `relativePath`. The hash covers only the audio data, so changing tags or cover art does not change it. `relativePath` is still written; a player can match by hash first and fall back to the path.

```powershell
.\jellyfin-loudness-exporter.exe --database .\jellyfin.db --root "music=C:\Music" --output .\loudness.json --audio-hash
```

- Supported: FLAC, MP3, MP4/M4A, Ogg Vorbis, Ogg Opus, WAV. The container is recognised from the file's first bytes, not its extension. The exact rules (`jle-audio-v1`) are in [docs/FORMAT.md](docs/FORMAT.md#audio-hash-jle-audio-v1).
- A file that is missing, cannot be read, is another format, or does not parse gets no `audioSha256`. It is still exported and counted in `coverage.hashUnavailable`; the export does not fail. A summary line such as `audio_hash_summary:hashed=950;unavailable=50;unsupported=40;malformed=2;unreadable=8` is printed to the console, and progress every 500 files.
- Every file is read in full, once per run, so a large library takes a while (roughly the time to copy it from the disk).
- In `--database` mode the paths are the local paths on the Jellyfin machine, so run the tool on that machine. In `--api` mode the server may report paths that are different on the machine running the tool (for example network or container paths); those files count as unavailable.
- Byte-identical duplicate files share a hash and are both listed. Different rips of the same album do not share a hash.
- Tools that change volume inside MP3 audio frames (MP3Gain, AACGain) change the hash.

To print the hash of individual files:

```powershell
.\jellyfin-loudness-exporter.exe --audio-hash-file "C:\Music\Album\01.flac"
```

## Self-test

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- --selftest
```

Fixtures live in `tests/fixtures/` (all marked SYNTHETIC). Short self-generated test tones for the audio hash are in `tests/fixtures/audio/`.

Tested with .NET SDK 10. The release executable is a Windows build and its self-test passes there. Automated tests do not open a live Jellyfin server.

## Build

See [docs/BUILDING.md](docs/BUILDING.md).

## More documentation

- [docs/FORMAT.md](docs/FORMAT.md)
- [docs/BUILDING.md](docs/BUILDING.md)
- [CHANGELOG.md](CHANGELOG.md)
- [THIRD_PARTY.md](THIRD_PARTY.md)

## License

[MIT](LICENSE). Copyright (c) 2026 oiuy-retro.

Unicode Data Files remain under Unicode License V3; see [THIRD_PARTY.md](THIRD_PARTY.md) and `third_party/unicode-15.1.0/NOTICE.md`.
