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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// File-backed cache for metadata lookup results.
/// </summary>
public sealed class MetadataCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<MetadataCache> _logger;
    private readonly ConcurrentDictionary<string, MetadataCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private string? _cacheFilePath;
    private bool _isDirty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCache"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public MetadataCache(ILogger<MetadataCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of entries in the cache.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Generates a cache key for a movie.
    /// </summary>
    /// <param name="title">The movie title.</param>
    /// <param name="year">The release year.</param>
    /// <returns>A normalized cache key.</returns>
    public static string GetMovieKey(string title, int? year)
    {
        return year.HasValue ? $"movie:{title.ToLowerInvariant()}:{year}" : $"movie:{title.ToLowerInvariant()}";
    }

    /// <summary>
    /// Generates a cache key for a series.
    /// </summary>
    /// <param name="title">The series title.</param>
    /// <param name="year">The premiere year.</param>
    /// <returns>A normalized cache key.</returns>
    public static string GetSeriesKey(string title, int? year)
    {
        return year.HasValue ? $"series:{title.ToLowerInvariant()}:{year}" : $"series:{title.ToLowerInvariant()}";
    }

    /// <summary>
    /// Initializes the cache from the file at the specified library path.
    /// </summary>
    /// <param name="libraryPath">The library path where cache file is stored.</param>
    /// <returns>A task representing the async operation.</returns>
    public async Task InitializeAsync(string libraryPath)
    {
        _cacheFilePath = Path.Combine(libraryPath, ".metadata-cache.json");

        if (File.Exists(_cacheFilePath))
        {
            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = await File.ReadAllTextAsync(_cacheFilePath).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<Dictionary<string, MetadataCacheEntry>>(json);

                if (entries != null)
                {
                    foreach (var kvp in entries)
                    {
                        _cache[kvp.Key] = kvp.Value;
                    }

                    _logger.LogInformation("Loaded {Count} entries from metadata cache", _cache.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load metadata cache, starting fresh");
                _cache.Clear();
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Tries to get a cached entry.
    /// </summary>
    /// <param name="key">The cache key (title + year).</param>
    /// <param name="entry">The cached entry if found.</param>
    /// <param name="maxAgeDays">Maximum age in days before entry is considered stale.</param>
    /// <returns>True if a valid entry was found.</returns>
    public bool TryGet(string key, out MetadataCacheEntry? entry, int maxAgeDays = 30)
    {
        if (_cache.TryGetValue(key, out entry))
        {
            // Check if entry is still valid
            if ((DateTime.UtcNow - entry.LastLookup).TotalDays <= maxAgeDays)
            {
                return true;
            }

            // Entry is stale
            entry = null;
        }

        return false;
    }

    /// <summary>
    /// Sets a cache entry.
    /// </summary>
    /// <param name="key">The cache key (title + year).</param>
    /// <param name="entry">The entry to cache.</param>
    public void Set(string key, MetadataCacheEntry entry)
    {
        entry.LastLookup = DateTime.UtcNow;
        _cache[key] = entry;
        _isDirty = true;
    }

    /// <summary>
    /// Flushes the cache to disk.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    public async Task FlushAsync()
    {
        if (!_isDirty || string.IsNullOrEmpty(_cacheFilePath))
        {
            return;
        }

        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dict = new Dictionary<string, MetadataCacheEntry>(_cache);
            var json = JsonSerializer.Serialize(dict, JsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, json).ConfigureAwait(false);
            _isDirty = false;
            _logger.LogDebug("Flushed {Count} entries to metadata cache", dict.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush metadata cache to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Clears all cached entries and deletes the cache file.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    public async Task ClearAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache.Clear();
            _isDirty = false;

            if (!string.IsNullOrEmpty(_cacheFilePath) && File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
                _logger.LogInformation("Cleared metadata cache");
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _fileLock.Dispose();
        _disposed = true;
    }
}
