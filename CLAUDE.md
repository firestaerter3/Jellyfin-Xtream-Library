# Jellyfin Xtream Library - Project Context

## Project Overview

A Jellyfin plugin that syncs Xtream VOD and Series content to native Jellyfin libraries via STRM files. This enables universal client compatibility (including Swiftfin) and full metadata support.

## Project Structure

```
Jellyfin.Xtream.Library/
├── Api/SyncController.cs          # REST API endpoints
├── Client/                         # Xtream API client
│   ├── IXtreamClient.cs
│   ├── XtreamClient.cs
│   └── Models/                     # API response models
├── Service/StrmSyncService.cs      # Core sync logic
├── Tasks/SyncLibraryTask.cs        # Scheduled task
├── Configuration/Web/              # Embedded config UI
│   ├── config.html
│   └── config.js
├── Plugin.cs                       # Plugin entry point
├── PluginConfiguration.cs          # Settings model
└── PluginServiceRegistrator.cs     # DI registration

Jellyfin.Xtream.Library.Tests/      # Unit tests (64 tests)
docs/                               # Documentation
├── REQUIREMENTS.md
└── ARCHITECTURE.md
```

## Build & Test

```bash
# Build
dotnet build -c Release

# Run tests
dotnet test -c Release

# Publish for release
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release
```

## Release Process

### 1. Update Version
Edit `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj`:
```xml
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
```

### 2. Commit & Tag
```bash
git add .
git commit -m "Release vX.Y.Z.0: Description"
git tag -a vX.Y.Z.0 -m "Release vX.Y.Z.0: Description"
git push origin main --tags
```

### 3. Build Release Package
```bash
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release
cd /tmp/claude/xtream-library-release
zip -j /tmp/claude/jellyfin-xtream-library_X.Y.Z.0.zip Jellyfin.Xtream.Library.dll
md5 -q /tmp/claude/jellyfin-xtream-library_X.Y.Z.0.zip  # Get checksum
```

### 4. Create GitHub Release
```bash
gh release create vX.Y.Z.0 /tmp/claude/jellyfin-xtream-library_X.Y.Z.0.zip \
  --title "vX.Y.Z.0: Title" \
  --notes "Changelog here"
```

### 5. Update Plugin Repository Manifest
Edit `../jellyfin-plugin-repo/manifest.json` (sibling directory):
- Add new version entry at the top of the versions array
- Include: version, changelog, targetAbi, sourceUrl, checksum, timestamp

```bash
cd ../jellyfin-plugin-repo
git add manifest.json
git commit -m "Add Xtream Library vX.Y.Z.0: Description"
git push
```

## Related Repositories

| Repository | Purpose | URL |
|------------|---------|-----|
| Plugin Source | This repo | https://github.com/firestaerter3/Jellyfin-Xtream-Library |
| Plugin Repo Manifest | Jellyfin plugin catalog | https://github.com/firestaerter3/jellyfin-plugin-repo |
| Manifest URL | For Jellyfin config | https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json |

## Plugin GUID
`a1b2c3d4-e5f6-7890-abcd-ef1234567890` (defined in config.js)

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/XtreamLibrary/Sync` | POST | Trigger manual sync |
| `/XtreamLibrary/Cancel` | POST | Cancel running sync |
| `/XtreamLibrary/Status` | GET | Get last sync result |
| `/XtreamLibrary/Progress` | GET | Get live sync progress |
| `/XtreamLibrary/TestConnection` | POST | Test provider connection |
| `/XtreamLibrary/Categories/Vod` | GET | Fetch VOD categories |
| `/XtreamLibrary/Categories/Series` | GET | Fetch Series categories |
| `/XtreamLibrary/Categories/Live` | GET | Fetch Live TV categories |
| `/XtreamLibrary/RetryFailed` | POST | Retry failed items from last sync |
| `/XtreamLibrary/CleanMovies` | POST | Delete all Movies library content |
| `/XtreamLibrary/CleanSeries` | POST | Delete all Series library content |
| `/XtreamLibrary/ClearMetadataCache` | POST | Clear metadata lookup cache |
| `/XtreamLibrary/LiveTv/RefreshCache` | POST | Refresh Live TV cache |

## Code Analysis

Uses strict code analysis (TreatWarningsAsErrors). Key rules disabled in `jellyfin.ruleset`:
- CA1819: Properties returning arrays (needed for configuration DTOs)
- CA1056: URI properties as strings
- CA1848: LoggerMessage delegates

## Target Framework
- .NET 9.0
- Jellyfin 10.11.0+

## Key Dependencies
- Jellyfin.Controller 10.11.0
- Jellyfin.Model 10.11.0
- Newtonsoft.Json 13.0.3 (required for Xtream API quirks)
