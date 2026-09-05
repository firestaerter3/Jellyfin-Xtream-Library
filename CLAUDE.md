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

# Config page tests (dependency-free, no jsdom). CI runs both of these.
node --check Jellyfin.Xtream.Library/Configuration/Web/config.js
node --test "tests/js/**/*.test.js"

# Publish for release
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release
```

### What the config page tests can and cannot catch

`tests/js` loads `config.js` into Node against a hand-rolled `document` stub, and the folder-mode
tests work by stubbing `renderCategoryList` and asserting the arguments it receives. That proves the
*decision* logic. It proves nothing about what the browser then renders, because there is no DOM and
no custom elements in the harness.

So a green suite does not clear a bug reported as "the UI shows the wrong thing". Reproduce those
against a real page. `emby-checkbox` is registered through `document.registerElement`, the
webcomponents v0 polyfill, on any Jellyfin 10.11 page including the login screen, so the component
can be exercised without reaching the plugin config page at all.

One result worth not re-deriving, from #81: **`emby-checkbox` does not clear `checked` during
upgrade, and being inside a `change` dispatch changes nothing.** Measured with the real component,
the attribute and the property both survive insertion on every path. If checkboxes come back
unticked, look at the data being passed to the render, not at the component.

## Release Process

Releases go to the **beta channel first**, then are promoted to stable. Never publish directly to stable.

### 1. Update Version
Edit `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj`:
```xml
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
```

This edit must land in the commit that gets tagged. The **tag is the source of truth** for the
shipped version: CI derives `AssemblyVersion`/`FileVersion` from the tag name, and a separate step
fails the release when this csproj disagrees. Nothing used to check, which is why v1.42.0.0 and
v1.42.1.0 both shipped DLLs reporting `1.41.0.0` (issue #69).

### 2. Commit & Tag
```bash
git add .
git commit -m "Release vX.Y.Z.0: Description"
git tag -a vX.Y.Z.0 -m "Release vX.Y.Z.0: Description"
git push origin main --tags
```

### 3. Build & Release (automatic)
Pushing the tag triggers `.github/workflows/build-release.yml`, which:
- derives the version from the tag and stamps it into the build,
- fails if csproj disagrees with the tag,
- verifies the built DLL actually carries that version,
- creates the GitHub release if it does not exist and uploads
  `jellyfin-xtream-library_X.Y.Z.0.zip` (numeric version, no `v` — that is what the manifest
  `sourceUrl` points at).

Get the checksum for the manifest entry:
```bash
gh release download vX.Y.Z.0 -D /tmp/claude/rel -p 'jellyfin-xtream-library_*.zip'
openssl md5 /tmp/claude/rel/jellyfin-xtream-library_X.Y.Z.0.zip | awk '{print $NF}'  # portable: macOS + Linux
```

To re-stamp an already-published tag, run the workflow manually with the `existing_tag` input.
It replaces the asset of the same name, so remember to update the checksum in the manifest.

<details>
<summary>Manual fallback (only if CI is unavailable)</summary>

```bash
VER=X.Y.Z.0
dotnet publish Jellyfin.Xtream.Library -c Release \
  -p:Version=$VER -p:AssemblyVersion=$VER -p:FileVersion=$VER \
  -o /tmp/claude/xtream-library-release
cd /tmp/claude/xtream-library-release
zip -j /tmp/claude/jellyfin-xtream-library_$VER.zip Jellyfin.Xtream.Library.dll
openssl md5 /tmp/claude/jellyfin-xtream-library_$VER.zip | awk '{print $NF}'
gh release create v$VER /tmp/claude/jellyfin-xtream-library_$VER.zip \
  --title "v$VER: Title" --notes "Changelog here"
```
Pass the version properties explicitly; a bare `dotnet publish` stamps whatever csproj happens to say.
</details>

### 5. Publish to Beta Channel
Edit `../jellyfin-plugin-repo/manifest-dev.json` (sibling directory):
- Add new version entry at the top of the versions array
- Include: version, changelog, targetAbi, sourceUrl, checksum, timestamp

```bash
cd ../jellyfin-plugin-repo
git add manifest-dev.json
git commit -m "Beta: Xtream Library vX.Y.Z.0: Description"
git push
```

### 6. Promote to Stable (separate step, on user request)
Edit `../jellyfin-plugin-repo/manifest.json`:
- Add the same version entry at the top of the versions array

```bash
cd ../jellyfin-plugin-repo
git add manifest.json
git commit -m "Stable: Xtream Library vX.Y.Z.0: Description"
git push
```

A beta build is normally left to soak about **a week** before promotion. Check what is overdue by
diffing the two manifests rather than going by memory: anything in `manifest-dev.json` that is not
in `manifest.json` and is older than that is a candidate. Promote by copying the entry **verbatim**
from the beta manifest, so version, changelog, `targetAbi`, `sourceUrl` and `checksum` cannot drift
between channels. Verify the asset still matches its recorded checksum before promoting.

Soak time is not the only gate. A release that migrates or renames anything on disk needs longer and
needs its warning leading the changelog, because the migration is what users cannot undo. v1.44.0.0
is the standing example: it renames movie folders carrying version tags, and the old folders become
orphans that the next sync deletes when cleanup is on, taking watched status and artwork with them.

### Editing the manifests

Four traps, all of which produce a diff that hides the one line you meant to change, or a change
landing somewhere it should not:

- **The two files are formatted differently.** `manifest.json` is **2-space** indented, has **no
  trailing newline**, and stores non-ASCII **escaped** (`—`). `manifest-dev.json` is
  **4-space**, ends with a newline, and stores literal UTF-8. Writing either with the other's
  convention rewrites all ~1900 lines. In Python: `indent=2, ensure_ascii=True` and no trailing
  write for stable, `indent=4, ensure_ascii=False` plus `f.write('\n')` for beta.
- **`manifest.json` contains two plugins named "Xtream Library".** Match on the GUID, not the name.
  The live one is `63ba5fcd-c8ce-421a-83e8-ba0b11030d53`; `a1b2c3d4-e5f6-7890-abcd-ef1234567890` is
  the abandoned pre-#43 entry, frozen at 1.33.2.0, and must stay frozen.
- **The PII pre-commit hook reads version numbers as IP addresses.** A changelog mentioning
  `1.44.0.0` trips `WARNING (ip)`. That is a false positive and `--no-verify` is the right call for
  manifest commits; read the hook output first to confirm it is only version strings.
- **Always check `git diff --stat` before committing a manifest.** The expected result is roughly 8
  insertions and 0 deletions. Anything larger means the formatting drifted.

## Related Repositories

| Repository | Purpose | URL |
|------------|---------|-----|
| Plugin Source | This repo | https://github.com/firestaerter3/Jellyfin-Xtream-Library |
| Plugin Repo Manifest | Jellyfin plugin catalog | https://github.com/firestaerter3/jellyfin-plugin-repo |
| Manifest URL | For Jellyfin config | https://firestaerter3.github.io/jellyfin-plugin-repo/manifest.json |

## Plugin GUID
`63ba5fcd-c8ce-421a-83e8-ba0b11030d53` (defined in config.js and Plugin.cs)

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
| `/XtreamLibrary/Channels/Live` | GET | Fetch channels in a Live TV category (`?categoryId=`) |
| `/XtreamLibrary/ChannelLogo/{streamId}` | GET | Serve a local-path channel override logo |
| `/XtreamLibrary/Streams/Vod` | GET | Fetch movies in a VOD category (`?categoryId=&providerIndex=`) |
| `/XtreamLibrary/Series/List` | GET | Fetch series in a Series category (`?categoryId=&providerIndex=`) |
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
