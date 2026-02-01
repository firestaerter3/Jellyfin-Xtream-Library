# Jellyfin Xtream Library

A Jellyfin plugin that syncs Xtream VOD and Series content to native Jellyfin libraries via STRM files. Universal client compatibility, full metadata support, and automatic library integration.

## Features

### Core Functionality
- **STRM File Sync**: Creates `.strm` files containing streaming URLs for Movies and Series
- **Native Libraries**: Content appears in standard Jellyfin Movie/TV Show libraries
- **Universal Compatibility**: Works with all Jellyfin clients including Swiftfin, Infuse, and web
- **Automatic Metadata**: Jellyfin fetches rich metadata from TMDB/TVDb

### Sync Options
- **Category Filtering**: Select specific VOD and Series categories to sync
- **Shift+Click Selection**: Quickly select ranges of categories
- **Parallel Sync**: Configurable parallelism (up to 3x faster syncs)
- **Smart Skip**: Skip API calls for content that already has STRM files
- **Real-Time Progress**: Live sync status showing categories, items, and created counts
- **Cancel Sync**: Stop sync mid-operation with the Cancel button
- **Scheduled Sync**: Choose between interval-based (every X minutes) or daily (specific time)

### Metadata Matching
- **Automatic TMDb/TVDb Lookup**: Uses Jellyfin's configured metadata providers to find IDs
- **Manual ID Overrides**: Force specific TMDb/TVDb IDs for content that doesn't match
- **Language Tag Stripping**: Removes tags like `| NL |`, `[EN]`, `(NL GESPROKEN)` for better matching
- **Folder Name Formatting**: Creates folders like `Movie Name (2023) [tmdbid-12345]` for reliable matching

### Content Organization
- **Single Folder Mode**: All movies/series sync to root Movies/Series folder
- **Multiple Folder Mode**: Organize content into subfolders by category (e.g., `Movies/Kids`, `Series/Documentaries`)
- **Visual Folder UI**: Create folders and assign categories with checkboxes instead of typing IDs
- **Multi-Folder Categories**: Same category can sync to multiple folders

### Artwork & Media Info
- **Provider Artwork Download**: Downloads posters/fanart from your provider for unmatched content
- **Season Posters**: Downloads season cover art for unmatched series
- **Episode Thumbnails**: Downloads episode thumbnails for unmatched content
- **Proactive Media Info**: Fetches resolution, codec, and audio info during sync (optional)
- **NFO Sidecar Files**: Writes Kodi-compatible NFO files with streamdetails for instant media info display

### Library Management
- **Orphan Cleanup**: Removes STRM files for content no longer on the provider
- **Safety Protection**: Skips cleanup if >20% would be deleted (provider glitch protection)
- **Delete All Content**: One-click button to clear libraries before re-syncing
- **Library Scan Trigger**: Automatically triggers Jellyfin scan after sync

## Why This Plugin?

The standard Xtream channel-based plugin presents content as Jellyfin "Channels" which have limited client support (particularly broken in Swiftfin). This plugin takes a different approach:

| Feature | Xtream Channels | Xtream Library (STRM) |
|---------|----------------|----------------------|
| Paradigm | Channel items | Native library items |
| Client support | Limited | Universal |
| Metadata | Plugin-managed | Jellyfin (TMDB/TVDb) |
| Collections | Not supported | Full support |
| Watch status | Channel-specific | Standard library |
| Live TV | Supported | Not supported |

## Installation

### From Repository (Recommended)

Add the plugin repository to Jellyfin:

1. Open **Dashboard → Plugins → Repositories**
2. Click `+` to add a repository
3. **Name:** `Xtream Plugins`
4. **URL:** `https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json`
5. Click **Save**

Then install "Xtream Library" from the plugin catalog (under General category).

### Manual Installation

1. Download the latest release from [GitHub Releases](https://github.com/firestaerter3/Jellyfin-Xtream-Library/releases)
2. Copy to your Jellyfin plugins folder:
   - Docker: `/config/plugins/Xtream Library/`
   - Linux: `~/.local/share/jellyfin/plugins/Xtream Library/`
   - Windows: `%APPDATA%\Jellyfin\plugins\Xtream Library\`
3. Restart Jellyfin

## Configuration

### Initial Setup

1. Go to **Dashboard → Plugins → Xtream Library**
2. Enter your Xtream provider credentials:
   - Base URL (e.g., `http://provider.com:8000`)
   - Username
   - Password
3. Click **Test Connection** to verify
4. Configure library path (default: `/config/xtream-library`)
5. Click **Save**

### Category Selection

1. Go to the **Movies** or **Series** tab
2. Click **Load Categories**
3. Check the categories you want to sync (shift+click for ranges)
4. Leave all unchecked to sync everything

### Folder Organization (Optional)

To organize content into subfolders:

1. Change **Folder Mode** to "Multiple Folders"
2. Click **+ Add Folder** to create a folder (e.g., "Kids")
3. Check the categories to include in that folder
4. Repeat for additional folders
5. Categories not assigned to any folder sync to the root

### Metadata Matching (Optional)

For automatic TMDb/TVDb ID lookup:

1. Ensure TMDb and/or TVDb plugins are installed in Jellyfin
2. Enable **Automatic Metadata ID Lookup** in plugin settings
3. For content that doesn't match, add manual overrides:
   - Movies: `The Matrix (1999)=603`
   - Series: `Breaking Bad (2008)=81189`

### First Sync

1. Click **Run Sync Now**
2. Monitor progress in real-time
3. Add libraries to Jellyfin after sync completes

### Adding Libraries to Jellyfin

1. Go to **Dashboard → Libraries → Add Library**
2. Add a **Movies** library:
   - Folder: `/config/xtream-library/Movies`
3. Add a **TV Shows** library:
   - Folder: `/config/xtream-library/Series`
4. Run a library scan

## File Structure

```
/config/xtream-library/
├── Movies/
│   ├── The Matrix (1999) [tmdbid-603]/
│   │   ├── The Matrix (1999) [tmdbid-603].strm
│   │   └── The Matrix (1999) [tmdbid-603].nfo
│   ├── Kids/                              # Custom subfolder
│   │   └── Finding Nemo (2003) [tmdbid-12]/
│   │       └── Finding Nemo (2003) [tmdbid-12].strm
│   └── ...
└── Series/
    ├── Breaking Bad (2008) [tvdbid-81189]/
    │   ├── Season 1/
    │   │   ├── Breaking Bad - S01E01 - Pilot.strm
    │   │   ├── Breaking Bad - S01E01 - Pilot.nfo
    │   │   └── ...
    │   └── Season 2/
    │       └── ...
    └── ...
```

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/XtreamLibrary/Sync` | POST | Trigger manual sync |
| `/XtreamLibrary/Cancel` | POST | Cancel running sync |
| `/XtreamLibrary/Status` | GET | Get last sync result |
| `/XtreamLibrary/Progress` | GET | Get real-time sync progress |
| `/XtreamLibrary/TestConnection` | POST | Test provider connection |
| `/XtreamLibrary/Categories/Vod` | GET | Fetch VOD categories |
| `/XtreamLibrary/Categories/Series` | GET | Fetch Series categories |
| `/XtreamLibrary/ClearMetadataCache` | POST | Clear metadata lookup cache |
| `/XtreamLibrary/CleanLibraries` | POST | Delete all library content |

## Scheduled Task

A scheduled task "Sync Xtream Library" runs automatically based on your settings:
- **Interval Mode**: Runs every X minutes (configurable, 10-1440)
- **Daily Mode**: Runs once per day at a specific time

Trigger manually from **Dashboard → Scheduled Tasks**.

## Requirements

- Jellyfin 10.11.0 or later
- .NET 9.0 runtime
- Xtream-compatible provider

## Troubleshooting

### STRM files not playing

1. Verify the stream URL works in a browser/VLC
2. Check Jellyfin logs for connection errors
3. Ensure your provider allows your IP/device
4. Try a different container extension in provider settings

### Metadata not loading

1. Ensure library type is set correctly (Movies/TV Shows)
2. Check that TMDB/TVDb plugins are enabled and configured
3. Enable "Automatic Metadata ID Lookup" for better matching
4. Add manual ID overrides for problematic content
5. Try refreshing metadata manually

### Content missing

1. Check sync logs in Jellyfin dashboard
2. Verify the category is selected in plugin settings
3. Run a manual sync and check for errors
4. Look for the content in failed items list

### Sync is slow

1. Increase sync parallelism (default: 3)
2. Enable "Smart Skip Existing" to skip unchanged content
3. Disable "Proactive Media Info" if not needed
4. Select only the categories you need

## License

This project is licensed under the GPL-3.0 License - see the LICENSE file for details.

## Credits

- Xtream API client based on [Jellyfin.Xtream](https://github.com/kevinjil/jellyfin-plugin-xtream) by Kevin Jilissen

## Links

- **Plugin Repository**: https://github.com/firestaerter3/Jellyfin-Xtream-Library
- **Manifest URL**: https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json
- **Issues**: https://github.com/firestaerter3/Jellyfin-Xtream-Library/issues
