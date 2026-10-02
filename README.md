<p align="center">
  <img src="images/logo.svg" alt="Xtream Library" width="400"/>
</p>

# Jellyfin Xtream Library

A Jellyfin plugin that syncs Xtream VOD, Series, and Live TV content to native Jellyfin libraries. STRM-based movies and series with universal client compatibility, plus a native Live TV tuner with full EPG support.

## Features

### Core Functionality
- **STRM File Sync**: Creates `.strm` files containing streaming URLs for Movies and Series
- **Native Libraries**: Content appears in standard Jellyfin Movie/TV Show libraries
- **Universal Compatibility**: Works with all Jellyfin clients including Swiftfin, Infuse, and web
- **Automatic Metadata**: Jellyfin fetches rich metadata from TMDB/TVDb

### Live TV
- **Native Tuner Host**: Registers as a Jellyfin tuner — no M3U tuner plugin needed
- **EPG / Programme Guide**: XMLTV endpoint with configurable days (1-14) and parallel fetching
- **Pre-Populated Stream Stats**: Fetches codec, resolution, fps, and bitrate from provider to skip FFmpeg probing
- **Catchup / Timeshift**: Replay past programmes with configurable catchup window (1-30 days)
- **Channel Name Cleaning**: Strips tags like `| HD |`, `[EN]`, `UK:`, codec info, and resolution suffixes
- **Channel Name Overrides**: Override name, number, or logo per channel (`StreamId=Name|Number|Logo`). The logo can be an http(s) URL or a local file path readable by the Jellyfin server (bind-mount the folder into the container for Docker installs).
- **Adult Channel Filtering**: Exclude adult channels from guide and playlist
- **Category Selection**: Filter Live TV channels by category (empty = all)
- **Per-Channel Selection**: Expand any selected category to enable or disable individual channels
- **Category Grouping**: Channels are tagged with their Xtream category so the Live TV guide groups them instead of showing one flat list (native tuner `ChannelGroup` / M3U `group-title`)
- **Dispatcharr Integration**: Enhanced stream stats and multi-variant stream support

### Sync Options
- **Category Filtering**: Select specific VOD, Series, and Live TV categories to sync
- **Per-Item Selection**: Expand any Movie or Series category (Single folder mode) to enable or disable individual titles, the same way Live TV lets you pick individual channels
- **Shift+Click Selection**: Quickly select ranges of categories
- **Incremental Sync**: Only fetches changed content after the first full sync (delta-based with checksums)
- **Parallel Sync**: Configurable parallelism (1-20 concurrent requests)
- **Category Batching**: Process categories in configurable batch sizes to control memory usage
- **Smart Skip**: Skip API calls for series that already have STRM files
- **Rate Limiting**: Configurable delay between API requests with automatic retry on HTTP 429
- **Real-Time Progress**: Live sync status showing categories, items, and created counts
- **Cancel Sync**: Stop sync mid-operation with the Cancel button
- **Retry Failed Items**: Re-attempt items that failed in the last sync
- **Scheduled Sync**: Choose between interval-based (every X minutes) or daily (specific time)

### Metadata Matching
- **Automatic TMDb/TVDb Lookup**: Uses Jellyfin's configured metadata providers to find IDs
- **Manual ID Overrides**: Force specific TMDb/TVDb IDs for content that doesn't match
- **Language Tag Stripping**: Removes tags like `| EN |`, `[DE]`, `(EN SPOKEN)` for better matching
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
- **Separate Clean Buttons**: Delete Movies or Series library content independently
- **Sync History**: View last 10 sync runs with timestamps and stats
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
| Live TV | Supported | Supported (native tuner + EPG) |

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

### Beta Channel (Opt-In)

Pre-release versions ship to a separate beta channel before being promoted to stable. To opt in:

1. Install the stable plugin first (steps above).
2. Open **Dashboard → Plugins → Xtream Library → Settings → General → Updates**.
3. Tick **Use beta channel** and click Save.

Beta releases will then appear in **Dashboard → Plugins → Catalog** alongside stable versions. Untick the box to opt back out. Beta releases may contain bugs or incomplete features — opting in helps catch regressions before they reach the stable channel.

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
5. Optionally, expand any selected category with the ▸ toggle to enable or disable individual movies or series (all enabled by default). Unticked titles are skipped, and removed on the next sync if they already exist. Per-item selection is available in Single folder mode.

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

### Live TV Setup

1. Go to the **Live TV** tab in plugin settings
2. Enable **Live TV**
3. Click **Load Categories** and select the Live TV categories you want
   - Optionally, expand any category with the ▸ toggle to enable or disable individual channels (all enabled by default). After editing per-channel selections, hit **Refresh Live TV cache** for the M3U/EPG to pick up the change.
4. Enable **Native Tuner** to register as a Jellyfin tuner (recommended)
5. Enable **EPG** for programme guide data (fetches XMLTV automatically)
6. Optionally enable **Catchup** for timeshift replay support
7. Click **Save**

Once enabled, go to **Dashboard → Live TV** in Jellyfin — the Xtream Library tuner and guide data will appear automatically. Channels are grouped by their Xtream category in the guide. To trim a large channel list down, set the Live channel mode to **Custom** and select only the categories you want — by default every channel the provider exposes is included.

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
| `/XtreamLibrary/History` | GET | Sync history (last 10 runs) |
| `/XtreamLibrary/Dashboard` | GET | Dashboard data (sync, progress, history, stats) |
| `/XtreamLibrary/FailedItems` | GET | Failed items from last sync |
| `/XtreamLibrary/RetryFailed` | POST | Retry failed items |
| `/XtreamLibrary/TestConnection` | POST | Test Xtream provider connection |
| `/XtreamLibrary/TestDispatcharr` | POST | Test Dispatcharr API connection |
| `/XtreamLibrary/Categories/Vod` | GET | Fetch VOD categories |
| `/XtreamLibrary/Categories/Series` | GET | Fetch Series categories |
| `/XtreamLibrary/Categories/Live` | GET | Fetch Live TV categories |
| `/XtreamLibrary/Channels/Live` | GET | Fetch channels in a Live TV category (`?categoryId=`) |
| `/XtreamLibrary/Streams/Vod` | GET | Fetch movies in a VOD category (`?categoryId=&providerIndex=`) |
| `/XtreamLibrary/Series/List` | GET | Fetch series in a Series category (`?categoryId=&providerIndex=`) |
| `/XtreamLibrary/CleanMovies` | POST | Delete all Movies library content |
| `/XtreamLibrary/CleanSeries` | POST | Delete all Series library content |
| `/XtreamLibrary/ClearMetadataCache` | POST | Clear metadata lookup cache |
| `/XtreamLibrary/LiveTv/RefreshCache` | POST | Refresh Live TV M3U/EPG cache |
| `/XtreamLibrary/LiveTv.m3u` | GET | M3U playlist (no auth; only with Publish Playlists on) |
| `/XtreamLibrary/Epg.xml` | GET | XMLTV EPG data (no auth) |
| `/XtreamLibrary/Catchup.m3u` | GET | Catch-up channels M3U (no auth; only with Publish Playlists on) |
| `/XtreamLibrary/ChannelLogo/{streamId}` | GET | Serve a local-path channel override logo (no auth) |

## Scheduled Task

A scheduled task "Sync Xtream Library" runs automatically based on your settings:
- **Interval Mode**: Runs every X minutes (configurable, 10-1440)
- **Daily Mode**: Runs once per day at a specific time

Trigger manually from **Dashboard → Scheduled Tasks**.

## Requirements

- Jellyfin 10.11.0 or later
- .NET 9.0 runtime
- Xtream-compatible provider
- Xtream provider with Live TV streams (for Live TV features)
- Dispatcharr (optional, for enhanced stream stats and multi-variant streams)

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

1. Increase sync parallelism (default: 10)
2. Enable "Smart Skip Existing" to skip unchanged content
3. Disable "Proactive Media Info" if not needed
4. Select only the categories you need

### Live TV not showing channels

1. Verify Live TV is enabled in plugin settings
2. Check that at least one Live TV category is selected
3. Ensure the native tuner is enabled
4. Go to Dashboard → Live TV and check that the tuner is detected
5. Refresh the Live TV cache from plugin settings

## Security Note: Credentials in STRM Files and Live TV URLs

The Xtream Codes protocol requires the username and password to appear directly in every stream
URL. There is no token or session form. Your Xtream credentials therefore end up in plaintext in
two places:

- The `.strm` files written under your library path
- The `LiveTv.m3u` and `Catchup.m3u` output served by the plugin

This is a property of the protocol, not a plugin defect, and the same is true of any Xtream-based
tooling. Anything that can read those files (other containers sharing the volume, backup tools,
indexers, users with shell access on the host) can harvest the credentials.

**The Live TV endpoints are reachable without a Jellyfin login.** `LiveTv.m3u`, `Epg.xml`,
`Catchup.m3u` and `ChannelLogo/{streamId}` are deliberately anonymous, because Jellyfin's own M3U
tuner and XMLTV guide fetcher send no Jellyfin credentials when they call them. The practical
consequence is that anyone who can reach one of those URLs gets your Xtream username and password,
once per channel, with no login. The path is the same on every install of this plugin, and if your
server is fronted by a real TLS certificate its hostname is already public in certificate
transparency logs, so an unlisted URL is not a defence. **Treat these URLs as equivalent to your
Xtream password.**

**How to limit the exposure:**

- **Do not expose the plugin's endpoints to the open internet.** If Jellyfin itself is public,
  restrict `/XtreamLibrary/` at your reverse proxy to the addresses that actually need it.
- **Prefer the native tuner over the M3U tuner.** With Enable Native Tuner the plugin hands
  channels to Jellyfin internally and never publishes a playlist, so no credential-bearing URL is
  served to an anonymous caller.
- **Leave Publish Playlists off unless something reads the playlists.** `LiveTv.m3u` and
  `Catchup.m3u` are only served while it is on; otherwise they answer 404. Only Jellyfin's M3U
  tuner and external players such as TiviMate need them. It is off when you enable Live TV on a new
  install, and stays on after upgrading if Live TV was already enabled. `Epg.xml` and the channel
  logo endpoint carry no credentials and are not affected.
- **Restrict the endpoints to known addresses.** "Restrict These URLs To" on the Live TV tab takes
  one IP or CIDR range per line; anything else gets a 404. Behind a reverse proxy, add the proxy to Jellyfin's Known
  Proxies (Dashboard, Networking) first. Otherwise every request through it counts as coming from
  the proxy's own address, and if that address is on the list (a private range, say), the whole
  internet gets through.
- **Restrict filesystem permissions.** Make sure the library path is not readable by other users or
  containers on the host.
- **Use a dedicated provider account.** If your provider supports multiple credential sets per
  subscription, use a separate one for Jellyfin so it can be rotated on its own.

With Dispatcharr mode on, channels Dispatcharr knows are written to the playlist as Dispatcharr's own
proxy URLs, which carry no credentials. Channels it does not know still carry the Xtream ones.

---

## License

This project is licensed under the GPL-3.0 License - see the LICENSE file for details.

## Credits

- Xtream API client based on [Jellyfin.Xtream](https://github.com/kevinjil/jellyfin-plugin-xtream) by Kevin Jilissen

## Links

- **Plugin Repository**: https://github.com/firestaerter3/Jellyfin-Xtream-Library
- **Manifest URL**: https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json
- **Issues**: https://github.com/firestaerter3/Jellyfin-Xtream-Library/issues
