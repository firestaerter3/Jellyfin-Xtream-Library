// CS0618: Legacy PluginConfiguration fields still used here; Phase 4/5 migrates to ProviderConfig.
#pragma warning disable CS0618

// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service that looks up metadata IDs using Jellyfin's provider manager.
/// </summary>
public sealed class MetadataLookupService : IMetadataLookupService, IDisposable
{
    /// <summary>
    /// The version of the series search, stored on each series cache entry. Until GitHub #185
    /// (version 0) the search read only the first result, which is usually TheMovieDb's and has
    /// no TVDb ID, so almost every series was cached as a miss. Such a miss is searched again
    /// instead of waiting out the cache age. Found IDs are kept whatever their version.
    /// </summary>
    internal const int SeriesSearchVersion = 1;

    private readonly IProviderManager _providerManager;
    private readonly MetadataCache _cache;
    private readonly ILogger<MetadataLookupService> _logger;
    private SemaphoreSlim? _rateLimiter;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataLookupService"/> class.
    /// </summary>
    /// <param name="providerManager">Jellyfin's provider manager.</param>
    /// <param name="cache">The metadata cache.</param>
    /// <param name="logger">The logger instance.</param>
    public MetadataLookupService(
        IProviderManager providerManager,
        MetadataCache cache,
        ILogger<MetadataLookupService> logger)
    {
        _providerManager = providerManager;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        var config = Plugin.Instance.Configuration;

        // Initialize rate limiter based on configuration
        var parallelism = Math.Max(1, Math.Min(config.MetadataParallelism, 20));
        _rateLimiter = new SemaphoreSlim(parallelism, parallelism);
        _logger.LogInformation("Metadata lookup initialized with parallelism={Parallelism}", parallelism);

        if (!string.IsNullOrEmpty(config.LibraryPath))
        {
            await _cache.InitializeAsync(config.LibraryPath).ConfigureAwait(false);
        }

        _initialized = true;
    }

    /// <inheritdoc />
    public async Task<int?> LookupMovieTmdbIdAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.EnableMetadataLookup)
        {
            return null;
        }

        await InitializeAsync().ConfigureAwait(false);

        var cacheKey = MetadataCache.GetMovieKey(title, year);
        if (_cache.TryGet(cacheKey, out var cached, config.MetadataCacheAgeDays))
        {
            _logger.LogDebug("Cache hit for movie: {Title} ({Year}) -> TMDb {Id}", title, year, cached?.TmdbId);
            return cached?.TmdbId;
        }

        if (_rateLimiter == null)
        {
            throw new InvalidOperationException("MetadataLookupService not initialized. Call InitializeAsync first.");
        }

        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var searchInfo = new MovieInfo
            {
                Name = title,
                Year = year,
            };

            var results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo> { SearchInfo = searchInfo },
                cancellationToken).ConfigureAwait(false);

            var firstResult = FirstResultWithId(results, MetadataProvider.Tmdb, out var parsedId);
            int? tmdbId = null;

            if (firstResult != null)
            {
                // Validate the match to reject obvious false positives
                if (IsLikelyFalsePositive(title, firstResult.Name, year, firstResult.ProductionYear))
                {
                    _logger.LogDebug(
                        "Rejected TMDb match for movie: '{SearchTitle}' -> '{ResultName}' ({ResultYear})",
                        title,
                        firstResult.Name,
                        firstResult.ProductionYear);
                }
                else
                {
                    tmdbId = parsedId;
                    _logger.LogDebug("Found TMDb ID for movie: {Title} ({Year}) -> {Id}", title, year, tmdbId);
                }
            }
            else
            {
                _logger.LogDebug("No TMDb ID found for movie: {Title} ({Year})", title, year);
            }

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

                    var fallbackFirst = FirstResultWithId(fallbackResults, MetadataProvider.Tmdb, out var fbId);
                    if (fallbackFirst != null &&
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lookup TMDb ID for movie: {Title} ({Year})", title, year);
            return null;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int?> LookupSeriesTvdbIdAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.EnableMetadataLookup)
        {
            return null;
        }

        await InitializeAsync().ConfigureAwait(false);

        var cacheKey = MetadataCache.GetSeriesKey(title, year);
        if (_cache.TryGet(cacheKey, out var cached, config.MetadataCacheAgeDays) && !IsSeriesMissFromBeforeFix(cached))
        {
            _logger.LogDebug("Cache hit for series: {Title} ({Year}) -> TVDb {Id}", title, year, cached?.TvdbId);
            return cached?.TvdbId;
        }

        if (_rateLimiter == null)
        {
            throw new InvalidOperationException("MetadataLookupService not initialized. Call InitializeAsync first.");
        }

        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var searchInfo = new SeriesInfo
            {
                Name = title,
                Year = year,
            };

            var results = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo> { SearchInfo = searchInfo },
                cancellationToken).ConfigureAwait(false);

            // A result within a year of the searched year comes first; without a year, within a
            // year of the best-ranked result of any provider (GitHub #185).
            var resultList = results.ToList();
            var firstResult = FirstResultWithId(resultList, MetadataProvider.Tvdb, out var parsedId, year ?? resultList.FirstOrDefault()?.ProductionYear);
            int? tvdbId = null;

            if (firstResult != null)
            {
                // Validate the match to reject obvious false positives
                if (IsLikelyFalsePositive(title, firstResult.Name, year, firstResult.ProductionYear))
                {
                    _logger.LogDebug(
                        "Rejected TVDb match for series: '{SearchTitle}' -> '{ResultName}' ({ResultYear})",
                        title,
                        firstResult.Name,
                        firstResult.ProductionYear);
                }
                else
                {
                    tvdbId = parsedId;
                    _logger.LogDebug("Found TVDb ID for series: {Title} ({Year}) -> {Id}", title, year, tvdbId);
                }
            }
            else
            {
                _logger.LogDebug("No TVDb ID found for series: {Title} ({Year})", title, year);
            }

            // Cache the primary result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TvdbId = tvdbId,
                Confidence = firstResult != null && tvdbId.HasValue ? 100 : 0,
                SearchVersion = SeriesSearchVersion,
            });

            // Fallback: retry without year if primary failed and feature is enabled.
            if (tvdbId == null && year.HasValue && config.FallbackToYearlessLookup)
            {
                _logger.LogInformation(
                    "Retrying TVDb lookup without year for: '{Title}' (extracted year={Year})",
                    title,
                    year);

                var fallbackKey = MetadataCache.GetSeriesKey(title, null);
                if (_cache.TryGet(fallbackKey, out var fallbackCached, config.MetadataCacheAgeDays) && !IsSeriesMissFromBeforeFix(fallbackCached))
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

                    var fallbackList = fallbackResults.ToList();
                    var fallbackFirst = FirstResultWithId(fallbackList, MetadataProvider.Tvdb, out var fbId, fallbackList.FirstOrDefault()?.ProductionYear);
                    if (fallbackFirst != null &&
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
                        SearchVersion = SeriesSearchVersion,
                    });
                }
            }

            return tvdbId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lookup TVDb ID for series: {Title} ({Year})", title, year);
            return null;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Whether a cached series entry is a miss from an older version of the search.
    /// </summary>
    /// <param name="entry">The cached entry, or null.</param>
    /// <returns>True when the entry should be searched again.</returns>
    internal static bool IsSeriesMissFromBeforeFix(MetadataCacheEntry? entry)
        => entry is { TvdbId: null } && entry.SearchVersion < SeriesSearchVersion;

    /// <summary>
    /// The first search result that carries a usable ID from <paramref name="provider"/>
    /// (GitHub #185).
    /// <para>
    /// The search does not name a provider, so Jellyfin asks every enabled one and returns their
    /// results one provider after another, merging only results that share an ID. TheMovieDb's
    /// hits, which carry no TVDb ID, can come first; taking only the very first result then
    /// failed for almost every series.
    /// </para>
    /// </summary>
    /// <param name="results">The search results, in the order Jellyfin returned them.</param>
    /// <param name="provider">The provider whose ID is wanted.</param>
    /// <param name="id">The parsed ID of the returned result, or 0.</param>
    /// <param name="preferredYear">When set, a result with the ID within a year of this is taken
    /// before an earlier one from another year; otherwise the first result with the ID.</param>
    /// <returns>The result, or null when none has a numeric ID from that provider.</returns>
    internal static RemoteSearchResult? FirstResultWithId(IEnumerable<RemoteSearchResult> results, MetadataProvider provider, out int id, int? preferredYear = null)
    {
        var idName = provider.ToString();
        RemoteSearchResult? first = null;
        int firstId = 0;
        foreach (var result in results)
        {
            if (result?.ProviderIds == null
                || !result.ProviderIds.TryGetValue(idName, out var value)
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            if (!preferredYear.HasValue
                || (result.ProductionYear.HasValue && Math.Abs(result.ProductionYear.Value - preferredYear.Value) <= 1))
            {
                id = parsed;
                return result;
            }

            if (first == null)
            {
                first = result;
                firstId = parsed;
            }
        }

        id = firstId;
        return first;
    }

    /// <inheritdoc />
    public Task FlushCacheAsync() => _cache.FlushAsync();

    /// <inheritdoc />
    public Task ClearCacheAsync() => _cache.ClearAsync();

    /// <summary>
    /// Checks whether a search result is likely a false positive match.
    /// Uses year mismatch and title length ratio to detect bad matches
    /// without breaking cross-language matching (e.g. Dutch title to English result).
    /// </summary>
    /// <param name="searchTitle">The original title used for the search query.</param>
    /// <param name="resultName">The title returned by the metadata provider.</param>
    /// <param name="searchYear">The year associated with the search, if known.</param>
    /// <param name="resultYear">The production year of the result from the provider.</param>
    /// <returns>True if the match is likely a false positive and should be rejected.</returns>
    internal static bool IsLikelyFalsePositive(string searchTitle, string? resultName, int? searchYear, int? resultYear)
    {
        // Check 1: Year mismatch from parameters
        if (searchYear.HasValue && resultYear.HasValue &&
            Math.Abs(searchYear.Value - resultYear.Value) > 2)
        {
            return true;
        }

        // Check 2: Extract year from search title text when no explicit year parameter.
        // Only match years that appear after some text (not at the start, to avoid
        // rejecting movies named with a year like "1917" or "2001: A Space Odyssey").
        if (!searchYear.HasValue && resultYear.HasValue)
        {
            var yearMatch = Regex.Match(searchTitle, @"(?<=\S\s)(19|20)\d{2}(?=\s|$)");
            if (yearMatch.Success &&
                int.TryParse(yearMatch.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var titleYear) &&
                Math.Abs(titleYear - resultYear.Value) > 2)
            {
                return true;
            }
        }

        // Check 3: Result title is extremely short compared to search title.
        // Catches cases like "Formule 1 2023 USA Austin SprintRace" → "+1".
        if (!string.IsNullOrEmpty(resultName) &&
            resultName.Length <= 3 && searchTitle.Length >= 15)
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _rateLimiter?.Dispose();
        _disposed = true;
    }
}
