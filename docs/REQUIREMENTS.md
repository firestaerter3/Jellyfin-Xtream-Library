# Requirements

This document defines the functional and non-functional requirements for the Jellyfin Xtream Library plugin.

## Problem Statement

Jellyfin's channel-based plugins (like the standard Xtream plugin) present IPTV content as "Channels" which have limited client support. Specifically:
- Swiftfin (iOS client) has broken channel support
- Channel items don't integrate with Jellyfin's metadata providers
- No support for collections, playlists, or watch history
- Limited UI compared to native library items

## Solution

Create STRM files that point to Xtream streaming URLs, allowing content to appear in native Jellyfin Movie/TV Show libraries with full metadata and client support.

---

## Functional Requirements

### FR-1: Provider Configuration

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1.1 | User can configure Xtream provider base URL | Must |
| FR-1.2 | User can configure username and password | Must |
| FR-1.3 | User can configure target library path for STRM files | Must |
| FR-1.4 | User can optionally configure custom User-Agent | Should |
| FR-1.5 | Configuration persists across Jellyfin restarts | Must |

### FR-2: Content Synchronization

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-2.1 | Sync VOD/Movie content from provider | Must |
| FR-2.2 | Sync Series/TV Show content from provider | Must |
| FR-2.3 | User can enable/disable movie sync independently | Should |
| FR-2.4 | User can enable/disable series sync independently | Should |
| FR-2.5 | Skip content that already exists (by stream ID) | Must |
| FR-2.6 | Handle duplicate content across categories | Must |
| FR-2.7 | Extract year from content titles when present | Should |
| FR-2.8 | User can select specific VOD categories to sync | Should |
| FR-2.9 | User can select specific Series categories to sync | Should |
| FR-2.10 | Empty category selection syncs all (backward compatible) | Must |

### FR-3: File Structure

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-3.1 | Create Movies folder structure: `Movies/{Name} ({Year})/` | Must |
| FR-3.2 | Create Series folder structure: `Series/{Name} ({Year})/Season N/` | Must |
| FR-3.3 | STRM filename includes series name, season, episode number | Must |
| FR-3.4 | STRM filename includes episode title when available | Should |
| FR-3.5 | Sanitize filenames to remove invalid characters | Must |
| FR-3.6 | Handle missing file extensions (default to mp4/mkv) | Must |

### FR-4: Orphan Management

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-4.1 | Optionally delete STRM files for removed provider content | Should |
| FR-4.2 | Clean up empty directories after orphan deletion | Should |
| FR-4.3 | User can enable/disable orphan cleanup | Should |

### FR-5: Scheduling

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-5.1 | Sync runs automatically on configurable interval | Must |
| FR-5.2 | Sync appears in Jellyfin Scheduled Tasks dashboard | Must |
| FR-5.3 | User can manually trigger sync from Scheduled Tasks | Must |
| FR-5.4 | User can configure sync interval in minutes | Should |

### FR-6: API Endpoints

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-6.1 | POST endpoint to trigger manual sync | Must |
| FR-6.2 | GET endpoint to retrieve last sync status/result | Must |
| FR-6.3 | GET endpoint to test provider connection | Must |
| FR-6.4 | All endpoints require admin authorization | Must |

### FR-7: Library Integration

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-7.1 | Optionally trigger Jellyfin library scan after sync | Should |
| FR-7.2 | Only trigger scan if content was added or removed | Should |

### FR-8: Connection Testing

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-8.1 | Verify provider credentials before sync | Should |
| FR-8.2 | Display user info on successful connection test | Should |
| FR-8.3 | Return clear error message on connection failure | Must |

---

## Non-Functional Requirements

### NFR-1: Performance

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-1.1 | Skip existing files without re-writing | - |
| NFR-1.2 | Process categories in sequence to avoid rate limiting | - |
| NFR-1.3 | Sync should not block Jellyfin operations | - |

### NFR-2: Reliability

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-2.1 | Individual item failures don't abort entire sync | - |
| NFR-2.2 | Log errors with sufficient context for debugging | - |
| NFR-2.3 | Handle API response inconsistencies (arrays vs objects) | - |
| NFR-2.4 | Support cancellation of running sync | - |

### NFR-3: Compatibility

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-3.1 | Support Jellyfin 10.11.0+ | Must |
| NFR-3.2 | Support .NET 9.0 runtime | Must |
| NFR-3.3 | Work with any Xtream-compatible provider | Should |
| NFR-3.4 | STRM files compatible with all Jellyfin clients | Must |

### NFR-4: Security

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-4.1 | Store credentials in Jellyfin's configuration system | - |
| NFR-4.2 | Require admin authentication for API endpoints | - |
| NFR-4.3 | Don't log passwords in plain text | - |

### NFR-5: Maintainability

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-5.1 | Separate API client from sync logic | - |
| NFR-5.2 | Use dependency injection for testability | - |
| NFR-5.3 | Document public APIs with XML comments | - |

---

## STRM File Specification

### Format
Single-line text file containing the streaming URL.

### Movie URL Pattern
```
{BaseUrl}/movie/{Username}/{Password}/{StreamId}.{Extension}
```

### Episode URL Pattern
```
{BaseUrl}/series/{Username}/{Password}/{EpisodeId}.{Extension}
```

### Example
```
http://provider.example.com:8000/movie/user123/pass456/12345.mp4
```

---

## File Naming Conventions

### Movies
```
{SanitizedName} ({Year})/{SanitizedName} ({Year}).strm
```
Example: `The Matrix (1999)/The Matrix (1999).strm`

### Episodes
```
{SanitizedName} ({Year})/Season {N}/{Show} - S{NN}E{NN} - {Title}.strm
```
Example: `Breaking Bad (2008)/Season 1/Breaking Bad - S01E01 - Pilot.strm`

### Episode Without Title
```
{Show} - S{NN}E{NN}.strm
```
Example: `Show Name - S02E10.strm`

### Generic Title Handling
Titles like "Episode 5" are stripped as they add no value.

---

## Sync Result Metrics

| Metric | Description |
|--------|-------------|
| MoviesCreated | Number of new movie STRM files created |
| MoviesSkipped | Number of movies skipped (already existed) |
| EpisodesCreated | Number of new episode STRM files created |
| EpisodesSkipped | Number of episodes skipped (already existed) |
| FilesDeleted | Number of orphaned files removed |
| Errors | Number of individual item failures |
| Duration | Time taken for sync operation |

---

## Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| BaseUrl | string | "" | Provider URL (e.g., http://host:port) |
| Username | string | "" | Xtream username |
| Password | string | "" | Xtream password |
| LibraryPath | string | "/config/xtream-library" | STRM output directory |
| SyncMovies | bool | true | Enable movie sync |
| SyncSeries | bool | true | Enable series sync |
| SyncIntervalMinutes | int | 60 | Auto-sync interval |
| TriggerLibraryScan | bool | true | Scan library after sync |
| CleanupOrphans | bool | true | Delete removed content |
| UserAgent | string | "" | Custom HTTP User-Agent |
| SelectedVodCategoryIds | List<int> | [] | VOD category IDs to sync (empty=all) |
| SelectedSeriesCategoryIds | List<int> | [] | Series category IDs to sync (empty=all) |

---

## API Response Codes

### POST /XtreamLibrary/Sync
| Code | Condition |
|------|-----------|
| 200 | Sync completed successfully |
| 500 | Sync failed with error |

### GET /XtreamLibrary/Status
| Code | Condition |
|------|-----------|
| 200 | Returns last sync result |
| 204 | No sync has been performed |

### GET /XtreamLibrary/TestConnection
| Code | Condition |
|------|-----------|
| 200 | Connection successful |
| 400 | Credentials not configured |
| 500 | Connection failed |

### GET /XtreamLibrary/Categories/Vod
| Code | Condition |
|------|-----------|
| 200 | Returns VOD categories |
| 400 | Credentials not configured or fetch failed |

### GET /XtreamLibrary/Categories/Series
| Code | Condition |
|------|-----------|
| 200 | Returns Series categories |
| 400 | Credentials not configured or fetch failed |
