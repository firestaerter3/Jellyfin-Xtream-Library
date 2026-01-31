# Jellyfin Xtream Library

A Jellyfin plugin that syncs Xtream VOD and Series content to native Jellyfin libraries via STRM files.

## Why This Plugin?

The standard Xtream channel-based plugin presents content as Jellyfin "Channels" which have limited client support (particularly broken in Swiftfin). This plugin takes a different approach:

1. **STRM Files**: Creates `.strm` files containing streaming URLs
2. **Native Libraries**: Content appears in standard Jellyfin Movie/TV Show libraries
3. **Full Metadata**: Jellyfin fetches metadata from TMDB/TVDb automatically
4. **Universal Compatibility**: Works with all Jellyfin clients including Swiftfin

## Installation

### From Repository

Add the plugin repository to Jellyfin:
- Repository URL: `https://raw.githubusercontent.com/rolandb5/jellyfin-plugin-repo/master/manifest.json`

Then install "Xtream Library" from the plugin catalog.

### Manual Installation

1. Download the latest release DLL
2. Copy to your Jellyfin plugins folder:
   - Docker: `/config/plugins/Xtream Library/`
   - Linux: `~/.local/share/jellyfin/plugins/Xtream Library/`
   - Windows: `%APPDATA%\Jellyfin\plugins\Xtream Library\`
3. Restart Jellyfin

## Configuration

1. Go to **Dashboard → Plugins → Xtream Library**
2. Enter your Xtream provider credentials:
   - Base URL (e.g., `http://provider.com:8000`)
   - Username
   - Password
3. Configure sync options:
   - Library Path (default: `/config/xtream-library`)
   - Enable Movies/VOD sync
   - Enable Series sync
   - Sync interval
4. Click **Save**
5. Click **Test Connection** to verify
6. Click **Run Sync Now** for initial sync

### Adding Libraries to Jellyfin

After the first sync:

1. Go to **Dashboard → Libraries → Add Library**
2. Add a **Movies** library:
   - Folder: `/config/xtream-library/Movies`
3. Add a **TV Shows** library:
   - Folder: `/config/xtream-library/Series`
4. Run a library scan

## File Structure

The plugin creates the following structure:

```
/config/xtream-library/
├── Movies/
│   ├── The Matrix (1999)/
│   │   └── The Matrix (1999).strm
│   └── Inception (2010)/
│       └── Inception (2010).strm
└── Series/
    ├── Breaking Bad (2008)/
    │   ├── Season 1/
    │   │   ├── Breaking Bad - S01E01 - Pilot.strm
    │   │   └── Breaking Bad - S01E02 - Cat's in the Bag.strm
    │   └── Season 2/
    │       └── ...
    └── ...
```

## API Endpoints

The plugin exposes the following API endpoints:

- `POST /XtreamLibrary/Sync` - Trigger manual sync
- `GET /XtreamLibrary/Status` - Get last sync result
- `GET /XtreamLibrary/TestConnection` - Test provider connection

## Scheduled Task

A scheduled task "Sync Xtream Library" runs automatically based on your configured interval. You can also trigger it manually from **Dashboard → Scheduled Tasks**.

## Comparison with Xtream FlatView

| Feature | Xtream FlatView (Channels) | Xtream Library (STRM) |
|---------|---------------------------|----------------------|
| Paradigm | Channel items | Native library items |
| Client support | Limited (Swiftfin broken) | Universal |
| Metadata | Plugin-managed | Jellyfin (TMDB/TVDb) |
| Collections | Not supported | Full support |
| Watch status | Channel-specific | Standard library |
| Live TV | Supported | Not supported |

## Requirements

- Jellyfin 10.11.0 or later
- .NET 9.0 runtime
- Xtream-compatible provider

## Troubleshooting

### STRM files not playing

1. Verify the stream URL works in a browser
2. Check Jellyfin logs for connection errors
3. Ensure your provider allows your IP/device

### Metadata not loading

1. Ensure library type is set correctly (Movies/TV Shows)
2. Check that TMDB/TVDb plugins are enabled
3. Try refreshing metadata manually

### Content missing

1. Check sync logs in Jellyfin dashboard
2. Verify categories exist on your provider
3. Run a manual sync

## License

This project is licensed under the GPL-3.0 License - see the LICENSE file for details.

## Credits

- Xtream API client based on [Jellyfin.Xtream](https://github.com/kevinjil/jellyfin-plugin-xtream) by Kevin Jilissen
