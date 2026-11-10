# SYNTHETIC portable fixtures

All paths, item IDs, and versions are fake. `manifest.json` identifies exporter
inputs, explicit Windows root mappings, partial mode, expected complete exports,
and expected safe error codes. Cases cover normal/missing gains, exact Unicode,
NFC/NFD and case collisions, three colliders, backslashes, invalid roots and
residuals, partial coverage, finite unusual gains, represented nonfinite values,
invalid JSON, overflow, and unknown input fields.

Every `expected-*.json` is a complete portable export, not a track/coverage
fragment. Compare every stable field and ignore **only** `exportedAt`.
`shared-valid-export.json` is a complete Jellyfin v1 example;
`shared-future-fields-export.json` adds unknown fields at every object level to
exercise forward compatibility. Reserved Android producer fields do not claim
scanner support and are not a frozen producer schema.

`collision-key-vectors.json` specifies exact `jf-ck-v1` validation keys, including
supplementary characters, full case fold, ordinal root IDs, non-Turkic dotted I,
Cherokee, and absence of post-fold NFC. Collision keys are not playback identity.
The malformed-NaN `.json` and `.txt` files intentionally contain invalid JSON.
