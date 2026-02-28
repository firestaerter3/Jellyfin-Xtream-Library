# Year-Free Metadata Lookup Fallback Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an opt-in `FallbackToYearlessLookup` config option that retries TMDb/TVDb metadata lookup without the year when the year-qualified search returns no result.

**Architecture:** The fallback is added inline in `LookupMovieTmdbIdAsync` and `LookupSeriesTvdbIdAsync`. After the primary search returns null, if the feature is enabled and a year was present, a second provider search is made with year=null. Each result (including null) is cached under its own key (`movie:title` vs `movie:title:year`) to prevent repeated API calls.

**Tech Stack:** C# / .NET 9, Jellyfin plugin SDK (IProviderManager), xUnit + Moq + FluentAssertions for tests.

---

### Task 1: Add `FallbackToYearlessLookup` config property

**Files:**
- Modify: `Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs`
- Modify: `Jellyfin.Xtream.Library/PluginConfiguration.cs`

**Step 1: Write the failing test**

Add at the end of `PluginConfigurationTests.cs`, inside the class body:

```csharp
[Fact]
public void FallbackToYearlessLookup_DefaultIsFalse()
{
    var config = new PluginConfiguration();
    config.FallbackToYearlessLookup.Should().BeFalse();
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release --filter "FallbackToYearlessLookup_DefaultIsFalse" -v normal
```

Expected: FAIL — `'PluginConfiguration' does not contain a definition for 'FallbackToYearlessLookup'`

**Step 3: Add property to `PluginConfiguration.cs`**

Insert this block directly before the `// =====================` line for `Dispatcharr Mode Settings` (around line 215):

```csharp
/// <summary>
/// Gets or sets a value indicating whether to retry metadata lookup without the year
/// if the year-qualified lookup returns no result.
/// Useful when the provider has incorrect years in stream names.
/// Note: year-based false-positive protection is weaker for the fallback result.
/// </summary>
public bool FallbackToYearlessLookup { get; set; } = false;
```

**Step 4: Run test to verify it passes**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release --filter "FallbackToYearlessLookup_DefaultIsFalse" -v normal
```

Expected: PASS

**Step 5: Commit**

```bash
git add Jellyfin.Xtream.Library/PluginConfiguration.cs Jellyfin.Xtream.Library.Tests/PluginConfigurationTests.cs
git commit -m "feat: add FallbackToYearlessLookup config property (default false)"
```

---

### Task 2: Fallback in `LookupMovieTmdbIdAsync`

**Files:**
- Modify: `Jellyfin.Xtream.Library.Tests/Service/MetadataLookupServiceTests.cs` (add tests at end of file)
- Modify: `Jellyfin.Xtream.Library/Service/MetadataLookupService.cs`

**Background on test setup**

The async methods read `Plugin.Instance.Configuration`, so tests need a live `Plugin` instance. Use the same pattern as `XtreamTunerHostTests`: mock `IApplicationPaths` and `IXmlSerializer`, pass a `PluginConfiguration` via the serializer. `IProviderManager` can be mocked with Moq.

`MetadataCache` is created with `NullLogger` and an empty path — the cache file won't exist so `InitializeAsync` starts with an empty in-memory cache (correct for testing).

**Step 1: Add required using directives at the top of `MetadataLookupServiceTests.cs`** (add any missing ones):

```csharp
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
```

**Step 2: Add a private helper method inside `MetadataLookupServiceTests`**

Add this private helper at the top of the class to set up Plugin.Instance with a given configuration:

```csharp
private static void InitPlugin(PluginConfiguration config)
{
    var tempPath = Path.Combine(Path.GetTempPath(), "claude", "test-metadata-config");
    var appPaths = new Mock<IApplicationPaths>();
    appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempPath);
    appPaths.Setup(p => p.DataPath).Returns(tempPath);
    appPaths.Setup(p => p.ProgramDataPath).Returns(tempPath);
    appPaths.Setup(p => p.CachePath).Returns(tempPath);
    appPaths.Setup(p => p.LogDirectoryPath).Returns(tempPath);
    appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tempPath);
    appPaths.Setup(p => p.TempDirectory).Returns(tempPath);
    appPaths.Setup(p => p.PluginsPath).Returns(tempPath);
    appPaths.Setup(p => p.WebPath).Returns(tempPath);
    appPaths.Setup(p => p.ProgramSystemPath).Returns(tempPath);
    var xmlSerializer = new Mock<IXmlSerializer>();
    xmlSerializer
        .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
        .Returns(config);
    _ = new Plugin(appPaths.Object, xmlSerializer.Object);
}
```

**Step 3: Write four failing tests for movie fallback**

Add these tests at the end of `MetadataLookupServiceTests.cs`, inside the class:

```csharp
// === FallbackToYearlessLookup: movie ===

[Fact]
public async Task LookupMovieTmdbIdAsync_FallbackEnabled_RetriesWithoutYear_WhenYearQualifiedFails()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var fallbackResult = new RemoteSearchResult
    {
        Name = "The Notebook",
        ProductionYear = 2004,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "11036" },
    };

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>())   // primary (year=2009): no match
        .ReturnsAsync(new[] { fallbackResult });            // fallback (year=null): match

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupMovieTmdbIdAsync("The Notebook", 2009, CancellationToken.None);

    result.Should().Be(11036);
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2));
}

[Fact]
public async Task LookupMovieTmdbIdAsync_FallbackDisabled_DoesNotRetry()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = false,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupMovieTmdbIdAsync("The Notebook", 2009, CancellationToken.None);

    result.Should().BeNull();
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
        Times.Once());
}

[Fact]
public async Task LookupMovieTmdbIdAsync_FallbackEnabled_NoDoubleCallWhenYearAlreadyNull()
{
    // When the stream has no year, the initial lookup is already year-free; no fallback needed.
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupMovieTmdbIdAsync("The Notebook", null, CancellationToken.None);

    result.Should().BeNull();
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
        Times.Once());
}

[Fact]
public async Task LookupMovieTmdbIdAsync_FallbackEnabled_CachesFallbackNull_AvoidingRepeatCalls()
{
    // When fallback also returns nothing, the null result is cached under the year-free key.
    // A second call with the same year-free key should hit cache, not call the provider again.
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    // First call: primary (year) + fallback (no year) = 2 provider calls
    await svc.LookupMovieTmdbIdAsync("Unknown Movie", 2020, CancellationToken.None);
    // Second call with same title, no year: should hit fallback cache, 0 new provider calls
    await svc.LookupMovieTmdbIdAsync("Unknown Movie", null, CancellationToken.None);

    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
            It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2)); // primary + fallback on first call; cache hit on second
}
```

**Step 4: Run tests to verify they fail**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release --filter "LookupMovieTmdbIdAsync" -v normal
```

Expected: all 4 FAIL — fallback logic doesn't exist yet.

**Step 5: Implement fallback in `LookupMovieTmdbIdAsync`**

In `MetadataLookupService.cs`, find this block (around line 145):

```csharp
            // Cache the result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TmdbId = tmdbId,
                Confidence = firstResult != null && tmdbId.HasValue ? 100 : 0,
            });

            return tmdbId;
```

Replace with:

```csharp
            // Cache the primary result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TmdbId = tmdbId,
                Confidence = firstResult != null && tmdbId.HasValue ? 100 : 0,
            });

            // Fallback: retry without year if primary failed and feature is enabled.
            // Only applicable when a year was present (otherwise lookup is already year-free).
            if (tmdbId == null && year.HasValue && config.FallbackToYearlessLookup)
            {
                _logger.LogInformation(
                    "Retrying TMDb lookup without year for: '{Title}' (extracted year={Year})",
                    title,
                    year);

                var fallbackKey = MetadataCache.GetMovieKey(title, null);
                if (_cache.TryGet(fallbackKey, out var fallbackCached, config.MetadataCacheAgeDays))
                {
                    tmdbId = fallbackCached?.TmdbId;
                    _logger.LogDebug("Fallback cache hit for movie: {Title} -> TMDb {Id}", title, tmdbId);
                }
                else
                {
                    var fallbackInfo = new MovieInfo { Name = title, Year = null };
                    var fallbackResults = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                        new RemoteSearchQuery<MovieInfo> { SearchInfo = fallbackInfo },
                        cancellationToken).ConfigureAwait(false);

                    var fallbackFirst = fallbackResults.FirstOrDefault();
                    if (fallbackFirst?.ProviderIds != null &&
                        fallbackFirst.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var fbStr) &&
                        int.TryParse(fbStr, out var fbId) &&
                        !IsLikelyFalsePositive(title, fallbackFirst.Name, null, fallbackFirst.ProductionYear))
                    {
                        tmdbId = fbId;
                        _logger.LogDebug("Fallback found TMDb ID for movie: {Title} -> {Id}", title, tmdbId);
                    }
                    else
                    {
                        _logger.LogDebug("Fallback found no TMDb ID for movie: {Title}", title);
                    }

                    _cache.Set(fallbackKey, new MetadataCacheEntry
                    {
                        TmdbId = tmdbId,
                        Confidence = tmdbId.HasValue ? 100 : 0,
                    });
                }
            }

            return tmdbId;
```

**Step 6: Run tests to verify they pass**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release --filter "LookupMovieTmdbIdAsync" -v normal
```

Expected: all 4 PASS

**Step 7: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/MetadataLookupService.cs Jellyfin.Xtream.Library.Tests/Service/MetadataLookupServiceTests.cs
git commit -m "feat: fallback to year-free TMDb lookup when year-qualified search fails"
```

---

### Task 3: Fallback in `LookupSeriesTvdbIdAsync`

**Files:**
- Modify: `Jellyfin.Xtream.Library.Tests/Service/MetadataLookupServiceTests.cs` (add 4 more tests)
- Modify: `Jellyfin.Xtream.Library/Service/MetadataLookupService.cs`

**Step 1: Add required using directives** (if not already present from Task 2):

```csharp
using MediaBrowser.Controller.Entities.TV;
```

**Step 2: Write four failing tests for series fallback**

Add at the end of `MetadataLookupServiceTests.cs`:

```csharp
// === FallbackToYearlessLookup: series ===

[Fact]
public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_RetriesWithoutYear_WhenYearQualifiedFails()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var fallbackResult = new RemoteSearchResult
    {
        Name = "Breaking Bad",
        ProductionYear = 2008,
        ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" },
    };

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .SetupSequence(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>())
        .ReturnsAsync(new[] { fallbackResult });

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", 2012, CancellationToken.None);

    result.Should().Be(81189);
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2));
}

[Fact]
public async Task LookupSeriesTvdbIdAsync_FallbackDisabled_DoesNotRetry()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = false,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", 2012, CancellationToken.None);

    result.Should().BeNull();
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
        Times.Once());
}

[Fact]
public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_NoDoubleCallWhenYearAlreadyNull()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", null, CancellationToken.None);

    result.Should().BeNull();
    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
        Times.Once());
}

[Fact]
public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_CachesFallbackNull_AvoidingRepeatCalls()
{
    InitPlugin(new PluginConfiguration
    {
        EnableMetadataLookup = true,
        FallbackToYearlessLookup = true,
        LibraryPath = string.Empty,
    });

    var mockProvider = new Mock<IProviderManager>();
    mockProvider
        .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(Array.Empty<RemoteSearchResult>());

    var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
    var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

    await svc.LookupSeriesTvdbIdAsync("Unknown Series", 2020, CancellationToken.None);
    await svc.LookupSeriesTvdbIdAsync("Unknown Series", null, CancellationToken.None);

    mockProvider.Verify(
        pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
            It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2));
}
```

**Step 3: Run tests to verify they fail**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release --filter "LookupSeriesTvdbIdAsync" -v normal
```

Expected: all 4 FAIL

**Step 4: Implement fallback in `LookupSeriesTvdbIdAsync`**

In `MetadataLookupService.cs`, find this block in `LookupSeriesTvdbIdAsync` (around line 229):

```csharp
            // Cache the result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TvdbId = tvdbId,
                Confidence = firstResult != null && tvdbId.HasValue ? 100 : 0,
            });

            return tvdbId;
```

Replace with:

```csharp
            // Cache the primary result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TvdbId = tvdbId,
                Confidence = firstResult != null && tvdbId.HasValue ? 100 : 0,
            });

            // Fallback: retry without year if primary failed and feature is enabled.
            if (tvdbId == null && year.HasValue && config.FallbackToYearlessLookup)
            {
                _logger.LogInformation(
                    "Retrying TVDb lookup without year for: '{Title}' (extracted year={Year})",
                    title,
                    year);

                var fallbackKey = MetadataCache.GetSeriesKey(title, null);
                if (_cache.TryGet(fallbackKey, out var fallbackCached, config.MetadataCacheAgeDays))
                {
                    tvdbId = fallbackCached?.TvdbId;
                    _logger.LogDebug("Fallback cache hit for series: {Title} -> TVDb {Id}", title, tvdbId);
                }
                else
                {
                    var fallbackInfo = new SeriesInfo { Name = title, Year = null };
                    var fallbackResults = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                        new RemoteSearchQuery<SeriesInfo> { SearchInfo = fallbackInfo },
                        cancellationToken).ConfigureAwait(false);

                    var fallbackFirst = fallbackResults.FirstOrDefault();
                    if (fallbackFirst?.ProviderIds != null &&
                        fallbackFirst.ProviderIds.TryGetValue(MetadataProvider.Tvdb.ToString(), out var fbStr) &&
                        int.TryParse(fbStr, out var fbId) &&
                        !IsLikelyFalsePositive(title, fallbackFirst.Name, null, fallbackFirst.ProductionYear))
                    {
                        tvdbId = fbId;
                        _logger.LogDebug("Fallback found TVDb ID for series: {Title} -> {Id}", title, tvdbId);
                    }
                    else
                    {
                        _logger.LogDebug("Fallback found no TVDb ID for series: {Title}", title);
                    }

                    _cache.Set(fallbackKey, new MetadataCacheEntry
                    {
                        TvdbId = tvdbId,
                        Confidence = tvdbId.HasValue ? 100 : 0,
                    });
                }
            }

            return tvdbId;
```

**Step 5: Run all tests to verify everything passes**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release -v normal
```

Expected: all tests PASS (was 64 before, should now be 73)

**Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/MetadataLookupService.cs Jellyfin.Xtream.Library.Tests/Service/MetadataLookupServiceTests.cs
git commit -m "feat: fallback to year-free TVDb lookup when year-qualified series search fails"
```

---

### Task 4: Config UI — checkbox in `config.html` and `config.js`

**Files:**
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.html`
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.js`

**Step 1: Add checkbox to `config.html`**

Find this block (around line 373):

```html
                            <div class="checkboxContainer checkboxContainer-withDescription">
                                <label class="emby-checkbox-label">
                                    <input is="emby-checkbox" type="checkbox" id="chkEnableMetadataLookup" name="EnableMetadataLookup" />
                                    <span>Enable Automatic Metadata ID Lookup</span>
                                </label>
                                <div class="fieldDescription checkboxFieldDescription">
                                    Automatically look up TMDb/TVDb IDs using Jellyfin's configured metadata providers.
                                    Requires metadata plugins (TMDb, TVDb) to be installed and configured in Jellyfin.
                                </div>
                            </div>
```

Insert the following block **immediately after** (after the closing `</div>` of that checkbox):

```html
                            <div class="checkboxContainer checkboxContainer-withDescription">
                                <label class="emby-checkbox-label">
                                    <input is="emby-checkbox" type="checkbox" id="chkFallbackToYearlessLookup" name="FallbackToYearlessLookup" />
                                    <span>Fallback to Year-Free Lookup</span>
                                </label>
                                <div class="fieldDescription checkboxFieldDescription">
                                    If a year-qualified metadata lookup returns no result, retry without the year.
                                    Enable this if your provider has many incorrect years in stream names. Note: weakens
                                    false-positive protection for ambiguous titles.
                                </div>
                            </div>
```

**Step 2: Load the value in `config.js`**

Find this block (around line 75):

```js
            // Metadata lookup
            document.getElementById('chkEnableMetadataLookup').checked = config.EnableMetadataLookup !== false;
            document.getElementById('txtMetadataParallelism').value = config.MetadataParallelism || 3;
```

Add the new line immediately after the `chkEnableMetadataLookup` line:

```js
            document.getElementById('chkFallbackToYearlessLookup').checked = config.FallbackToYearlessLookup === true;
```

**Step 3: Save the value in `config.js`**

Find this block (around line 203):

```js
            // Metadata lookup
            config.EnableMetadataLookup = document.getElementById('chkEnableMetadataLookup').checked;
            config.MetadataParallelism = parseInt(document.getElementById('txtMetadataParallelism').value) || 3;
```

Add the new line immediately after the `EnableMetadataLookup` line:

```js
            config.FallbackToYearlessLookup = document.getElementById('chkFallbackToYearlessLookup').checked;
```

**Step 4: Build to verify no compile errors**

```bash
dotnet build Jellyfin.Xtream.Library -c Release
```

Expected: Build succeeded, 0 warnings, 0 errors

**Step 5: Commit**

```bash
git add Jellyfin.Xtream.Library/Configuration/Web/config.html Jellyfin.Xtream.Library/Configuration/Web/config.js
git commit -m "feat: add FallbackToYearlessLookup checkbox to plugin config UI"
```

---

### Task 5: Final verification and release

**Step 1: Run the full test suite**

```bash
dotnet test Jellyfin.Xtream.Library.Tests -c Release -v normal
```

Expected: All tests PASS

**Step 2: Bump the version in `Jellyfin.Xtream.Library.csproj`**

Change `AssemblyVersion` and `FileVersion` from current `1.31.35.0` to `1.31.36.0`.

**Step 3: Commit version bump**

```bash
git add Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj
git commit -m "Release v1.31.36.0: Add year-free metadata lookup fallback option"
```

**Step 4: Tag the release**

```bash
git tag -a v1.31.36.0 -m "Release v1.31.36.0: Add year-free metadata lookup fallback option"
git push origin main --tags
```

**Step 5: Build the release package**

```bash
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release
cd /tmp/claude/xtream-library-release
zip -j /tmp/claude/jellyfin-xtream-library_1.31.36.0.zip Jellyfin.Xtream.Library.dll
md5 -q /tmp/claude/jellyfin-xtream-library_1.31.36.0.zip
```

**Step 6: Create GitHub release**

```bash
GH_INSECURE_NO_TLS_VERIFY=1 gh release create v1.31.36.0 /tmp/claude/jellyfin-xtream-library_1.31.36.0.zip \
  --title "v1.31.36.0: Add year-free metadata lookup fallback" \
  --notes "## What's new

### New Feature: Fallback to Year-Free Lookup (#21)

When enabled, the plugin will retry TMDb/TVDb metadata lookup without the year if the year-qualified search returns no result.

**How to enable:** Plugin Settings → Enable this option: *Fallback to Year-Free Lookup*

**When to use:** Enable this if your provider has stream names with incorrect years (e.g. \`The Notebook (2009)\` instead of 2004) and many movies/series fail to match.

**Note:** Disabling year-based matching slightly weakens false-positive protection for ambiguous titles. If you get incorrect metadata matches after enabling this, consider using TMDb/TVDb folder ID overrides for the affected items."
```

**Step 7: Update plugin repository manifest**

In `../jellyfin-plugin-repo/manifest.json`, add a new version entry at the top of the versions array:

```json
{
  "version": "1.31.36.0",
  "changelog": "Add year-free metadata lookup fallback option for providers with incorrect years in stream names.",
  "targetAbi": "10.11.0.0",
  "sourceUrl": "https://github.com/firestaerter3/Jellyfin-Xtream-Library/releases/download/v1.31.36.0/jellyfin-xtream-library_1.31.36.0.zip",
  "checksum": "<md5 from step 5>",
  "timestamp": "2026-02-28T00:00:00Z"
}
```

Then commit and push:

```bash
cd ../jellyfin-plugin-repo
git add manifest.json
git commit -m "Add Xtream Library v1.31.36.0: Year-free metadata lookup fallback"
git push
```

---

## Notes

- **Clearing metadata cache after enabling:** Users who already have `movie:title:year → null` cached entries must clear the metadata cache (plugin settings → Clear Metadata Cache) after enabling `FallbackToYearlessLookup` to re-trigger lookups for previously unmatched items.
- **Plugin.Instance in tests:** All async method tests use the same `InitPlugin()` helper that mimics the `XtreamTunerHostTests` setup pattern. Plugin.Instance is static and tests may interfere if run in parallel — this is a known limitation of the current test architecture, consistent with other tests in the project.
