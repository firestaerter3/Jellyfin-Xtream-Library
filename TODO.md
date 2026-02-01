# TODO

Planned features and known limitations for Jellyfin Xtream Library.

## Planned Features

### High Priority

- [x] **Bulk retry for failed items** - Retry mechanism for items that failed during sync

### Medium Priority

- [ ] **NFO file generation** - Create NFO files alongside STRM for better metadata in other media players
- [ ] **Custom folder structure** - Configurable output folder templates (e.g., genre-based organization)
- [ ] **Scheduled sync improvements** - More granular scheduling (specific times, days of week)

### Low Priority

- [ ] **Artwork download** - Cache poster/fanart images locally instead of using provider URLs
- [ ] **Provider health check** - Dashboard widget showing provider connection status
- [ ] **Sync history** - Log of past sync operations with statistics

## Known Limitations

### Provider Compatibility

- Some Xtream providers return malformed JSON for certain endpoints
- Episode numbering from providers may not match standard databases
- Provider API rate limits may cause failures at high parallelism settings

### Name Sanitization

Current sanitization handles:
- Year tags: `(2024)`
- Language tags: `| NL |`, `┃NL┃`, `[EN]`, `(NL GESPROKEN)`, `[NL Gepsroken]`
- Codec tags: `HEVC`, `x264`, `x265`, `H.264`
- Quality tags: `4K`, `1080p`, `720p`, `HDR`, `UHD`
- Source tags: `BluRay`, `WEBRip`, `HDTV`, `REMUX`
- Asian bracketed text: `[本好きの下剋上]`, `[日本語タイトル]`
- Malformed quotes: `'\'` → `'`

### Jellyfin Integration

- Library scan timing: Jellyfin may not immediately recognize new STRM files
- Credential exposure: Remote paths in Jellyfin's API may include Xtream credentials

### Performance

- Initial sync of large libraries (10,000+ items) may take significant time
- Subsequent incremental syncs are much faster (skips unchanged content)
- Smart skip requires series folder to exist with at least one STRM file

## Recently Completed

### v1.6.0

- [x] **Incremental sync** - Skip unchanged series/movies based on `LastModified` and `Added` timestamps
  - Series: Uses `LastModified` timestamp from API
  - Movies: Parses `Added` Unix timestamp from API
  - Deletion tracking: Detects series/movies removed from provider and cleans up sync state
  - Configurable via `IncrementalSyncEnabled` (default: true)
  - Periodic full sync via `FullSyncIntervalHours` (default: 24h)
  - Force full sync via API: `POST /Xtream/SeriesCacheRefresh?fullSync=true`
  - Sync state persisted in `{PluginConfig}/Jellyfin.Xtream/sync-state.json`

### v1.5.1

- [x] Tabbed configuration UI (General, Movies, Series tabs)
- [x] Detailed sync statistics (series, seasons, episodes with added/deleted counts)
- [x] Prefix language tag stripping (`┃NL┃` at start of names)
- [x] Asian bracketed text removal for better metadata matching
- [x] Auto-load categories when credentials are configured

### v1.5.0

- [x] Parallel sync with configurable parallelism (default: 3 concurrent operations)
- [x] Real-time sync progress API endpoint
- [x] Smart skip for existing series (avoids unnecessary API calls)
- [x] Language tag stripping from titles
- [x] Category filtering for VOD and Series

### v1.4.0

- [x] Orphan cleanup - removes STRM files for content no longer on provider
- [x] Automatic Jellyfin library scan trigger after sync
- [x] Connection test endpoint

## Contributing

See the project README for contribution guidelines.
