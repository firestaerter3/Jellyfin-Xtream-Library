# Per-Series / Per-Movie Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users include/exclude individual movies and series within a selected VOD/Series category, mirroring the existing Live TV per-channel selection (GitHub issue #54).

**Architecture:** Use the same exclude-list model as Live TV (`ExcludedLiveStreamIds`), but per-provider instead of global. Add `ExcludedVodStreamIds` and `ExcludedSeriesIds` to `ProviderConfig`. Sync skips excluded IDs when enumerating category contents. Two new read-only API endpoints list the items in a category on demand (lazy-loaded when a category is expanded in the config UI). The config UI generalizes the Live TV expand/checkbox panel to VOD and Series. Default (empty exclusion arrays) preserves today's "sync everything in selected categories" behavior, so the change is backward-compatible with no migration needed.

**Tech Stack:** .NET 9 / C# (Jellyfin plugin), Newtonsoft.Json models, xUnit + FluentAssertions + Moq tests, vanilla JS config UI (`config.js` / `config.html`).

**Scope boundary:** Per-item selection UI is wired into the **Single folder mode** category list (`renderCategoryList`), matching where Live TV exposes it. Multiple/folder-mapping mode is out of scope for this plan (the sync-time exclusion filter still applies universally if exclusions exist, but no per-item UI is added to the folder-mapping editor). Note this limitation in the changelog.

---

### Task 1: Add per-provider exclusion config fields

**Goal:** `ProviderConfig` gains `ExcludedVodStreamIds` and `ExcludedSeriesIds`, and changing them invalidates the sync snapshot fingerprint (so deselecting an item forces a resync that removes its STRM).

**Files:**
- Modify: `Jellyfin.Xtream.Library/ProviderConfig.cs` (after line 92, the `SelectedSeriesCategoryIds` property)
- Modify: `Jellyfin.Xtream.Library/Service/SnapshotService.cs:99-109` (`CalculateConfigFingerprint`)
- Test: `Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs`
- Test: `Jellyfin.Xtream.Library.Tests/Service/SnapshotServiceTests.cs`

**Acceptance Criteria:**
- [ ] `new ProviderConfig().ExcludedVodStreamIds` and `.ExcludedSeriesIds` default to empty `int[]`.
- [ ] `CalculateConfigFingerprint` returns a different hash when `ExcludedVodStreamIds` differs.
- [ ] `CalculateConfigFingerprint` returns a different hash when `ExcludedSeriesIds` differs.
- [ ] Existing tests still pass.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~PluginConfigurationTests|FullyQualifiedName~SnapshotServiceTests"` → all pass

**Steps:**

- [ ] **Step 1: Add the two properties to `ProviderConfig.cs`** (immediately after line 92, inside the `// Content` region):

```csharp
    /// <summary>
    /// Gets or sets VOD stream IDs to exclude from sync, even if their category is selected.
    /// Empty array means no per-movie exclusions (sync every movie in the selected categories).
    /// </summary>
    public int[] ExcludedVodStreamIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets series IDs to exclude from sync, even if their category is selected.
    /// Empty array means no per-series exclusions (sync every series in the selected categories).
    /// </summary>
    public int[] ExcludedSeriesIds { get; set; } = Array.Empty<int>();
```

- [ ] **Step 2: Add a failing test for defaults in `PluginConfigurationTests.cs`** (add inside the `PluginConfigurationTests` class):

```csharp
    [Fact]
    public void ProviderConfig_ExclusionArrays_DefaultToEmpty()
    {
        var provider = new ProviderConfig();
        provider.ExcludedVodStreamIds.Should().BeEmpty();
        provider.ExcludedSeriesIds.Should().BeEmpty();
    }
```

- [ ] **Step 3: Extend the fingerprint in `SnapshotService.cs`.** Replace the `string.Join("|", ...)` argument list at lines 99-109 so the two exclusion arrays are appended before `ComputeMd5`:

```csharp
        var data = string.Join(
            "|",
            provider.MovieFolderMode,
            provider.SeriesFolderMode,
            provider.MovieFolderMappings ?? string.Empty,
            provider.SeriesFolderMappings ?? string.Empty,
            string.Join(",", provider.SelectedVodCategoryIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            string.Join(",", provider.SelectedSeriesCategoryIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            string.Join(",", provider.ExcludedVodStreamIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            string.Join(",", provider.ExcludedSeriesIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            enableMetadataLookup.ToString(CultureInfo.InvariantCulture),
            provider.TmdbFolderIdOverrides ?? string.Empty,
            provider.TvdbFolderIdOverrides ?? string.Empty);
```

- [ ] **Step 4: Add fingerprint tests in `SnapshotServiceTests.cs`** (mirror the existing fingerprint test style in that file — read the file first to match the exact helper/assertion conventions, then add):

```csharp
    [Fact]
    public void CalculateConfigFingerprint_DiffersWhenVodExclusionsChange()
    {
        var a = new ProviderConfig { ExcludedVodStreamIds = new[] { 1, 2 } };
        var b = new ProviderConfig { ExcludedVodStreamIds = new[] { 1, 2, 3 } };
        SnapshotService.CalculateConfigFingerprint(a)
            .Should().NotBe(SnapshotService.CalculateConfigFingerprint(b));
    }

    [Fact]
    public void CalculateConfigFingerprint_DiffersWhenSeriesExclusionsChange()
    {
        var a = new ProviderConfig { ExcludedSeriesIds = new[] { 10 } };
        var b = new ProviderConfig { ExcludedSeriesIds = new[] { 11 } };
        SnapshotService.CalculateConfigFingerprint(a)
            .Should().NotBe(SnapshotService.CalculateConfigFingerprint(b));
    }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PluginConfigurationTests|FullyQualifiedName~SnapshotServiceTests"`
Expected: PASS (including the 3 new tests)

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/ProviderConfig.cs Jellyfin.Xtream.Library/Service/SnapshotService.cs Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs Jellyfin.Xtream.Library.Tests/Service/SnapshotServiceTests.cs
git commit -m "feat(config): add per-provider VOD/Series item exclusion fields"
```

---

### Task 2: Sync-time exclusion filtering

**Goal:** When `SyncMoviesAsync`/`SyncSeriesAsync` enumerate the items in each selected category, items whose ID is in the provider's exclusion array are skipped. The logic lives in a testable internal static helper.

**Files:**
- Create: `Jellyfin.Xtream.Library/Service/ContentExclusionFilter.cs`
- Modify: `Jellyfin.Xtream.Library/Service/StrmSyncService.cs:1404-1409` (VOD fetch loop) and `:2148-2153` (Series fetch loop)
- Test: `Jellyfin.Xtream.Library.Tests/Service/ContentExclusionFilterTests.cs`

**Acceptance Criteria:**
- [ ] `ContentExclusionFilter.BuildSet(null)` and `BuildSet(empty)` return an empty set; `BuildSet(new[]{1,2})` returns `{1,2}`.
- [ ] In the VOD loop, a stream whose `StreamId` is excluded is not added to `streamBag`.
- [ ] In the Series loop, a series whose `SeriesId` is excluded is not added to `seriesBag`.
- [ ] Empty exclusion set is a no-op (all items pass through) — verified by test.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~ContentExclusionFilterTests"` → all pass

**Steps:**

- [ ] **Step 1: Write the failing test file `ContentExclusionFilterTests.cs`:**

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ContentExclusionFilterTests
{
    [Fact]
    public void BuildSet_Null_ReturnsEmpty()
    {
        ContentExclusionFilter.BuildSet(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildSet_Empty_ReturnsEmpty()
    {
        ContentExclusionFilter.BuildSet(System.Array.Empty<int>()).Should().BeEmpty();
    }

    [Fact]
    public void BuildSet_WithIds_ReturnsThoseIds()
    {
        var set = ContentExclusionFilter.BuildSet(new[] { 1, 2, 2, 3 });
        set.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void IsExcluded_EmptySet_AlwaysFalse()
    {
        var set = ContentExclusionFilter.BuildSet(System.Array.Empty<int>());
        ContentExclusionFilter.IsExcluded(set, 5).Should().BeFalse();
    }

    [Fact]
    public void IsExcluded_MatchingId_True()
    {
        var set = ContentExclusionFilter.BuildSet(new[] { 5, 6 });
        ContentExclusionFilter.IsExcluded(set, 5).Should().BeTrue();
        ContentExclusionFilter.IsExcluded(set, 7).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ContentExclusionFilterTests"`
Expected: FAIL — `ContentExclusionFilter` does not exist (compile error)

- [ ] **Step 3: Create `ContentExclusionFilter.cs`:**

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Helpers for filtering individual VOD/Series items out of a sync run based on
/// the per-provider exclusion lists (<see cref="ProviderConfig.ExcludedVodStreamIds"/>
/// and <see cref="ProviderConfig.ExcludedSeriesIds"/>).
/// </summary>
internal static class ContentExclusionFilter
{
    /// <summary>
    /// Builds a lookup set from an exclusion ID array. Null or empty input yields an empty set.
    /// </summary>
    /// <param name="excludedIds">The configured exclusion IDs, may be null.</param>
    /// <returns>A hash set of the excluded IDs.</returns>
    public static HashSet<int> BuildSet(int[]? excludedIds)
    {
        if (excludedIds == null || excludedIds.Length == 0)
        {
            return new HashSet<int>();
        }

        return new HashSet<int>(excludedIds);
    }

    /// <summary>
    /// Returns true if the given item ID is present in the exclusion set.
    /// An empty set always returns false (nothing excluded).
    /// </summary>
    /// <param name="excludedSet">The exclusion set from <see cref="BuildSet"/>.</param>
    /// <param name="itemId">The stream or series ID to test.</param>
    /// <returns>True if the item should be excluded.</returns>
    public static bool IsExcluded(HashSet<int> excludedSet, int itemId)
        => excludedSet.Count != 0 && excludedSet.Contains(itemId);
}
```

- [ ] **Step 4: Wire into the VOD fetch loop in `StrmSyncService.cs`.** Just before the `await Parallel...` block that fetches VOD streams (the block containing lines 1404-1409), build the set once. Find the line `var selectedIds = provider.SelectedVodCategoryIds;` (line 1247) region — add after the category filtering completes and before the parallel fetch:

```csharp
            var excludedVodSet = ContentExclusionFilter.BuildSet(provider.ExcludedVodStreamIds);
```

Then change the inner loop at lines 1406-1409 from:

```csharp
                        foreach (var stream in streams)
                        {
                            streamBag.Add((stream, category.CategoryId));
                        }
```

to:

```csharp
                        foreach (var stream in streams)
                        {
                            if (ContentExclusionFilter.IsExcluded(excludedVodSet, stream.StreamId))
                            {
                                continue;
                            }

                            streamBag.Add((stream, category.CategoryId));
                        }
```

(`excludedVodSet` is captured by the lambda; it is read-only here so it is safe to share across the parallel iterations.)

- [ ] **Step 5: Wire into the Series fetch loop in `StrmSyncService.cs`.** Mirror Step 4 for series. Near `var selectedIds = provider.SelectedSeriesCategoryIds;` (line 1985) region / before the series parallel fetch, add:

```csharp
            var excludedSeriesSet = ContentExclusionFilter.BuildSet(provider.ExcludedSeriesIds);
```

Then change lines 2150-2153 from:

```csharp
                        foreach (var series in seriesList)
                        {
                            seriesBag.Add((series, category.CategoryId));
                        }
```

to:

```csharp
                        foreach (var series in seriesList)
                        {
                            if (ContentExclusionFilter.IsExcluded(excludedSeriesSet, series.SeriesId))
                            {
                                continue;
                            }

                            seriesBag.Add((series, category.CategoryId));
                        }
```

- [ ] **Step 6: Run tests + full build**

Run: `dotnet build -c Release` then `dotnet test -c Release --filter "FullyQualifiedName~ContentExclusionFilterTests|FullyQualifiedName~StrmSyncServiceTests"`
Expected: build succeeds (TreatWarningsAsErrors clean), tests PASS

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/ContentExclusionFilter.cs Jellyfin.Xtream.Library/Service/StrmSyncService.cs Jellyfin.Xtream.Library.Tests/Service/ContentExclusionFilterTests.cs
git commit -m "feat(sync): skip per-item excluded VOD streams and series during sync"
```

---

### Task 3: API endpoints to list items in a category

**Goal:** Two read-only endpoints return the items in a VOD/Series category so the config UI can lazy-load them: `GET /XtreamLibrary/Streams/Vod?categoryId=&providerIndex=` and `GET /XtreamLibrary/Series/List?categoryId=&providerIndex=`.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Api/SyncController.cs` (add two endpoints after `GetSeriesCategories`, ~line 447; add two DTO classes near the bottom of the file alongside `CategoryDto`)
- Modify: `CLAUDE.md` (API Endpoints table — add the two new rows)
- Test: `Jellyfin.Xtream.Library.Tests/Api/SyncControllerTests.cs`

**Acceptance Criteria:**
- [ ] `GET Streams/Vod` with an unconfigured provider returns `BadRequest`.
- [ ] `GET Series/List` with an unconfigured provider returns `BadRequest`.
- [ ] With a configured provider + mocked `IXtreamClient`, `GetVodStreams` returns the mapped `ContentItemDto` list ordered by `Num` then `Name`.
- [ ] Build is warning-clean (XML docs required on public members — TreatWarningsAsErrors).

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~SyncControllerTests"` → all pass

**Steps:**

- [ ] **Step 1: Add the two DTO classes** near the existing `CategoryDto` class in `SyncController.cs` (find `public class CategoryDto` first, then add after it):

```csharp
/// <summary>
/// Data transfer object for a single VOD (movie) item within a category.
/// </summary>
public class ContentItemDto
{
    /// <summary>
    /// Gets or sets the Xtream stream ID of the movie.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the provider-assigned ordinal number.
    /// </summary>
    public int Num { get; set; }

    /// <summary>
    /// Gets or sets the movie title.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Data transfer object for a single series item within a category.
/// </summary>
public class SeriesItemDto
{
    /// <summary>
    /// Gets or sets the Xtream series ID.
    /// </summary>
    public int SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the provider-assigned ordinal number.
    /// </summary>
    public int Num { get; set; }

    /// <summary>
    /// Gets or sets the series title.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Add the two endpoints** in `SyncController` right after `GetSeriesCategories` (after line 447). They mirror `GetVodCategories` exactly for provider resolution:

```csharp
    /// <summary>
    /// Gets all VOD (movie) items within a category.
    /// </summary>
    /// <param name="categoryId">The VOD category ID.</param>
    /// <param name="providerIndex">Zero-based provider index (default: 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of movies in the category.</returns>
    [HttpGet("Streams/Vod")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<ContentItemDto>>> GetVodStreams(
        [FromQuery] int categoryId,
        [FromQuery] int providerIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest("Plugin not initialized.");
        }

        var provider = config.Providers.ElementAtOrDefault(providerIndex);
        if (provider == null || string.IsNullOrEmpty(provider.BaseUrl) || string.IsNullOrEmpty(provider.Username))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = new Client.ConnectionInfo(provider.BaseUrl, provider.Username, provider.Password ?? string.Empty);
            var streams = await _client.GetVodStreamsByCategoryAsync(connectionInfo, categoryId, cancellationToken).ConfigureAwait(false);

            var result = streams.Select(s => new ContentItemDto
            {
                StreamId = s.StreamId,
                Num = s.Num,
                Name = s.Name,
            }).OrderBy(s => s.Num).ThenBy(s => s.Name);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch VOD streams for category {CategoryId}", categoryId);
            return BadRequest($"Failed to fetch movies: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all series within a category.
    /// </summary>
    /// <param name="categoryId">The series category ID.</param>
    /// <param name="providerIndex">Zero-based provider index (default: 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of series in the category.</returns>
    [HttpGet("Series/List")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<SeriesItemDto>>> GetSeriesList(
        [FromQuery] int categoryId,
        [FromQuery] int providerIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest("Plugin not initialized.");
        }

        var provider = config.Providers.ElementAtOrDefault(providerIndex);
        if (provider == null || string.IsNullOrEmpty(provider.BaseUrl) || string.IsNullOrEmpty(provider.Username))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = new Client.ConnectionInfo(provider.BaseUrl, provider.Username, provider.Password ?? string.Empty);
            var seriesList = await _client.GetSeriesByCategoryAsync(connectionInfo, categoryId, cancellationToken).ConfigureAwait(false);

            var result = seriesList.Select(s => new SeriesItemDto
            {
                SeriesId = s.SeriesId,
                Num = s.Num,
                Name = s.Name,
            }).OrderBy(s => s.Num).ThenBy(s => s.Name);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch series for category {CategoryId}", categoryId);
            return BadRequest($"Failed to fetch series: {ex.Message}");
        }
    }
```

- [ ] **Step 3: Add controller tests** to `SyncControllerTests.cs`. Read the existing file first to confirm whether `Plugin.Instance` is initialized in these tests (the file notes setting up Plugin.Instance is "complex"); follow the `XtreamTunerHostTests` initialization pattern (it creates a `Plugin` to set `Plugin.Instance` and adds a provider via `TestDataBuilder.CreateProviderConfig()`). Add at minimum the unconfigured-provider path which needs no provider:

```csharp
    [Fact]
    public async Task GetVodStreams_NoProviderConfigured_ReturnsBadRequest()
    {
        // With no providers configured, the controller must reject the request.
        var result = await _controller.GetVodStreams(categoryId: 1);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetSeriesList_NoProviderConfigured_ReturnsBadRequest()
    {
        var result = await _controller.GetSeriesList(categoryId: 1);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
```

If `TryGetConfig()` returns null without `Plugin.Instance`, the test still lands on a `BadRequestObjectResult` ("Plugin not initialized."), which satisfies the assertion. If the suite already initializes `Plugin.Instance` (check the constructor and any `[Collection]` attribute), additionally add a happy-path test that configures a provider via `Plugin.Instance.Configuration.Providers` and sets up `_mockClient.Setup(c => c.GetVodStreamsByCategoryAsync(...))` to return two `StreamInfo` items, asserting the mapped/ordered `ContentItemDto` result.

- [ ] **Step 4: Update the API Endpoints table in `CLAUDE.md`** — add two rows under the existing `Channels/Live` row:

```
| `/XtreamLibrary/Streams/Vod` | GET | Fetch movies in a VOD category (`?categoryId=&providerIndex=`) |
| `/XtreamLibrary/Series/List` | GET | Fetch series in a Series category (`?categoryId=&providerIndex=`) |
```

- [ ] **Step 5: Build + test**

Run: `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~SyncControllerTests"`
Expected: build warning-clean, tests PASS

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/Api/SyncController.cs Jellyfin.Xtream.Library.Tests/Api/SyncControllerTests.cs CLAUDE.md
git commit -m "feat(api): add endpoints to list VOD/Series items within a category"
```

---

### Task 4: Config UI — per-provider exclusion state (load/save)

**Goal:** The config UI tracks `excludedVodStreamIds` and `excludedSeriesIds` per active provider, loading them when a provider is selected and writing them back on save. No rendering yet — this task is pure state plumbing so it can be verified independently.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.js` — `makeDefaultProvider` (~line 35-50), top-level state init (~line 17-21), `loadProviderIntoUI` (~line 108-109), `updateActiveProviderFromUI` (~line 182-195)

**Acceptance Criteria:**
- [ ] `makeDefaultProvider` includes `ExcludedVodStreamIds: []` and `ExcludedSeriesIds: []`.
- [ ] Switching providers loads that provider's `ExcludedVodStreamIds`/`ExcludedSeriesIds` into `self.excludedVodStreamIds`/`self.excludedSeriesIds`.
- [ ] Saving writes `self.excludedVodStreamIds`/`self.excludedSeriesIds` back to `providers[activeProviderIndex]`.
- [ ] Loading a provider with no exclusion fields (old config) defaults both to `[]` (no JS error).

**Verify:** Manual — load `config.html` in the Jellyfin dashboard, switch providers, save, reload, confirm via `ApiClient.getPluginConfiguration` in devtools that the arrays round-trip. (JS has no unit harness in this repo; verification is behavioral.)

**Steps:**

- [ ] **Step 1: Add state fields** next to the existing Live TV state (after line 21, the `expandedLiveCategories: {},` line):

```javascript
    excludedVodStreamIds: [],
    excludedSeriesIds: [],
    contentItemsByCategory: { vod: {}, series: {} },
    expandedContentCategories: { vod: {}, series: {} },
```

- [ ] **Step 2: Add defaults to `makeDefaultProvider`** — inside the returned object (alongside `SelectedVodCategoryIds: [], SelectedSeriesCategoryIds: [],` at lines 46-47):

```javascript
            ExcludedVodStreamIds: [],
            ExcludedSeriesIds: [],
```

- [ ] **Step 3: Load in `loadProviderIntoUI`** — after lines 108-109 (`self.selectedSeriesCategoryIds = ...`):

```javascript
        self.excludedVodStreamIds = p.ExcludedVodStreamIds || [];
        self.excludedSeriesIds = p.ExcludedSeriesIds || [];
        self.contentItemsByCategory = { vod: {}, series: {} };
        self.expandedContentCategories = { vod: {}, series: {} };
```

- [ ] **Step 4: Save in `updateActiveProviderFromUI`** — near where `p.SelectedVodCategoryIds`/`p.SelectedSeriesCategoryIds` are written (lines 182-195), add (place outside the folder-mode `if/else`, so exclusions persist regardless of folder mode):

```javascript
        p.ExcludedVodStreamIds = this.excludedVodStreamIds.slice();
        p.ExcludedSeriesIds = this.excludedSeriesIds.slice();
```

- [ ] **Step 5: Manual verify**

Build/publish the plugin (see Task 6 build step), deploy to the test Jellyfin, open the plugin config, open devtools console, run:
```js
ApiClient.getPluginConfiguration('63ba5fcd-c8ce-421a-83e8-ba0b11030d53').then(c => console.log(c.Providers[0].ExcludedVodStreamIds, c.Providers[0].ExcludedSeriesIds));
```
Expected: two arrays logged (empty on a fresh config), no console errors on provider switch/save.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/Configuration/Web/config.js
git commit -m "feat(ui): track per-provider VOD/Series item exclusions in config state"
```

---

### Task 5: Config UI — expand category + per-item checkboxes

**Goal:** In Single folder mode, each VOD/Series category row gets an expand toggle (▸/▾) that lazy-loads its items into a checkbox panel, mirroring Live TV. Unchecking an item adds it to the exclusion list; the category checkbox gates visibility exactly like Live TV.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.js` — generalize `renderCategoryList` (lines 1030-1144) so the `vod`/`series` branches render an expand button + panel; add `toggleContentCategory`, `fetchContentItemsForCategory`, `renderContentItems`, `updateContentExclusion` (model the four on `toggleLiveCategory`/`fetchLiveChannelsForCategory`/`renderLiveChannels`/`updateLiveExclusion` at lines 1146-1301)
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.html` — confirm `.live-channel-list` styling is reusable; if the styles are scoped to `.live-*` classes, add equivalent `.content-item-list` rules (or reuse the existing generic `.checkboxContainer`)

**Acceptance Criteria:**
- [ ] VOD and Series category rows (Single mode) show a ▸ expand button.
- [ ] Expanding a category calls `Streams/Vod`/`Series/List`, renders one checkbox per item, lazy-loaded once and cached in `contentItemsByCategory`.
- [ ] An item is checked when its category is selected AND its ID is not in the exclusion list (mirror `categorySelected && !excluded[id]`).
- [ ] Unchecking an item adds its ID to `excludedVodStreamIds`/`excludedSeriesIds`; rechecking removes it.
- [ ] "Select all"/"Deselect all" per category work and auto-tick the parent category when needed (mirror Live TV).
- [ ] Deselecting the category greys items as not-syncing (mirror Live TV's `categorySelected` gate).

**Verify:** Manual on the test Jellyfin — expand a VOD category, untick two movies, save, run a sync, confirm those two STRMs are not created (and removed if previously present); same for a series.

**Steps:**

- [ ] **Step 1: Generalize the `renderCategoryList` item branch.** In `renderCategoryList` (lines 1030-1082), the current `else` branch (lines 1070-1078) renders a plain checkbox for `vod`/`series`. Replace it so `vod`/`series` render the same expandable structure as `live`, parameterized by `type`. Use a data attribute `data-content-type` and panel class `content-item-list`:

```javascript
            } else {
                // vod / series: expandable row with a per-item panel (issue #54)
                const isExpanded = !!(self.expandedContentCategories[type] && self.expandedContentCategories[type][category.CategoryId]);
                html += '<div class="content-cat-row" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '">';
                html += '<div style="display: flex; align-items: center;">';
                html += '<button type="button" class="content-cat-expand" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '" ';
                html += 'aria-label="Toggle items" ';
                html += 'style="margin-right: 8px; background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; font-size: 0.85em; padding: 2px 8px; min-width: 24px; border-radius: 3px;">';
                html += isExpanded ? '▾' : '▸';
                html += '</button>';
                html += '<label class="emby-checkbox-label" style="flex: 1;">';
                html += '<input is="emby-checkbox" type="checkbox" id="' + checkboxId + '" ';
                html += 'data-category-id="' + category.CategoryId + '" data-category-type="' + type + '" ';
                html += 'data-index="' + index + '" ' + isChecked + '/>';
                html += '<span>' + self.escapeHtml(category.CategoryName) + ' <small style="opacity:0.5;">(ID: ' + category.CategoryId + ')</small></span>';
                html += '</label>';
                html += '</div>';
                html += '<div class="content-item-list" data-cat-id="' + category.CategoryId + '" data-content-type="' + type + '" ';
                html += 'style="margin-left: 36px; margin-top: 4px; ' + (isExpanded ? '' : 'display: none;') + '"></div>';
                html += '</div>';
            }
```

- [ ] **Step 2: Wire expand handlers + change handlers for vod/series** in `renderCategoryList`. After the existing `if (type === 'live') { ... }` block (ends ~line 1143), add a sibling block:

```javascript
        if (type === 'vod' || type === 'series') {
            container.querySelectorAll('.content-cat-expand').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const catId = parseInt(btn.getAttribute('data-cat-id'));
                    self.toggleContentCategory(type, catId, btn);
                });
            });

            // When a category checkbox toggles, re-render any expanded panel so item
            // checkboxes follow the category's selected state.
            checkboxes.forEach(function (cb) {
                cb.addEventListener('change', function () {
                    const catId = parseInt(cb.getAttribute('data-category-id'));
                    if (self.expandedContentCategories[type][catId] && self.contentItemsByCategory[type][catId]) {
                        self.renderContentItems(type, catId);
                    }
                });
            });

            // Re-render panels that were expanded before this list was rebuilt.
            Object.keys(self.expandedContentCategories[type]).forEach(function (catIdStr) {
                if (!self.expandedContentCategories[type][catIdStr]) return;
                const catId = parseInt(catIdStr);
                if (self.contentItemsByCategory[type][catId]) {
                    self.renderContentItems(type, catId);
                } else {
                    self.fetchContentItemsForCategory(type, catId);
                }
            });
        }
```

- [ ] **Step 3: Add the four helper functions** (place them right after `updateLiveExclusion`, ~line 1301). These mirror the Live TV equivalents but key on `type` and use the new endpoints. The endpoint and ID field differ by type: `vod` → `Streams/Vod`, item id field `StreamId`; `series` → `Series/List`, item id field `SeriesId`.

```javascript
    toggleContentCategory: function (type, categoryId, btn) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const isExpanded = !!self.expandedContentCategories[type][categoryId];
        if (isExpanded) {
            self.expandedContentCategories[type][categoryId] = false;
            panel.style.display = 'none';
            if (btn) btn.textContent = '▸';
            return;
        }

        self.expandedContentCategories[type][categoryId] = true;
        panel.style.display = '';
        if (btn) btn.textContent = '▾';

        if (self.contentItemsByCategory[type][categoryId]) {
            self.renderContentItems(type, categoryId);
        } else {
            self.fetchContentItemsForCategory(type, categoryId);
        }
    },

    fetchContentItemsForCategory: function (type, categoryId) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const endpoint = type === 'vod' ? 'XtreamLibrary/Streams/Vod' : 'XtreamLibrary/Series/List';
        const label = type === 'vod' ? 'movies' : 'series';
        panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">Loading ' + label + '...</div>';

        fetch(ApiClient.getUrl(endpoint) + '?categoryId=' + encodeURIComponent(categoryId) + '&providerIndex=' + self.activeProviderIndex, {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (items) {
                self.contentItemsByCategory[type][categoryId] = items || [];
                self.renderContentItems(type, categoryId);
            })
            .catch(function (error) {
                console.error('Failed to load ' + label + ' for category ' + categoryId + ':', error);
                panel.innerHTML = '<div style="color: #d33; padding: 4px 0;">Failed to load ' + label + '. ' +
                    '<button type="button" class="content-item-retry" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ' +
                    'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; margin-left: 8px; border-radius: 3px;">Retry</button></div>';
                const retry = panel.querySelector('.content-item-retry');
                if (retry) {
                    retry.addEventListener('click', function () {
                        self.fetchContentItemsForCategory(type, categoryId);
                    });
                }
            });
    },

    renderContentItems: function (type, categoryId) {
        const self = this;
        const panel = document.querySelector('.content-item-list[data-content-type="' + type + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        const items = self.contentItemsByCategory[type][categoryId] || [];
        const label = type === 'vod' ? 'movies' : 'series';
        if (items.length === 0) {
            panel.innerHTML = '<div class="fieldDescription" style="padding: 4px 0;">No ' + label + ' in this category.</div>';
            return;
        }

        const idField = type === 'vod' ? 'StreamId' : 'SeriesId';
        const exclusionList = type === 'vod' ? self.excludedVodStreamIds : self.excludedSeriesIds;
        const excluded = {};
        exclusionList.forEach(function (id) { excluded[id] = true; });

        const categoryCb = document.querySelector('input[data-category-type="' + type + '"][data-category-id="' + categoryId + '"]');
        const categorySelected = categoryCb ? categoryCb.checked : false;

        let html = '';
        html += '<div style="margin: 4px 0;">';
        html += '<button type="button" class="content-item-select-all" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px; margin-right: 6px;">Select all</button>';
        html += '<button type="button" class="content-item-deselect-all" data-cat-id="' + categoryId + '" data-content-type="' + type + '" ';
        html += 'style="background: none; border: 1px solid rgba(255,255,255,0.2); color: inherit; cursor: pointer; padding: 2px 8px; border-radius: 3px;">Deselect all</button>';
        html += '<small style="opacity: 0.6; margin-left: 10px;">' + items.length + ' ' + label + '</small>';
        if (!categorySelected) {
            html += '<small style="opacity: 0.6; margin-left: 10px; font-style: italic;">Category is deselected — tick an item or the category to include it.</small>';
        }
        html += '</div>';

        items.forEach(function (item) {
            const itemId = item[idField];
            const isChecked = categorySelected && !excluded[itemId] ? 'checked' : '';
            html += '<div class="checkboxContainer" style="margin: 2px 0;">';
            html += '<label class="emby-checkbox-label">';
            html += '<input is="emby-checkbox" type="checkbox" class="content-item-cb" ';
            html += 'data-item-id="' + itemId + '" ' + isChecked + '/>';
            html += '<span>' + self.escapeHtml(item.Name || '(unnamed)');
            if (item.Num) {
                html += ' <small style="opacity:0.5;">#' + item.Num + '</small>';
            }
            html += '</span></label></div>';
        });

        panel.innerHTML = html;

        panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
            cb.addEventListener('change', function () {
                const itemId = parseInt(cb.getAttribute('data-item-id'));
                if (cb.checked && categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                }
                self.updateContentExclusion(type, itemId, !cb.checked);
            });
        });

        const selectAll = panel.querySelector('.content-item-select-all');
        if (selectAll) {
            selectAll.addEventListener('click', function () {
                if (categoryCb && !categoryCb.checked) {
                    categoryCb.checked = true;
                }
                panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
                    if (!cb.checked) {
                        cb.checked = true;
                        self.updateContentExclusion(type, parseInt(cb.getAttribute('data-item-id')), false);
                    }
                });
            });
        }
        const deselectAll = panel.querySelector('.content-item-deselect-all');
        if (deselectAll) {
            deselectAll.addEventListener('click', function () {
                panel.querySelectorAll('.content-item-cb').forEach(function (cb) {
                    if (cb.checked) {
                        cb.checked = false;
                        self.updateContentExclusion(type, parseInt(cb.getAttribute('data-item-id')), true);
                    }
                });
            });
        }
    },

    updateContentExclusion: function (type, itemId, shouldBeExcluded) {
        const self = this;
        const list = type === 'vod' ? self.excludedVodStreamIds : self.excludedSeriesIds;
        const idx = list.indexOf(itemId);
        if (shouldBeExcluded && idx === -1) {
            list.push(itemId);
        } else if (!shouldBeExcluded && idx !== -1) {
            list.splice(idx, 1);
        }
    },
```

- [ ] **Step 4: Verify CSS.** Check `config.html` for `.live-channel-list` / `.live-cat-row` rules. The new code uses inline styles for layout plus the generic `.checkboxContainer` and `.emby-checkbox-label` (already styled). No new CSS is strictly required. If the Live TV rows rely on a `.category-list` parent rule that conflicts, add matching rules for `.content-cat-row`/`.content-item-list`. Confirm by visual inspection in the dashboard.

- [ ] **Step 5: Manual verify** (the AC's behavioral check)

Build/publish, deploy, open config → Movies tab → Load Categories → expand a category → untick 2 movies → Save → run sync → confirm those 2 movies produce no STRM (and are removed if they existed). Repeat on Series tab.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/Configuration/Web/config.js Jellyfin.Xtream.Library/Configuration/Web/config.html
git commit -m "feat(ui): expand VOD/Series categories to pick individual items (#54)"
```

---

### Task 6: Version bump, docs, changelog, and full verification

**Goal:** Bump the plugin version, document the feature, and verify the whole build + test suite is green before any release.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj` (lines 6-7, version `1.36.7.0` → `1.37.0.0`)
- Modify: `README.md` and `docs/ARCHITECTURE.md` (document per-item selection)
- Modify: `docs/REQUIREMENTS.md` if it enumerates content-selection capabilities

**Acceptance Criteria:**
- [ ] `dotnet build -c Release` succeeds warning-clean.
- [ ] `dotnet test -c Release` — full suite passes (baseline 64+ tests plus the new ones).
- [ ] Version is `1.37.0.0` in both `AssemblyVersion` and `FileVersion`.
- [ ] README documents that individual movies/series can be excluded within a category (Single folder mode), matching Live TV per-channel selection.

**Verify:** `dotnet build -c Release && dotnet test -c Release` → build clean, all tests pass

**Steps:**

- [ ] **Step 1: Bump version** in `Jellyfin.Xtream.Library.csproj`:

```xml
    <AssemblyVersion>1.37.0.0</AssemblyVersion>
    <FileVersion>1.37.0.0</FileVersion>
```

(Minor bump, not patch — this is a new user-facing feature.)

- [ ] **Step 2: Document in README.md** — add a short subsection under the content/library configuration section explaining: in Single folder mode, expand a Movie or Series category to tick/untick individual titles; unticked titles are excluded from sync (and removed on the next sync if previously present). Note the parity with Live TV per-channel selection and the Single-mode limitation. Keep it user-facing and plain (no AI tells).

- [ ] **Step 3: Document in docs/ARCHITECTURE.md** — note the new `ExcludedVodStreamIds`/`ExcludedSeriesIds` per-provider fields, the `ContentExclusionFilter` sync-time gate, and the `Streams/Vod` + `Series/List` endpoints used by the config UI for lazy item loading.

- [ ] **Step 4: Full build + test**

Run: `dotnet build -c Release && dotnet test -c Release`
Expected: build warning-clean; all tests pass

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj README.md docs/ARCHITECTURE.md docs/REQUIREMENTS.md
git commit -m "Release v1.37.0.0: per-series/per-movie selection within categories (#54)"
```

- [ ] **Step 6: Release (user-gated — do NOT run autonomously).** The release to GitHub + the beta manifest is outward-facing and the user controls timing. After the user approves, follow `CLAUDE.md` → Release Process: tag `v1.37.0.0`, `dotnet publish`, zip the DLL, `gh release create`, then add the beta-channel entry to `../jellyfin-plugin-repo/manifest-dev.json`. Use `GH_INSECURE_NO_TLS_VERIFY=1` and `dangerouslyDisableSandbox: true` per `CLAUDE.local.md`. After release, draft (do not auto-post) a reply on issue #54 for the user's approval.

---

## Notes for the implementer

- **Why exclude-list, not include-list:** VOD categories can hold thousands of titles; an empty exclusion array must mean "sync everything" so existing users are unaffected and large libraries stay manageable. This matches Live TV's `ExcludedLiveStreamIds`.
- **Orphan cleanup interaction:** excluded items are simply never enumerated, so the orphan cleanup pass (which compares filesystem to the synced set) will remove their STRMs on the next sync — this is the desired "untick to remove" behavior. The fingerprint change in Task 1 ensures the resync actually runs.
- **Per-provider, not global:** unlike Live TV (single provider, global arrays on `PluginConfiguration`), VOD/Series selection is per-provider on `ProviderConfig`. All UI state must load/save through `loadProviderIntoUI`/`updateActiveProviderFromUI`, never the global `loadConfig`/`saveConfig`.
- **Single folder mode only:** the per-item UI lives in `renderCategoryList` (Single mode). In Multiple/folder-mapping mode the per-item panel is not shown; the sync-time filter still honors any exclusions already saved.
