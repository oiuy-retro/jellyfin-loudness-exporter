# Portable loudness export format (schemaVersion 1)

SYNTHETIC examples below use fake IDs and paths. They are suitable for Android consumers that understand this format.

## Top-level object

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `format` | string | yes | Always `jellyfin-loudness-export` |
| `schemaVersion` | integer | yes | `1` |
| `exportedAt` | string | yes | ISO-8601 UTC ending in `Z` |
| `source` | object | yes | Provenance for gains |
| `roots` | array | yes | Root labels only (`id`) |
| `tracks` | array | yes | Portable entries |
| `coverage` | object | yes | Counts and safe error codes |

Unknown top-level fields must be ignored by readers (additive).

## `source`

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `kind` | string | yes | `jellyfin-api` for this exporter |
| `gainOrigin` | string | yes | `lufs-or-replaygain-unspecified` — Jellyfin's API does not say which |
| `serverVersion` | string | yes | Actual Jellyfin version string when known |
| `gainUnits` | string | yes | `dB` |
| `lufsDerivedReferenceLufs` | number | no | Informational only. If present, `-18`. **Not** a claim that every gain was LUFS-derived. Do not treat as a universal playback target. |

Reserved for future producers (omit in Jellyfin exporter): per-track `source.kind`, `referenceLufs`, `measuredLufs`, `truePeakDbtp`.

## `roots`

Each element is `{ "id": "music" }`. Absolute Windows roots stay on the exporter machine and are never written here.

## `tracks`

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `rootId` | string | yes | Matches a `roots[].id` |
| `relativePath` | string | yes | `/`-separated, no leading slash, exact case/Unicode as stored after stripping the root |
| `sourceItemId` | string | yes | Jellyfin item id |
| `sourceAlbumId` | string | no | Jellyfin album id when known |
| `trackGainDb` | number | no | Omitted when missing; finite only; not clamped |
| `albumGainDb` | number | no | Omitted when missing; finite only; not clamped |

Identity for matching: `(rootId, relativePath)` with full relative path (never basename-only). Collision detection uses a normalized key (Unicode NFC + case-fold) but the stored `relativePath` preserves the first-seen exact form until a collision blocks emission.

## `coverage`

| Field | Meaning |
| --- | --- |
| `partial` | `true` when the exporter ran with explicit partial mode |
| `itemsSeen` | Source audio items considered |
| `entriesExported` | Length of `tracks` |
| `missingTrackGain` | Exported (or would-be) entries lacking track gain |
| `missingAlbumGain` | Exported (or would-be) entries lacking album gain |
| `missingBothGains` | Entries lacking both gains (still exportable) |
| `unmatched` | Source paths that matched no configured root |
| `collisions` | Collision events (case/Unicode); colliding keys are never emitted |
| `outOfRangeGains` | Entries with a finite gain outside `[-30, +30]` dB (value still exported) |
| `rejected` | Items rejected for invalid path residual, nonfinite gain, etc. |
| `errors` | Up to 200 `{ "code", "sourceItemId"? }` — no absolute paths |

## Example (SYNTHETIC)

```json
{
  "format": "jellyfin-loudness-export",
  "schemaVersion": 1,
  "exportedAt": "2026-11-08T00:00:00Z",
  "source": {
    "kind": "jellyfin-api",
    "gainOrigin": "lufs-or-replaygain-unspecified",
    "serverVersion": "10.11.0-SYNTHETIC",
    "gainUnits": "dB",
    "lufsDerivedReferenceLufs": -18
  },
  "roots": [ { "id": "music" } ],
  "tracks": [
    {
      "rootId": "music",
      "relativePath": "Artist/Album/01 Track.flac",
      "sourceItemId": "11111111-1111-1111-1111-111111111111",
      "sourceAlbumId": "22222222-2222-2222-2222-222222222222",
      "trackGainDb": -1.5,
      "albumGainDb": -0.7
    }
  ],
  "coverage": {
    "partial": false,
    "itemsSeen": 1,
    "entriesExported": 1,
    "missingTrackGain": 0,
    "missingAlbumGain": 0,
    "missingBothGains": 0,
    "unmatched": 0,
    "collisions": 0,
    "outOfRangeGains": 0,
    "rejected": 0,
    "errors": []
  }
}
```

## Conditional provenance

Jellyfin `DtoService` may set `NormalizationGain` from LUFS as `-18 - LUFS`, or pass through ReplayGain tag dB. The public DTO does not label which. This format therefore uses `gainOrigin=lufs-or-replaygain-unspecified` and only optionally mentions `-18` as an informational Jellyfin LUFS reference hint.

Pinned server source inspected during design:

- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1295-L1325
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1827-L1836
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/ScheduledTasks/Tasks/AudioNormalizationTask.cs

## Privacy

Relative paths can still reveal folder/artist/album structure. Do not publish export files containing private library layout unless that is intended. Absolute roots, server URLs, usernames, and tokens must never appear in the export.
