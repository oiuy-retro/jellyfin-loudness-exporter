# Third-party components

| Component | License | Notes |
| --- | --- | --- |
| .NET runtime / SDK libraries | Microsoft .NET Library License | Not redistributed in source. A self-contained publish may include runtime files produced by the SDK. |
| Unicode Character Database `CaseFolding.txt` 15.1.0 | Unicode Data Files and Software License | Vendored under `third_party/unicode-15.1.0/` and embedded for collision keys. See that directory's `NOTICE.md`. Source: https://www.unicode.org/Public/15.1.0/ucd/CaseFolding.txt (SHA-256 `4e55acfdc32825a22e87670e9056a3bf94ad7c5400065778e9e10f8314372bcf`). |

No other third-party NuGet packages are referenced.

Jellyfin is not redistributed. The exporter only documents a read-only HTTP client workflow against a Jellyfin server API.
