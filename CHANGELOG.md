# Changelog

## Unreleased

- MIT license (Copyright 2026 oiuy-retro).
- Audit fixes on `feature/initial-exporter`: atomic unique-temp writer; Unicode 15.1 full case-fold collision keys (`jf-ck-v1`) with runtime-NFC limitation documented; coverage semantics; path validation; JSON alias/dup handling; safe errors; fake HTTP transport tests; expanded fixtures/vectors.

## Local audit corrections

- Bound headers and body with one request deadline; reject absent/invalid/changed totals.
- Retain the exclusive temporary stream; fix supplementary root matching and reject drive-relative roots.
- Package full Unicode permission text and actual .NET runtime/host pack notices.
- Add portable full-output fixtures, strict shared-export validation, all-stable-field comparison, and adversarial in-memory HTTP tests.
- Distinguish exact playback identity from collision validation; mark Android extensions reserved and unimplemented.
