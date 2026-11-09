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

## Optional Windows self-contained publish

```bash
dotnet publish src/JellyfinLoudnessExporter/JellyfinLoudnessExporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/win-x64
```

`dist/` is gitignored. Windows execution of the published binary is not verified in this Linux build environment.

## Notes

- Do not commit `bin/`, `obj/`, `dist/`, or any `.exe` / `.dll` artifacts.
- Do not put API tokens or real library roots into tracked config files.
