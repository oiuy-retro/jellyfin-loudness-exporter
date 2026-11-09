# Jellyfin Loudness Exporter

Command-line tool that reads Jellyfin audio item loudness fields (or an offline fixture) and writes a portable UTF-8 JSON file for a music library that mirrors the same relative folder tree on another device.

This is not affiliated with the Jellyfin project. Stock Jellyfin has no native import for this file format; none is promised.

## What it does

- Queries Jellyfin `GET /Items` for `Audio` items with `fields=Path`, or reads a SYNTHETIC/offline items JSON file.
- Maps each item `Path` onto one or more named Windows roots you pass on the command line.
- Writes `format=jellyfin-loudness-export` / `schemaVersion=1` JSON (see [docs/FORMAT.md](docs/FORMAT.md)).
- Keeps API tokens in RAM from a masked prompt only (never CLI args, config files, logs, or disk).
- Refuses to silently overwrite an existing output file (use `--force`).

## Security and limitations

- Read-only against Jellyfin HTTP APIs. No library scans, tag writes, or database changes.
- Export files omit server URL, credentials, usernames, and absolute Windows roots. Relative paths remain and can reveal library structure.
- Jellyfin may return network-substituted paths; only explicitly configured roots are matched.
- Gains may come from LUFS analysis or ReplayGain tags; the API does not distinguish them. See FORMAT.md.
- Missing gains are omitted (not written as zero). Entries with both gains missing may still be exported for coverage.
- Windows execution and live Jellyfin servers are **not** verified in the automated tests here.

## Run (offline)

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- \
  --offline tests/fixtures/input-normal.json \
  --root music=C:\Music \
  --output /tmp/loudness.json \
  --lufs-reference-hint
```

## Run (API)

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- \
  --api --base-url https://jellyfin.example \
  --user-id 00000000-0000-0000-0000-000000000000 \
  --root music=C:\Music \
  --output loudness.json \
  --lufs-reference-hint
```

You will be prompted for the API token (hidden input).

Add `--partial` to allow unmatched paths / collisions to produce a file with `coverage.partial=true`. Without it, those conditions block the export.

## Self-test

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- --selftest
```

Tested with .NET SDK `10.0.402` offline self-tests only. Windows hosts and real Jellyfin servers are not verified.

## Build

See [docs/BUILDING.md](docs/BUILDING.md).

## Jellyfin source pins

Design checked against jellyfin/jellyfin commit `797999fc9bd49a9f0853916e2933e3ad06173503`:

- DtoService gain and path mapping
- AudioNormalizationTask LUFS persistence
- Public `BaseItemDto` fields `NormalizationGain`, `AlbumNormalizationGain`, `Path`

## More documentation

- [docs/FORMAT.md](docs/FORMAT.md) — schema contract
- [docs/BUILDING.md](docs/BUILDING.md) — build and publish
- [CHANGELOG.md](CHANGELOG.md)
- [THIRD_PARTY.md](THIRD_PARTY.md)

## License

License is pending an owner decision. See [LICENSE](LICENSE). Until a license is chosen, this project is not licensed for use, modification, or redistribution, and release binaries should not be published.
