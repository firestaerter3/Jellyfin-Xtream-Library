# Architecture

This document describes the architecture of the Jellyfin Xtream Library plugin.

## Overview

The plugin syncs content from an Xtream-compatible IPTV provider to Jellyfin's native library system using STRM files. This approach allows Jellyfin to treat streaming content as regular media files, enabling full metadata support and universal client compatibility.

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│  Xtream API     │────▶│  Xtream Library  │────▶│  STRM Files     │
│  (Provider)     │     │  Plugin          │     │  (File System)  │
└─────────────────┘     └──────────────────┘     └─────────────────┘
                                                         │
                                                         ▼
                                                 ┌─────────────────┐
                                                 │  Jellyfin       │
                                                 │  Libraries      │
                                                 └─────────────────┘
```

## Project Structure

```
Jellyfin.Xtream.Library/
├── Api/                        # REST API controllers
│   └── SyncController.cs       # Manual sync & status endpoints
├── Client/                     # API clients
│   ├── IXtreamClient.cs        # Xtream client interface
│   ├── XtreamClient.cs         # Xtream HTTP client implementation
│   ├── IDispatcharrClient.cs   # Dispatcharr REST API interface
│   ├── DispatcharrClient.cs    # Dispatcharr JWT-authenticated client
│   ├── ConnectionInfo.cs       # Connection credentials wrapper
│   ├── Models/                 # API response models
│   │   ├── Category.cs
│   │   ├── Episode.cs
│   │   ├── Series.cs
│   │   ├── StreamInfo.cs       # VOD stream info
│   │   ├── DispatcharrTokenResponse.cs  # Dispatcharr REST API models
│   │   └── ...
│   └── *Converter.cs           # JSON converters for API quirks
├── Service/
│   ├── StrmSyncService.cs      # Core sync logic
│   ├── BetaChannelManager.cs   # Pure decision logic for beta repo entry
│   └── BetaChannelManager.HostedService.cs  # IHostedService wiring
├── Tasks/
│   └── SyncLibraryTask.cs      # Scheduled task wrapper
├── Configuration/
│   └── Web/                    # Embedded web UI (config.html/js)
├── Plugin.cs                   # Plugin entry point
├── PluginConfiguration.cs      # Configuration model
└── PluginServiceRegistrator.cs # DI registration
```

## Component Responsibilities

### Plugin.cs
- Plugin entry point implementing `BasePlugin<PluginConfiguration>`
- Provides singleton `Instance` access
- Exposes `ConnectionInfo` wrapper from configuration
- Registers embedded web pages for configuration UI

### PluginConfiguration.cs
- Persisted configuration model
- Provider credentials (BaseUrl, Username, Password)
- Sync options (LibraryPath, SyncMovies, SyncSeries)
- Behavior settings (SyncInterval, TriggerLibraryScan, CleanupOrphans)
- Updates (UseBetaChannel — opt-in for pre-release versions)

### PluginServiceRegistrator.cs
- Registers services with Jellyfin's DI container:
  - `IXtreamClient` → `XtreamClient` (HttpClient)
  - `IDispatcharrClient` → `DispatcharrClient` (HttpClient)
  - `StrmSyncService` (Singleton)
  - `IScheduledTask` → `SyncLibraryTask` (Singleton)
  - `BetaChannelManager` (IHostedService)

### BetaChannelManager
- Two-file partial class split for testability:
  - `BetaChannelManager.cs` holds pure decision logic — `ComputeNextRepositories` (append or remove the beta `RepositoryInfo` based on `UseBetaChannel`) and `StructurallyEqual` (Name+Url+Enabled triple comparison). No DI, no I/O, no `Plugin.Instance` access — covered by 13 unit tests.
  - `BetaChannelManager.HostedService.cs` wires the pure logic to `IServerConfigurationManager`. `StartAsync` performs an initial reconcile and subscribes to `Plugin.Instance.ConfigurationChanged`; `StopAsync` unsubscribes. `Sync()` mutates `ServerConfiguration.PluginRepositories` and calls `SaveConfiguration()`, with `IOException`/`UnauthorizedAccessException` caught and rolled back in-memory so a transient disk error never leaves runtime state divergent from `system.xml`.
- The beta manifest URL (`https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json`) is matched URL-only and case-insensitively, so a user-renamed entry is treated as the beta entry and not duplicated.

### XtreamClient
- HTTP client for Xtream API communication
- Endpoints:
  - `GetUserAndServerInfoAsync` - Authentication/connection test
  - `GetVodCategoryAsync` / `GetVodStreamsByCategoryAsync` - Movies
  - `GetSeriesCategoryAsync` / `GetSeriesByCategoryAsync` - Series list
  - `GetSeriesStreamsBySeriesAsync` - Episode details
- Custom JSON converters handle API inconsistencies:
  - `StringBoolConverter` - "1"/"0" to boolean
  - `SingularToListConverter` - Single value or array
  - `OnlyObjectConverter` - Ignore non-object responses

### DispatcharrClient
- JWT-authenticated HTTP client for Dispatcharr's REST API
- Used when `EnableDispatcharrMode` is configured
- Endpoints:
  - `POST /api/token/` - JWT login (username/password → access + refresh tokens)
  - `POST /api/token/refresh/` - Refresh expired access token
  - `GET /api/vod/movies/{id}/` - Movie detail (UUID for proxy URLs)
  - `GET /api/vod/movies/{id}/providers/` - All stream relations per movie
- Token management: caches access token, refreshes before 5-min expiry
- Graceful degradation: returns null/empty on failure, sync falls back to standard mode

### StrmSyncService
Core synchronization logic:

1. **Movie Sync** (`SyncMoviesAsync`)
   - Fetches VOD categories
   - For each stream, creates folder structure: `Movies/{Name} ({Year})/`
   - Writes STRM file with streaming URL
   - Tracks processed stream IDs to skip duplicates across categories

2. **Series Sync** (`SyncSeriesAsync` → `SyncSingleSeriesAsync`)
   - Fetches series categories
   - For each series, fetches episode list
   - Creates folder structure: `Series/{Name} ({Year})/Season N/`
   - Writes episode STRM files with format: `{Show} - S{NN}E{NN} - {Title}.strm`

3. **Orphan Cleanup** (optional)
   - Collects existing STRM files before sync
   - After sync, deletes files not in synced set
   - Removes empty parent directories

4. **Movie Versions** (see [MOVIE_VERSIONS.md](MOVIE_VERSIONS.md))
   - `ExtractVersionLabel` - Extracts codec/quality/source tags as version label
   - `BuildMovieStrmFileName` - Constructs STRM filename with optional version suffix
   - Multiple quality variants (e.g., default, HEVC, 4K) create separate STRMs in the same folder
   - Jellyfin natively detects these and shows a version picker during playback

5. **Utility Methods**
   - `SanitizeFileName` - Removes invalid chars, codec/quality tags, collapses underscores
   - `ExtractYear` - Parses year from title like "Movie (2024)"
   - `BuildEpisodeFileName` - Formats episode filename with padding
   - `TruncateFileNameToFsLimit` - Caps STRM filenames at 255 UTF-8 bytes (Linux NAME_MAX), preserving the `.strm` extension and respecting multi-byte character boundaries
   - `CleanupEmptyDirectories` - Recursive empty dir removal

### SyncController
REST API for manual operations:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/XtreamLibrary/Sync` | POST | Trigger manual sync |
| `/XtreamLibrary/Status` | GET | Get last sync result |
| `/XtreamLibrary/TestConnection` | POST | Verify provider connection |
| `/XtreamLibrary/Categories/Vod` | GET | Fetch VOD categories from provider |
| `/XtreamLibrary/Categories/Series` | GET | Fetch Series categories from provider |

All endpoints require admin authorization (`RequiresElevation` policy).

### SyncLibraryTask
Jellyfin scheduled task wrapper:
- Implements `IScheduledTask` for dashboard visibility
- Configurable interval from `SyncIntervalMinutes` setting
- Delegates to `StrmSyncService.SyncAsync()`

## Data Flow

### Sync Process

```
1. Trigger (Scheduled Task or API)
         │
         ▼
2. StrmSyncService.SyncAsync()
         │
         ├──▶ Collect existing STRM files (if CleanupOrphans)
         │
         ├──▶ SyncMoviesAsync()
         │      │
         │      ├─▶ XtreamClient.GetVodCategoryAsync()
         │      │
         │      ├─▶ Filter categories by SelectedVodCategoryIds (if not empty)
         │      │
         │      └─▶ For each selected category:
         │           └─▶ XtreamClient.GetVodStreamsByCategoryAsync()
         │                └─▶ Create folder + Write STRM file
         │
         ├──▶ SyncSeriesAsync()
         │      │
         │      ├─▶ XtreamClient.GetSeriesCategoryAsync()
         │      │
         │      ├─▶ Filter categories by SelectedSeriesCategoryIds (if not empty)
         │      │
         │      └─▶ For each selected category:
         │           └─▶ XtreamClient.GetSeriesByCategoryAsync()
         │                └─▶ For each series:
         │                     └─▶ XtreamClient.GetSeriesStreamsBySeriesAsync()
         │                          └─▶ Create season folders + Write STRM files
         │
         ├──▶ Delete orphaned files (if CleanupOrphans)
         │
         └──▶ Trigger Jellyfin library scan (if TriggerLibraryScan)
```

### STRM File Format

STRM files contain a single line with the streaming URL:

```
{BaseUrl}/movie/{Username}/{Password}/{StreamId}.{Extension}
{BaseUrl}/series/{Username}/{Password}/{EpisodeId}.{Extension}
```

## Dependencies

### Jellyfin Framework
- `Jellyfin.Controller` - Plugin infrastructure, library manager
- `Jellyfin.Model` - Configuration, task interfaces
- `Microsoft.AspNetCore.App` - Web API controllers

### Third-Party
- `Newtonsoft.Json` - JSON serialization (required for Xtream API quirks)

## Thread Safety

- `XtreamClient` uses shared `HttpClient` (thread-safe)
- `StrmSyncService` maintains no mutable state between syncs
- `LastSyncResult` property provides read-only access to last result
- File operations are sequential within each sync

## Error Handling

- Individual stream/episode failures are logged but don't abort sync
- `SyncResult.Errors` counter tracks failures
- `SyncResult.Error` contains exception message for complete failures
- API returns 500 with error details on sync failure

## Extension Points

1. **Additional Content Types**: Add methods for Live TV channels
2. **Metadata Extraction**: Parse provider metadata to NFO files
3. **Selective Sync**: Filter by category/genre/year
4. **Progress Reporting**: Detailed progress during long syncs
