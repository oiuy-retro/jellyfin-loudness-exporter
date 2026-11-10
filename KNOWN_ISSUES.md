# Known issues

## The exporter could only reach loudness through the HTTP API

[Issue #3](https://github.com/oiuy-retro/jellyfin-loudness-exporter/issues/3). A local Jellyfin database already stores LUFS, and this tool did not read it. `--database` opens `jellyfin.db` read-only and does not ask for a token. When LUFS is set, gain is -18 minus that LUFS. Otherwise the stored normalization gain is used. Album gain comes from the parent album.

## Connection failures were reported as an internal error

[Issue #2](https://github.com/oiuy-retro/jellyfin-loudness-exporter/issues/2). A base URL that cannot be reached printed `internal_error:unexpected`. It now prints `http_error:unreachable` and does not include the host.
