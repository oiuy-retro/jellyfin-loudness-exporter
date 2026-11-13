# Changelog

## Unreleased

- Optional `--audio-hash`: adds a per-track `audioSha256` (algorithm `jle-audio-v1`, recorded in the new top-level `audioHashAlgorithm`) computed from the audio payload of FLAC, MP3, MP4/M4A, Ogg Vorbis, Ogg Opus and WAV files, excluding tags and cover art. Files are opened read-only and streamed. This is an optional addition to schemaVersion 1; `relativePath` stays required.
- `coverage.hashed` and `coverage.hashUnavailable` are written when hashing is on. Unsupported, malformed or unreadable files get no hash and do not block the export.
- `--audio-hash-file` prints the hash of individual files.
- Frozen hashing rules in docs/FORMAT.md, an independent Python reference in `tests/tools/jle_audio_hash.py`, and SYNTHETIC self-generated test tones with expected hashes in `tests/fixtures/audio/`.

## 0.1.0 — 2026-11-09

- Read loudness from a local Jellyfin database with `--database`. The file is opened read-only. Gain is `-18` minus `LUFS` when LUFS is set, otherwise the stored normalization gain. Album gain comes from the parent album.
- MIT license (Copyright 2026 oiuy-retro).
- Audit fixes on `feature/initial-exporter`: atomic unique-temp writer; Unicode 15.1 full case-fold collision keys (`jf-ck-v1`) with runtime-NFC limitation documented; coverage semantics; path validation; JSON alias/dup handling; safe errors; fake HTTP transport tests; expanded fixtures/vectors.

## Local audit corrections

- Bound headers and body with one request deadline; reject absent/invalid/changed totals.
- Retain the exclusive temporary stream; fix supplementary root matching and reject drive-relative roots.
- Package full Unicode permission text and actual .NET runtime/host pack notices.
- Add portable full-output fixtures, strict shared-export validation, all-stable-field comparison, and adversarial in-memory HTTP tests.
- Distinguish exact playback identity from collision validation; mark Android extensions reserved and unimplemented.
