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
    /// no TVDb ID, so almost every series was cached as a miss. Such a miss says nothing about the
    /// fixed search, so it is searched again instead of waiting out the cache age. Found IDs are
    /// kept whatever their version.
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
    public async Task<int?> LookupMovieTmdbIdAsync(string title, int? year, int? releaseDateYear, string? originalTitle, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        if (!config.EnableMetadataLookup)
        {
            return null;
        }

        await InitializeAsync().ConfigureAwait(false);

        var attempts = GetMovieLookupAttempts(title, year, releaseDateYear, originalTitle, config.FallbackToYearlessLookup);
        try
        {
            for (int i = 0; i < attempts.Count; i++)
            {
                var (attemptTitle, attemptYear, checkYear) = attempts[i];

                // Each attempt has its own cache entry, so a miss cached for the first one
                // does not hide a match an earlier run found with a later one. A year-free search
                // checked against a release year is cached per year: its answer depends on it.
                var cacheKey = MetadataCache.GetMovieKey(attemptTitle, attemptYear);
                if (!attemptYear.HasValue && checkYear.HasValue)
                {
                    cacheKey += string.Create(CultureInfo.InvariantCulture, $":check{checkYear}");
                }

                if (_cache.TryGet(cacheKey, out var cached, config.MetadataCacheAgeDays))
                {
                    _logger.LogDebug("Cache hit for movie: {Title} ({Year}) -> TMDb {Id}", attemptTitle, attemptYear, cached?.TmdbId);
                    if (cached?.TmdbId is int cachedId)
                    {
                        return cachedId;
                    }

                    continue;
                }

                if (i > 0)
                {
                    _logger.LogInformation(
                        "Retrying TMDb lookup for '{Title}' ({Year}) as '{AttemptTitle}' ({AttemptYear})",
                        title,
                        year,
                        attemptTitle,
                        attemptYear);
                }

                if (_rateLimiter == null)
                {
                    throw new InvalidOperationException("MetadataLookupService not initialized. Call InitializeAsync first.");
                }

                // Held for one search at a time, so a movie working through its fallbacks does
                // not keep other lookups waiting until their timeout runs out.
                int? tmdbId;
                await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    tmdbId = await SearchMovieTmdbIdAsync(attemptTitle, attemptYear, checkYear, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _rateLimiter.Release();
                }

                // Cache even a miss, to avoid repeating the search
                _cache.Set(cacheKey, new MetadataCacheEntry
                {
                    TmdbId = tmdbId,
                    Confidence = tmdbId.HasValue ? 100 : 0,
                });

                if (tmdbId.HasValue)
                {
                    return tmdbId;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A timeout or a cancelled sync propagates, so the caller can tell it from a miss.
            _logger.LogWarning(ex, "Failed to lookup TMDb ID for movie: {Title} ({Year})", title, year);
            return null;
        }
    }

    /// <summary>
    /// Lists the searches a movie lookup tries, in order. Only the first is tried unless the
    /// year-free fallback is enabled. The fallbacks cover a provider putting the wrong year in the
    /// title (the year of its release date) and a title in another language than the metadata
    /// provider knows it by (the original title).
    /// <para>
    /// Each attempt carries the year its result is checked against. A search without a year still
    /// checks against the release-date year when there is one, so it cannot accept a remake or an
    /// older film of the same name. And when the title has no year, the release-date year is
    /// searched before the title alone, since the year-free search takes whichever version comes
    /// first.
    /// </para>
    /// </summary>
    /// <param name="title">The cleaned movie title.</param>
    /// <param name="year">The year taken from the stream title, if any.</param>
    /// <param name="releaseDateYear">The year of the provider's release date, if any.</param>
    /// <param name="originalTitle">The provider's original title, cleaned, if any.</param>
    /// <param name="fallbackEnabled">Whether the year-free fallback setting is on.</param>
    /// <returns>The attempts, without duplicates.</returns>
    internal static IReadOnlyList<(string Title, int? Year, int? CheckYear)> GetMovieLookupAttempts(
        string title,
        int? year,
        int? releaseDateYear,
        string? originalTitle,
        bool fallbackEnabled)
    {
        var attempts = new List<(string Title, int? Year, int? CheckYear)>();
        void Add(string t, int? y, int? check)
        {
            if (!attempts.Any(a => a.Year == y && string.Equals(a.Title, t, StringComparison.OrdinalIgnoreCase)))
            {
                attempts.Add((t, y, check));
            }
        }

        if (!fallbackEnabled)
        {
            Add(title, year, year);
            return attempts;
        }

        if (!year.HasValue && releaseDateYear.HasValue)
        {
            Add(title, releaseDateYear, releaseDateYear);
        }

        Add(title, year, year ?? releaseDateYear);
        if (releaseDateYear.HasValue)
        {
            Add(title, releaseDateYear, releaseDateYear);
        }

        Add(title, null, releaseDateYear);

        if (!string.IsNullOrWhiteSpace(originalTitle))
        {
            Add(originalTitle, releaseDateYear ?? year, releaseDateYear ?? year);
            Add(originalTitle, null, releaseDateYear);
        }

        return attempts;
    }

    private async Task<int?> SearchMovieTmdbIdAsync(string title, int? year, int? checkYear, CancellationToken cancellationToken)
    {
        var results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
            new RemoteSearchQuery<MovieInfo> { SearchInfo = new MovieInfo { Name = title, Year = year } },
            cancellationToken).ConfigureAwait(false);

        var firstResult = FirstResultWithId(results, MetadataProvider.Tmdb, out var parsedId);
        if (firstResult == null)
        {
            _logger.LogDebug("No TMDb ID found for movie: {Title} ({Year})", title, year);
            return null;
        }

        // Validate the match to reject obvious false positives
        if (IsLikelyFalsePositive(title, firstResult.Name, checkYear, firstResult.ProductionYear))
        {
            _logger.LogDebug(
                "Rejected TMDb match for movie: '{SearchTitle}' -> '{ResultName}' ({ResultYear})",
                title,
                firstResult.Name,
                firstResult.ProductionYear);
            return null;
        }

        _logger.LogDebug("Found TMDb ID for movie: {Title} ({Year}) -> {Id}", title, year, parsedId);
        return parsedId;
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

        // Same shape as the movie lookup, and for the same reason: each attempt has its own cache
        // entry, so a miss cached for the year-qualified search does not hide a match the
        // year-free retry found on an earlier run.
        var attempts = new List<int?> { year };
        if (year.HasValue && config.FallbackToYearlessLookup)
        {
            attempts.Add(null);
        }

        try
        {
            for (int i = 0; i < attempts.Count; i++)
            {
                var attemptYear = attempts[i];
                var cacheKey = MetadataCache.GetSeriesKey(title, attemptYear);
                if (_cache.TryGet(cacheKey, out var cached, config.MetadataCacheAgeDays)
                    && !IsSeriesMissFromBeforeFix(cached))
                {
                    _logger.LogDebug("Cache hit for series: {Title} ({Year}) -> TVDb {Id}", title, attemptYear, cached?.TvdbId);
                    if (cached?.TvdbId is int cachedId)
                    {
                        return cachedId;
                    }

                    continue;
                }

                if (i > 0)
                {
                    _logger.LogInformation(
                        "Retrying TVDb lookup without year for: '{Title}' (extracted year={Year})",
                        title,
                        year);
                }

                if (_rateLimiter == null)
                {
                    throw new InvalidOperationException("MetadataLookupService not initialized. Call InitializeAsync first.");
                }

                int? tvdbId;
                await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    tvdbId = await SearchSeriesTvdbIdAsync(title, attemptYear, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _rateLimiter.Release();
                }

                // Cache even a miss, to avoid repeating the search
                _cache.Set(cacheKey, new MetadataCacheEntry
                {
                    TvdbId = tvdbId,
                    Confidence = tvdbId.HasValue ? 100 : 0,
                    SearchVersion = SeriesSearchVersion,
                });

                if (tvdbId.HasValue)
                {
                    return tvdbId;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to lookup TVDb ID for series: {Title} ({Year})", title, year);
            return null;
        }
    }

    /// <summary>
    /// Whether a cached series entry is a miss from an older version of the search.
    /// </summary>
    /// <param name="entry">The cached entry, or null.</param>
    /// <returns>True when the entry should be searched again.</returns>
    internal static bool IsSeriesMissFromBeforeFix(MetadataCacheEntry? entry)
        => entry is { TvdbId: null } && entry.SearchVersion < SeriesSearchVersion;

    private async Task<int?> SearchSeriesTvdbIdAsync(string title, int? year, CancellationToken cancellationToken)
    {
        var results = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
            new RemoteSearchQuery<SeriesInfo> { SearchInfo = new SeriesInfo { Name = title, Year = year } },
            cancellationToken).ConfigureAwait(false);

        // A result within a year of the searched year comes first; without a year, within a year
        // of the best-ranked result of any provider, so "Ghostwriter" does not become the 1992
        // show when the first result is the 2019 one (GitHub #185). With a known year, a result
        // from that year is a better guess than a higher-ranked same-name show from two years off.
        var resultList = results.ToList();
        var firstResult = FirstResultWithId(resultList, MetadataProvider.Tvdb, out var parsedId, year ?? resultList.FirstOrDefault()?.ProductionYear);
        if (firstResult == null)
        {
            _logger.LogDebug("No TVDb ID found for series: {Title} ({Year})", title, year);
            return null;
        }

        // Validate the match to reject obvious false positives
        if (IsLikelyFalsePositive(title, firstResult.Name, year, firstResult.ProductionYear))
        {
            _logger.LogDebug(
                "Rejected TVDb match for series: '{SearchTitle}' -> '{ResultName}' ({ResultYear})",
                title,
                firstResult.Name,
                firstResult.ProductionYear);
            return null;
        }

        _logger.LogDebug("Found TVDb ID for series: {Title} ({Year}) -> {Id}", title, year, parsedId);
        return parsedId;
    }

    /// <summary>
    /// The first search result that carries a usable ID from <paramref name="provider"/>
    /// (GitHub #185).
    /// <para>
    /// The search does not name a provider, so Jellyfin asks every enabled one and returns their
    /// results one provider after another (ProviderManager.GetRemoteSearchResults). Results of
    /// different providers are only merged when they share an ID, so TheMovieDb's hits, which carry
    /// no TVDb ID, can come before TheTVDB's. Taking only the very first result then failed for
    /// almost every series. Skipping results without the ID keeps each provider's own ranking.
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
