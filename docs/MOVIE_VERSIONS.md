# Movie Versions: Multi-Quality Variant Support

## Problem Statement

Xtream providers often offer the same movie in multiple quality variants. A single title may appear as several streams with codec/quality/source suffixes in the name:

| Stream Name | Intended Variant |
|-------------|-----------------|
| `┃NL┃ Gladiator` | Default (no tag) |
| `┃NL┃ Gladiator HEVC` | HEVC codec |
| `┃NL┃ Gladiator II` | Default (no tag) |
| `┃NL┃ Gladiator II [4K]` | 4K quality |
| `┃NL┃ Gladiator II HEVC` | HEVC codec |

Because `SanitizeFileName` strips codec/quality/source tags to produce clean folder names, all variants of the same title resolve to the same STRM filename. The last stream processed silently overwrites the previous one — quality variants are lost with no indication.

### Scale of Impact

Real provider data shows 1,654 movies (9.5% of library) with multiple stream relations. These are quality variants that should be preserved.

## Solution

Extract quality/codec/source tags from the original stream name and encode them as Jellyfin-compatible version label suffixes in the STRM filename. Jellyfin natively detects multiple STRM files in the same movie folder and presents a version picker in the UI.

### Jellyfin Version Detection

Jellyfin's built-in "movie versions" feature works by detecting multiple video files in the same movie folder. When it finds them, it groups them under a single library entry and shows a version picker during playback. No additional configuration is needed — the filename suffix after ` - ` becomes the version label.

Reference: [Jellyfin Movie Versions Documentation](https://jellyfin.org/docs/general/server/media/movies/#multiple-versions-of-a-movie)

## Architecture

### Component Overview

```
Stream Name                    ExtractVersionLabel()         BuildMovieStrmFileName()
─────────────                  ─────────────────────         ────────────────────────
"┃NL┃ Gladiator"         →    null                     →    "Gladiator [tmdbid-98].strm"
"┃NL┃ Gladiator HEVC"    →    "HEVC"                   →    "Gladiator [tmdbid-98] - HEVC.strm"
"┃NL┃ Gladiator II [4K]" →    "4K"                     →    "Gladiator II [tmdbid-98978] - 4K.strm"
"Movie HEVC 4K BluRay"   →    "HEVC 4K BluRay"         →    "Movie - HEVC 4K BluRay.strm"
```

### Data Flow

```
                    ┌──────────────────────────────────────────────┐
                    │           Xtream Provider API                │
                    │                                              │
                    │  Stream 1: "┃NL┃ Gladiator"                 │
                    │  Stream 2: "┃NL┃ Gladiator HEVC"            │
                    │  Stream 3: "┃NL┃ Gladiator II"              │
                    │  Stream 4: "┃NL┃ Gladiator II [4K]"         │
                    │  Stream 5: "┃NL┃ Gladiator II HEVC"         │
                    └────────────────────┬─────────────────────────┘
                                         │
                                         ▼
                    ┌──────────────────────────────────────────────┐
                    │         StrmSyncService (per stream)         │
                    │                                              │
                    │  1. SanitizeFileName() → clean folder name   │
                    │  2. ExtractVersionLabel() → version suffix   │
                    │  3. BuildMovieFolderName() → folder with ID  │
                    │  4. BuildMovieStrmFileName() → STRM filename │
                    └────────────────────┬─────────────────────────┘
                                         │
                                         ▼
                    ┌──────────────────────────────────────────────┐
                    │              File System Output               │
                    │                                              │
                    │  Gladiator [tmdbid-98]/                      │
                    │    Gladiator [tmdbid-98].strm                │
                    │    Gladiator [tmdbid-98] - HEVC.strm         │
                    │                                              │
                    │  Gladiator II [tmdbid-98978]/                │
                    │    Gladiator II [tmdbid-98978].strm          │
                    │    Gladiator II [tmdbid-98978] - 4K.strm    │
                    │    Gladiator II [tmdbid-98978] - HEVC.strm  │
                    └────────────────────┬─────────────────────────┘
                                         │
                                         ▼
                    ┌──────────────────────────────────────────────┐
                    │           Jellyfin Library Scan               │
                    │                                              │
                    │  "Gladiator" → 1 entry, 2 versions           │
                    │  "Gladiator II" → 1 entry, 3 versions        │
                    │  Version picker shown during playback         │
                    └──────────────────────────────────────────────┘
```

### Methods

#### `ExtractVersionLabel(string? name) → string?`

**Location:** `StrmSyncService.cs` (after `SanitizeFileName`)

Extracts codec, quality, and source tags from the original stream name and returns them as a combined version label. Returns `null` when no tags are present.

**Processing steps:**
1. Strip prefix language tags (same `PrefixLanguageTagPattern` used by `SanitizeFileName`)
2. Match all codec tags via `CodecTagPattern` (HEVC, x264, x265, H.264, AVC, etc.)
3. Match all quality tags via `QualityTagPattern` (4K, UHD, 1080p, 720p, HDR, etc.)
4. Match all source tags via `SourceTagPattern` (BluRay, WEBRip, HDTV, REMUX, etc.)
5. Join all matches with spaces, or return `null` if none found

**Tag categories (reuses existing regexes):**

| Category | Pattern | Examples |
|----------|---------|----------|
| Codec | `CodecTagPattern` | HEVC, x264, x265, H.264, AVC, VP9, AV1, 10bit |
| Quality | `QualityTagPattern` | 4K, UHD, 2160p, 1080p, 720p, 480p, HDR, HDR10, SDR |
| Source | `SourceTagPattern` | BluRay, BRRip, WEBRip, WEB-DL, HDTV, DVDRip, REMUX |

#### `BuildMovieStrmFileName(string folderName, string? versionLabel) → string`

**Location:** `StrmSyncService.cs` (after `ExtractVersionLabel`)

Constructs the STRM filename with an optional version label suffix.

| Input | Output |
|-------|--------|
| `("Gladiator [tmdbid-98]", null)` | `"Gladiator [tmdbid-98].strm"` |
| `("Gladiator [tmdbid-98]", "HEVC")` | `"Gladiator [tmdbid-98] - HEVC.strm"` |
| `("Folder", "HEVC 4K")` | `"Folder - HEVC 4K.strm"` |

The ` - ` separator follows Jellyfin's naming convention for version labels.

### Integration Points

The version label logic is applied in three locations:

| Location | Method | Purpose |
|----------|--------|---------|
| Main sync loop | `SyncMoviesAsync` (~line 1279) | Primary movie processing — extracts `versionLabel` from `stream.Name` |
| Retry path | `RetryMovieAsync` (~line 325) | Retries failed movies — extracts `versionLabel` from `item.Name` |
| Orphan protection | Unchanged movie loop (~line 1171) | Protects all `.strm` files in folder via `Directory.GetFiles("*.strm")` |

### SanitizeFileName Enhancement: Empty Brackets

When `SanitizeFileName` strips tags like `[4K]`, the brackets remain as `[]`. A new `EmptyBracketsPattern` (`\[\s*\]|\(\s*\)`) removes these empty brackets after tag stripping.

**Before:** `"Movie [4K] Title"` → `"Movie [] Title"` → `"Movie  Title"` (double space, trimmed)
**After:** `"Movie [4K] Title"` → `"Movie  Title"` → `"Movie Title"` (clean)

## Scenarios

### Scenario 1: Multiple Quality Variants (Common Case)

**Provider streams:**
- Stream 101: `"┃NL┃ Gladiator"` (TMDB 98)
- Stream 102: `"┃NL┃ Gladiator HEVC"` (TMDB 98)

**Result:**
```
Movies/Gladiator [tmdbid-98]/
  Gladiator [tmdbid-98].strm           → http://provider/movie/user/pass/101.mp4
  Gladiator [tmdbid-98] - HEVC.strm    → http://provider/movie/user/pass/102.mp4
```

**Jellyfin shows:** One "Gladiator" entry with version picker offering default and HEVC.

### Scenario 2: Three Variants (4K + HEVC + Default)

**Provider streams:**
- Stream 201: `"┃NL┃ Gladiator II"`
- Stream 202: `"┃NL┃ Gladiator II [4K]"`
- Stream 203: `"┃NL┃ Gladiator II HEVC"`

**Result:**
```
Movies/Gladiator II [tmdbid-98978]/
  Gladiator II [tmdbid-98978].strm          → .../201.mp4
  Gladiator II [tmdbid-98978] - 4K.strm     → .../202.mp4
  Gladiator II [tmdbid-98978] - HEVC.strm   → .../203.mp4
```

**Jellyfin shows:** One "Gladiator II" entry with three versions.

### Scenario 3: Single Movie with Quality Tag (No Duplicate)

**Provider streams:**
- Stream 301: `"┃NL┃ Rare Movie HEVC"` (only one stream)

**Result:**
```
Movies/Rare Movie [tmdbid-12345]/
  Rare Movie [tmdbid-12345] - HEVC.strm    → .../301.mp4
```

**Jellyfin shows:** One "Rare Movie" entry. A single version is harmless — no version picker is shown, playback is immediate.

### Scenario 4: True Duplicates (Identical Names)

**Provider streams:**
- Stream 401: `"┃NL┃ Some Movie"` (category A)
- Stream 402: `"┃NL┃ Some Movie"` (category B, exact same name)

Both streams have `null` version labels, producing identical STRM paths. Last one wins. This is correct behavior — they're true duplicates, not quality variants.

### Scenario 5: Combined Tags

**Provider streams:**
- Stream 501: `"Movie HEVC 4K HDR BluRay"`

**Version label:** `"HEVC 4K HDR BluRay"`

**Result:**
```
Movies/Movie/
  Movie - HEVC 4K HDR BluRay.strm    → .../501.mp4
```

### Scenario 6: Version Disappears from Provider

A variant that was previously available is removed by the provider.

**Before:** Folder has `Movie.strm` and `Movie - HEVC.strm`
**After sync:** Provider no longer has HEVC stream. Orphan cleanup deletes `Movie - HEVC.strm`.

### Scenario 7: First Sync After Upgrade

Existing library has overwritten STRM files (old behavior where last variant won).

**Behavior:**
1. Existing STRM file contains the "wrong" URL (from last-wins overwrite)
2. Self-healing URL comparison detects mismatch → updates STRM content
3. New version STRM files are created for additional variants
4. Next sync stabilizes with all versions in place

### Scenario 8: Folder Mapping (Category-Based Subfolders)

When folder mappings are configured, version labels work within each target folder.

**Config:** Category 5 → "Action" folder
**Streams:** `"Gladiator"` and `"Gladiator HEVC"` both in category 5

**Result:**
```
Movies/Action/Gladiator [tmdbid-98]/
  Gladiator [tmdbid-98].strm
  Gladiator [tmdbid-98] - HEVC.strm
```

### Scenario 9: Series Episodes (Not Affected)

Version labels are only applied to movies. Series episode STRM filenames continue using the existing `BuildEpisodeFileName` format (`{Show} - S{NN}E{NN} - {Title}.strm`). Series episodes from Xtream providers do not typically have quality variants.

## Orphan Protection

### Previous Behavior

The unchanged movie orphan protection tracked a single STRM path per movie:
```csharp
syncedFiles.TryAdd(Path.Combine(..., $"{existingFolderName}.strm"), 0);
```

This would fail to protect version-suffixed files, causing them to be deleted as orphans on incremental syncs.

### New Behavior

When a movie is unchanged (skipped by incremental sync), ALL `.strm` files in its folder are protected:
```csharp
foreach (var strmFile in Directory.GetFiles(movieFolder, "*.strm"))
{
    syncedFiles.TryAdd(strmFile, 0);
}
```

This is more robust — it protects all versions regardless of which specific streams were in the unchanged list. It handles edge cases like:
- Multiple version STRMs from previous syncs
- Manually added STRM files in the folder
- Version labels that changed between syncs

## Test Coverage

### ExtractVersionLabel Tests (9 tests)

| Test | Input | Expected | Validates |
|------|-------|----------|-----------|
| `ExtractVersionLabel_Null` | `null` | `null` | Null safety |
| `ExtractVersionLabel_NoTags` | `"Gladiator"` | `null` | Clean titles return null |
| `ExtractVersionLabel_HEVC` | `"┃NL┃ Gladiator HEVC"` | `"HEVC"` | Codec extraction with prefix stripping |
| `ExtractVersionLabel_4K` | `"┃NL┃ Movie [4K]"` | `"4K"` | Bracketed quality tag extraction |
| `ExtractVersionLabel_Combined` | `"Movie HEVC 4K HDR BluRay"` | `"HEVC 4K HDR BluRay"` | Multiple tag categories combined |
| `ExtractVersionLabel_x264` | `"Movie x264"` | `"x264"` | Lowercase codec tag |
| `ExtractVersionLabel_1080p` | `"Movie 1080p"` | `"1080p"` | Resolution quality tag |
| `ExtractVersionLabel_BluRay` | `"Movie BluRay"` | `"BluRay"` | Source tag extraction |
| `ExtractVersionLabel_REMUX` | `"Movie REMUX"` | `"REMUX"` | Source tag (common high-quality indicator) |

### BuildMovieStrmFileName Tests (3 tests)

| Test | Input | Expected | Validates |
|------|-------|----------|-----------|
| `BuildMovieStrmFileName_NullLabel` | `("Folder", null)` | `"Folder.strm"` | Default behavior (no version) |
| `BuildMovieStrmFileName_WithLabel` | `("Folder [tmdbid-98]", "HEVC")` | `"Folder [tmdbid-98] - HEVC.strm"` | Version suffix with TMDB ID |
| `BuildMovieStrmFileName_CombinedLabel` | `("Folder", "HEVC 4K")` | `"Folder - HEVC 4K.strm"` | Multi-tag combined label |

### SanitizeFileName Enhancement Test (1 test)

| Test | Input | Expected | Validates |
|------|-------|----------|-----------|
| `SanitizeFileName_EmptyBrackets` | `"Movie [4K] Title"` | `"Movie Title"` | Empty brackets removed after tag stripping |

## Files Changed

| File | Change |
|------|--------|
| `Jellyfin.Xtream.Library/Service/StrmSyncService.cs` | Added `ExtractVersionLabel`, `BuildMovieStrmFileName`, `EmptyBracketsPattern`; updated sync loop, retry path, and orphan protection |
| `Jellyfin.Xtream.Library.Tests/Service/StrmSyncServiceTests.cs` | Added 13 unit tests across 3 new test regions |

## Dispatcharr Multi-Stream Mode

### Overview

When the Xtream provider is a Dispatcharr instance, a second layer of multi-stream detection is available. While name-based version labels (`ExtractVersionLabel`) detect quality variants encoded in stream names, Dispatcharr Multi-Stream Mode discovers **all upstream stream relations** per movie via Dispatcharr's REST API.

### How It Works

1. During sync, for each movie, the plugin queries `GET /api/vod/movies/{id}/` for the movie UUID and `GET /api/vod/movies/{id}/providers/` for all M3U account relations
2. Each relation has a unique `stream_id` pointing to a different upstream file
3. The plugin creates one STRM per provider using proxy URLs: `{BaseUrl}/proxy/vod/movie/{uuid}?stream_id={stream_id}`
4. Jellyfin ffprobes each STRM and shows real media info in the version picker

### Proxy URL Format

```
{BaseUrl}/proxy/vod/movie/{UUID}?stream_id={StreamId}
```

No credentials are embedded in STRM files — Dispatcharr handles upstream authentication.

### Example: Gladiator (Movie ID 11372514)

With 4 upstream streams:

```
Gladiator [tmdbid-98]/
  Gladiator [tmdbid-98].strm                       -> /proxy/vod/movie/{uuid}?stream_id=423017
  Gladiator [tmdbid-98] - Version 2.strm            -> /proxy/vod/movie/{uuid}?stream_id=978284
  Gladiator [tmdbid-98] - Version 3.strm            -> /proxy/vod/movie/{uuid}?stream_id=1153981
  Gladiator [tmdbid-98] - Version 4.strm            -> /proxy/vod/movie/{uuid}?stream_id=1302933
```

Jellyfin shows real media info per version:
- "720p H.264 AAC 5.1 -- 2h35m" (stream 423017)
- "720p H.264 AAC 5.1 -- 1h42m" (stream 978284)
- "1080p H.264 AC3 5.1 -- 2h51m" (stream 1153981)
- "1080p H.264 AC3 5.1 -- 2h51m" (stream 1302933)

### Interaction with Name-Based Labels

When Dispatcharr mode is enabled, movies with multiple providers use numbered version labels (`Version 2`, `Version 3`, etc.) instead of name-based labels. For movies without multiple providers, name-based labels still apply.

### Configuration

Enable in plugin settings under Advanced Settings > Dispatcharr:
- **Dispatcharr Mode**: Toggle on/off
- **API Username**: Django admin username
- **API Password**: Django admin password

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| REST API unreachable | Falls back to single-stream (Xtream URL) |
| JWT auth fails | Logs error, uses standard mode for this sync |
| Movie has 0 providers | Shouldn't happen; falls back to standard URL |
| Movie has 1 provider | Single STRM with proxy URL (no version picker) |
| Duplicate stream_ids | Deduplicated before STRM generation |
| Dispatcharr enabled -> disabled | Next sync creates standard STRMs; old version STRMs become orphans |

## Requirements Traceability

| Requirement | Status |
|-------------|--------|
| FR-2.6: Handle duplicate content across categories | Extended — now distinguishes quality variants from true duplicates |
| FR-3.1: Create Movies folder structure | Extended — multiple STRMs per folder for quality variants |
| FR-3.5: Sanitize filenames to remove invalid characters | Extended — empty bracket cleanup |
| FR-4.1: Delete STRM files for removed provider content | Updated — orphan cleanup handles per-version STRMs |
| NFR-3.4: STRM files compatible with all Jellyfin clients | Maintained — uses native Jellyfin version detection |
