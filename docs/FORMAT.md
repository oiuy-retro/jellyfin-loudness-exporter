# Portable loudness export format (schemaVersion 1)

Encoding: **UTF-8 without BOM**.

SYNTHETIC examples use fake IDs and paths. Readers must ignore **unknown fields on every object** (additive).

## Top-level object

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `format` | string | yes | Always `jellyfin-loudness-export` |
| `schemaVersion` | integer | yes | `1` |
| `exportedAt` | string | yes | ISO-8601 UTC ending in `Z` |
| `collisionKeyAlgorithm` | string | yes (this exporter) | `jf-ck-v1` |
| `collisionKey` | object | yes (this exporter) | Documents case-fold / NFC / rootId rules |
| `source` | object | yes | Provenance for gains |
| `roots` | array | yes | Root labels only (`id`) |
| `tracks` | array | yes | Portable entries |
| `coverage` | object | yes | Counts and safe error codes |

## Primary matching identity

Consumers match by **ordinal-exact `rootId` plus the exact original `relativePath`**,
including case and Unicode form after separator conversion and root stripping.
The collision key below is validation only; it is **not playback identity** and
must not replace exact-path matching. Never match on basename alone.

## Collision keys (`jf-ck-v1`)

Every producer claiming `collisionKeyAlgorithm="jf-ck-v1"` must apply this same
collision gate and omit all members of a colliding group (or abort). Consumers
retain exact-path primary matching regardless of this validation key.
For each successfully mapped track:

1. Store `relativePath` exactly (after `\`→`/` strip of the chosen root). **Do not** rewrite case or Unicode form of the stored path.
2. Build collision key: `rootId` (**ordinal exact**, never case-folded) + `U+001F` + `FullCaseFold(NFC(relativePath))`.
3. **Case fold:** Unicode 15.1.0 full default mapping, status **C+F only** (non-Turkic; status **T** excluded). Table vendored from https://www.unicode.org/Public/15.1.0/ucd/CaseFolding.txt
4. **NFC:** performed by the **.NET runtime** `string.Normalize(FormC)`. This is **not** a pinned cross-platform normalization table. Different Unicode versions / platforms may NFC rare sequences differently. Do **not** claim bit-identical NFC across all platforms. Android may later pin its own NFC data; until then treat NFC as “runtime NFC” under `collisionKey.nfc`.
5. There is **no NFC normalization after folding**. Malformed UTF-16 (lone surrogates) fails closed.
6. Shared vectors: `tests/fixtures/collision-key-vectors.json` (SYNTHETIC).

`collisionKey` object fields:

| Field | Example value |
| --- | --- |
| `caseFold` | `unicode-15.1-full-C+F-non-Turkic` |
| `nfc` | `runtime-dotnet-Normalize-FormC` |
| `rootId` | `ordinal-exact` |
| `relativePath` | human-readable rule summary |

## `source` (Jellyfin exporter)

| Field | Required | Notes |
| --- | --- | --- |
| `kind` | yes | `jellyfin-api` |
| `gainOrigin` | yes | `lufs-or-replaygain-unspecified` |
| `serverVersion` | no | Actual version when known; **omit** if unknown (never fabricate) |
| `gainUnits` | yes | `dB` |
| `lufsDerivedReferenceLufs` | no | Informational `-18` only; not a universal playback target |

### Android producer extension — RESERVED, not frozen

Exporter-only v1 does not implement Android importing or scanning. The proposed
`android-ebur128` / `measured-lufs` producer, its producer/version fields,
`referenceLufs`, and per-track provenance are **RESERVED and not a frozen Android
schema**. No scanner support is claimed. A future Android producer would use dB,
omit Jellyfin `serverVersion`, and specify its own measurement provenance before
this extension is finalized. Consumers ignore unknown fields at all levels.

`sourceItemId` is **required for Jellyfin** exports; optional for non-Jellyfin producers. It is **not** the match identity (match uses `rootId` + `relativePath`).

## `roots`

`{ "id": "music" }` only. Duplicate ids / unknown `rootId` on tracks / empty / absolute / traversal in relative paths are invalid. Absolute Windows roots stay local to the exporter machine.

## `tracks`

| Field | Required | Notes |
| --- | --- | --- |
| `rootId` | yes | |
| `relativePath` | yes | `/`-separated, no leading slash |
| `sourceItemId` | Jellyfin: yes | |
| `sourceAlbumId` | no | |
| `trackGainDb` / `albumGainDb` | no | Omit if missing; finite only; not clamped |
| `measuredLufs` | no; RESERVED | Not emitted or implemented by this exporter; Android extension not frozen |
| `truePeakDbtp` | no; RESERVED | Not emitted or implemented by this exporter; Android extension not frozen |
| `source` | no; RESERVED | Optional per-track provenance; not emitted; Android extension not frozen |

## `coverage`

| Field | Meaning |
| --- | --- |
| `partial` | `true` only when exporter ran with explicit `--partial` |
| `itemsSeen` | Source items considered |
| `entriesExported` | Length of `tracks` (denominator for “exported” stats) |
| `missingTrackGain` / `missingAlbumGain` / `missingBothGains` | Counted **only** among final exported tracks |
| `unmatched` | Paths matching no root |
| `collisions` | Total colliding **source items** (order-independent; 3 items sharing a key ⇒ 3). Colliding keys never appear in `tracks` |
| `outOfRangeGains` | Exported tracks with a finite gain outside `[-30,+30]` (value still exported) |
| `rejected` | Invalid path residual, nonfinite/invalid gain type, etc. |
| `errors` | Up to 200 `{code, sourceItemId?}` — no absolute paths |

Missing-gains-only complete exports keep `partial=false`.

## Item-level vs document-level JSON errors

| Condition | Complete mode | Partial mode |
| --- | --- | --- |
| Malformed JSON / duplicate property names / logical alias dupes (`NormalizationGain`+`trackGainDb`) | Abort | Abort |
| Per-item invalid gain type / nonfinite (when represented) / bad residual | Abort | Skip item (`rejected`) |
| Unmatched / collisions | Abort | Continue; set counts |

Valid exports never contain JSON `NaN` / `Infinity` numbers.

API fetching fails closed if any requested `TotalRecordCount` is missing, invalid,
negative, changed across pages, or inconsistent with fetched items. An HTTP
request deadline covers both headers and the entire bounded response body.
Stable sorting and count checks do not provide a server-side snapshot: avoid
library changes while exporting. No live-server snapshot guarantee is claimed.

## Example (SYNTHETIC)

See `tests/fixtures/shared-valid-export.json`.

## Jellyfin source pins

- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1295-L1325
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/Dto/DtoService.cs#L1827-L1836
- https://github.com/jellyfin/jellyfin/blob/797999fc9bd49a9f0853916e2933e3ad06173503/Emby.Server.Implementations/ScheduledTasks/Tasks/AudioNormalizationTask.cs

## Privacy

Relative paths can reveal library structure. Absolute roots, server URLs, usernames, and tokens must never appear in the export.
