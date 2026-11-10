# Known issues

## Connection failures were reported as an internal error

[Issue #2](https://github.com/oiuy-retro/jellyfin-loudness-exporter/issues/2). A base URL that cannot be reached printed `internal_error:unexpected`. It now prints `http_error:unreachable` and does not include the host.
