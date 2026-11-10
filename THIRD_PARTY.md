# Third-party components

| Component | License | Notes |
| --- | --- | --- |
| .NET runtime | MIT and component-specific third-party terms | Not vendored in source. Self-contained publish copies the resolved runtime pack’s own `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` to `notices/dotnet-runtime/`, and host pack notices to `notices/dotnet-host/`. These notices, not a blanket library license, describe the redistributed components. |
| Unicode Character Database `CaseFolding.txt` 15.1.0 | Unicode License V3 | Vendored under `third_party/unicode-15.1.0/` and embedded for collision keys. See that directory's `NOTICE.md` and complete `LICENSE.txt`. The full license is copied to `notices/unicode/LICENSE.txt` for both build and publish output. Source: https://www.unicode.org/Public/15.1.0/ucd/CaseFolding.txt (SHA-256 `4e55acfdc32825a22e87670e9056a3bf94ad7c5400065778e9e10f8314372bcf`). |

No other third-party NuGet packages are referenced.

Jellyfin is not redistributed. The exporter only documents a read-only HTTP client workflow against a Jellyfin server API.
