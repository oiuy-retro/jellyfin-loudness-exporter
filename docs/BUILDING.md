# Building

## Requirements

- .NET SDK 10.x (`dotnet --version` reports 10.x)
- Optional: ability to `dotnet publish -r win-x64` for a Windows self-contained binary (runtime pack downloaded by the SDK on demand)

## Restore and build

```bash
dotnet restore JellyfinLoudnessExporter.sln
dotnet build JellyfinLoudnessExporter.sln -c Release
```

## Self-test (offline only)

```bash
dotnet run --project src/JellyfinLoudnessExporter -c Release -- --selftest
```

Or after build:

```bash
dotnet src/JellyfinLoudnessExporter/bin/Release/net10.0/jellyfin-loudness-exporter.dll --selftest
```

## Audio hash cross-check (optional)

The self-test compares the exporter's `jle-audio-v1` results with
`tests/fixtures/audio/vectors.json`. To compare against the independent Python
reference (Python 3.10 or later, standard library only), including a few
thousand mutated copies of the fixtures, after a build:

```bash
python3 tests/tools/cross_check.py src/JellyfinLoudnessExporter/bin/Release/net10.0/jellyfin-loudness-exporter.dll
```

`tests/tools/make_audio_fixtures.py` documents how the fixture tones were made.
It needs `ffmpeg` and rewrites `tests/fixtures/audio/`; another FFmpeg build may
produce different bytes, so do not run it unless you mean to replace the
fixtures and their expected hashes.

## Optional Windows self-contained publish

```bash
dotnet publish src/JellyfinLoudnessExporter/JellyfinLoudnessExporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
```

`dist/` is gitignored. Windows execution of the published binary is not verified in this Linux build environment.

## Notes

- Do not commit `bin/`, `obj/`, `dist/`, or any `.exe` / `.dll` artifacts.
- Do not put API tokens or real library roots into tracked config files.

## License

MIT. See LICENSE. Unicode CaseFolding data: see THIRD_PARTY.md.

## Packaged licenses

Build and publish output include the project MIT license, `THIRD_PARTY.md`, and
`notices/unicode/LICENSE.txt` (the full Unicode copyright and permission notice).
Self-contained publish additionally copies `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` from the actual resolved .NET runtime/host packs into
`notices/dotnet-runtime/` and `notices/dotnet-host/`. Keep these sidecar notices
with the executable, even for a single-file build. A missing runtime notice
fails publishing. No binaries or runtime packs belong in git.
