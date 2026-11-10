# Jellyfin Loudness Exporter

Command-line tool that reads Jellyfin audio loudness fields (or an offline fixture) and writes a portable UTF-8 (no BOM) JSON file for a library that mirrors the same relative folder tree elsewhere.

This is not affiliated with the Jellyfin project. Stock Jellyfin has no native import for this file format; none is promised.

## What it does

- Queries Jellyfin `GET /Items` for `Audio` with `fields=Path`, reads a local `jellyfin.db` read-only, or reads offline items JSON under `tests/fixtures/`.
- Maps each item `Path` onto named Windows roots passed as `--root id=WindowsPath`.
- Writes `format=jellyfin-loudness-export` / `schemaVersion=1` (see [docs/FORMAT.md](docs/FORMAT.md)).
- Collision keys use algorithm `jf-ck-v1`: ordinal `rootId` + Unicode 15.1 full case fold (C+F) after **runtime** NFC of the relative path. NFC is not a pinned cross-platform table; see FORMAT.md.
- API token from a masked prompt into RAM only (never CLI args, config, log, or disk).
- Atomic output via an exclusively created unique same-directory temp file; refuses silent overwrite (`--force` replaces without delete-then-move).

## Security and limitations

- Read-only HTTP against Jellyfin. No scans, tag writes, or DB changes.
- Export omits server URL, credentials, usernames, and absolute roots. Relative paths can still reveal structure.
- Prefer `https`. `--allow-http` prints an explicit warning. Redirects are refused. Base URLs must not contain userinfo, query, or fragment; a missing trailing slash on an app prefix such as `/jellyfin` is normalized.
- Errors print safe category codes only (no roots/URLs/tokens).
- Windows execution and live Jellyfin servers are **not** verified by automated tests here.
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

## Self-test

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- --selftest
```

Fixtures live in `tests/fixtures/` (all marked SYNTHETIC).

Tested with .NET SDK `10.0.402` offline self-tests only. Windows hosts and real Jellyfin servers are not verified.

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
