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
using System.Linq;
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
    private readonly IProviderManager _providerManager;
    private readonly MetadataCache _cache;
    private readonly ILogger<MetadataLookupService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(3, 3); // Max 3 concurrent lookups
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

            var firstResult = results.FirstOrDefault();
            int? tmdbId = null;

            if (firstResult?.ProviderIds != null &&
                firstResult.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbIdStr) &&
                int.TryParse(tmdbIdStr, out var parsedId))
            {
                tmdbId = parsedId;
                _logger.LogDebug("Found TMDb ID for movie: {Title} ({Year}) -> {Id}", title, year, tmdbId);
            }
            else
            {
                _logger.LogDebug("No TMDb ID found for movie: {Title} ({Year})", title, year);
            }

            // Cache the result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TmdbId = tmdbId,
                Confidence = firstResult != null ? 100 : 0,
            });

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
        if (_cache.TryGet(cacheKey, out var cached, config.MetadataCacheAgeDays))
        {
            _logger.LogDebug("Cache hit for series: {Title} ({Year}) -> TVDb {Id}", title, year, cached?.TvdbId);
            return cached?.TvdbId;
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

            var firstResult = results.FirstOrDefault();
            int? tvdbId = null;

            if (firstResult?.ProviderIds != null &&
                firstResult.ProviderIds.TryGetValue(MetadataProvider.Tvdb.ToString(), out var tvdbIdStr) &&
                int.TryParse(tvdbIdStr, out var parsedId))
            {
                tvdbId = parsedId;
                _logger.LogDebug("Found TVDb ID for series: {Title} ({Year}) -> {Id}", title, year, tvdbId);
            }
            else
            {
                _logger.LogDebug("No TVDb ID found for series: {Title} ({Year})", title, year);
            }

            // Cache the result (even if null, to avoid repeated lookups)
            _cache.Set(cacheKey, new MetadataCacheEntry
            {
                TvdbId = tvdbId,
                Confidence = firstResult != null ? 100 : 0,
            });

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

    /// <inheritdoc />
    public Task FlushCacheAsync() => _cache.FlushAsync();

    /// <inheritdoc />
    public Task ClearCacheAsync() => _cache.ClearAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _rateLimiter.Dispose();
        _disposed = true;
    }
}
