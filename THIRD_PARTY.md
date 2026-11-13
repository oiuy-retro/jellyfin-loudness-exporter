# Third-party components

| Component | License | Notes |
| --- | --- | --- |
| .NET runtime | MIT and component-specific third-party terms | Not vendored in source. Self-contained publish copies the resolved runtime pack’s own `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` to `notices/dotnet-runtime/`, and host pack notices to `notices/dotnet-host/`. These notices, not a blanket library license, describe the redistributed components. |
| Unicode Character Database `CaseFolding.txt` 15.1.0 | Unicode License V3 | Vendored under `third_party/unicode-15.1.0/` and embedded for collision keys. See that directory's `NOTICE.md` and complete `LICENSE.txt`. The full license is copied to `notices/unicode/LICENSE.txt` for both build and publish output. Source: https://www.unicode.org/Public/15.1.0/ucd/CaseFolding.txt (SHA-256 `4e55acfdc32825a22e87670e9056a3bf94ad7c5400065778e9e10f8314372bcf`). |

No other third-party NuGet packages are referenced.

The audio test tones in `tests/fixtures/audio/` were generated for this project
with FFmpeg 7.1.5 (Debian package `7:7.1.5-0+deb13u1`) and the encoders it
links (LAME 3.100, libopus 1.5.2, libvorbis 1.3.7, FFmpeg's native FLAC and AAC
encoders). They contain only sine tones and synthetic tag text produced by
`tests/tools/make_audio_fixtures.py`. No FFmpeg, LAME, Opus or Vorbis code or
binaries are included in this repository or in the build output.

Jellyfin is not redistributed. The exporter only documents a read-only HTTP client workflow against a Jellyfin server API.
