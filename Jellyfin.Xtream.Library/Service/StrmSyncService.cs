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
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service responsible for syncing Xtream content to STRM files.
/// </summary>
public partial class StrmSyncService
{
    // Static HttpClient is intentional for connection pooling and efficient socket usage.
    // For image downloads, we don't need per-request configuration, and a shared client
    // improves performance by reusing TCP connections. A default User-Agent is set below.
    private static readonly HttpClient ImageHttpClient = CreateImageHttpClient();

    private readonly IXtreamClient _client;
    private readonly IDispatcharrClient _dispatcharrClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IMetadataLookupService _metadataLookup;
    private readonly SnapshotService _snapshotService;
    private readonly DeltaCalculator _deltaCalculator;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<StrmSyncService> _logger;
    private readonly object _ctsLock = new();
    private readonly List<SyncResult> _syncHistory = new();
    private readonly object _syncHistoryLock = new();
    private bool _historyLoaded;
    private CancellationTokenSource? _currentSyncCts;
    private bool _syncSuppressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSyncService"/> class.
    /// </summary>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="dispatcharrClient">The Dispatcharr REST API client.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="snapshotService">The snapshot persistence service.</param>
    /// <param name="deltaCalculator">The delta calculator for incremental sync.</param>
    /// <param name="appPaths">The application paths service.</param>
    /// <param name="logger">The logger instance.</param>
    public StrmSyncService(
        IXtreamClient client,
        IDispatcharrClient dispatcharrClient,
        ILibraryManager libraryManager,
        IMetadataLookupService metadataLookup,
        SnapshotService snapshotService,
        DeltaCalculator deltaCalculator,
        IServerApplicationPaths appPaths,
        ILogger<StrmSyncService> logger)
    {
        _client = client;
        _dispatcharrClient = dispatcharrClient;
        _libraryManager = libraryManager;
        _metadataLookup = metadataLookup;
        _snapshotService = snapshotService;
        _deltaCalculator = deltaCalculator;
        _appPaths = appPaths;
        _logger = logger;

        // Restore LastSyncResult from disk so the dashboard shows
        // the most recent sync result immediately after restart.
        lock (_syncHistoryLock)
        {
            EnsureHistoryLoaded();
        }
    }

    /// <summary>
    /// Gets the result of the last sync operation.
    /// </summary>
    public SyncResult? LastSyncResult { get; private set; }

    /// <summary>
    /// Gets the current sync progress.
    /// </summary>
    public SyncProgress CurrentProgress { get; } = new SyncProgress();

    private string SyncHistoryPath => Path.Combine(_appPaths.DataPath, "xtream-library", "sync_history.json");

    /// <summary>
    /// Gets the list of failed items from the last sync that can be retried.
    /// </summary>
    public IReadOnlyList<FailedItem> FailedItems => LastSyncResult?.FailedItems ?? Array.Empty<FailedItem>();

    /// <summary>
    /// Gets the sync history (last 10 results, most recent first).
    /// </summary>
    public IReadOnlyList<SyncResult> SyncHistory
    {
        get
        {
            lock (_syncHistoryLock)
            {
                EnsureHistoryLoaded();
                return _syncHistory.ToList();
            }
        }
    }

    /// <summary>
    /// Cancels the currently running sync operation, if any.
    /// </summary>
    /// <returns>True if a sync was cancelled, false if no sync was running.</returns>
    public bool CancelSync()
    {
        lock (_ctsLock)
        {
            if (_currentSyncCts != null && !_currentSyncCts.IsCancellationRequested && CurrentProgress.IsRunning)
            {
                _logger.LogInformation("Cancelling sync operation...");
                _currentSyncCts.Cancel();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Suppresses all scheduled/automatic sync operations until a manual sync is triggered.
    /// Used by CleanLibraries to prevent the scheduler from repopulating content.
    /// </summary>
    public void SuppressSync()
    {
        _syncSuppressed = true;
        _logger.LogInformation("Sync suppressed until manually triggered");
    }

    /// <summary>
    /// Clears sync suppression, allowing syncs to run again.
    /// Called when user manually triggers a sync.
    /// </summary>
    public void ClearSuppression()
    {
        if (_syncSuppressed)
        {
            _syncSuppressed = false;
            _logger.LogInformation("Sync suppression cleared");
        }
    }

    /// <summary>
    /// Retries syncing all failed items from the last sync.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The retry result with statistics.</returns>
    public async Task<SyncResult> RetryFailedAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var result = new SyncResult { StartTime = DateTime.UtcNow };

        var itemsToRetry = FailedItems.ToList();
        if (itemsToRetry.Count == 0)
        {
            result.EndTime = DateTime.UtcNow;
            result.Success = true;
            return result;
        }

        _logger.LogInformation("Retrying {Count} failed items", itemsToRetry.Count);

        CurrentProgress.IsRunning = true;
        CurrentProgress.StartTime = DateTime.UtcNow;
        CurrentProgress.Phase = "Retrying failed items";
        CurrentProgress.TotalItems = itemsToRetry.Count;
        CurrentProgress.ItemsProcessed = 0;

        try
        {
            var connectionInfo = Plugin.Instance.Creds;
            string moviesPath = Path.Combine(config.LibraryPath, "Movies");
            string seriesPath = Path.Combine(config.LibraryPath, "Series");

            foreach (var item in itemsToRetry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    CurrentProgress.CurrentItem = item.Name;

                    if (item.ItemType == "Movie")
                    {
                        await RetryMovieAsync(connectionInfo, moviesPath, item, result, cancellationToken).ConfigureAwait(false);
                    }
                    else if (item.ItemType == "Series")
                    {
                        await RetrySingleSeriesAsync(connectionInfo, seriesPath, item, result, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
                {
                    // Item ID no longer exists - try to find by name (may have been re-added with new ID)
                    if (item.ItemType == "Series")
                    {
                        var foundSeries = await FindSeriesByNameAsync(connectionInfo, item.Name, cancellationToken).ConfigureAwait(false);
                        if (foundSeries != null)
                        {
                            _logger.LogInformation(
                                "Found series with new ID {NewId} (was {OldId}): {SeriesName}",
                                foundSeries.SeriesId,
                                item.ItemId,
                                item.Name);

                            try
                            {
                                var newItem = new FailedItem
                                {
                                    ItemType = "Series",
                                    ItemId = foundSeries.SeriesId,
                                    Name = foundSeries.Name,
                                };
                                await RetrySingleSeriesAsync(connectionInfo, seriesPath, newItem, result, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception retryEx)
                            {
                                _logger.LogWarning(retryEx, "Failed to sync series with new ID: {SeriesName}", foundSeries.Name);
                                result.Errors++;
                                result.AddFailedItem(new FailedItem
                                {
                                    ItemType = "Series",
                                    ItemId = foundSeries.SeriesId,
                                    Name = foundSeries.Name,
                                    ErrorMessage = retryEx.Message,
                                });
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Series still returning 404 and not findable by name, keeping in failed list: {SeriesName}",
                                item.Name);
                            result.Errors++;
                            result.AddFailedItem(new FailedItem
                            {
                                ItemType = item.ItemType,
                                ItemId = item.ItemId,
                                Name = item.Name,
                                ErrorMessage = "404 Not Found (provider may be temporarily unavailable)",
                            });
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Movie still returning 404, keeping in failed list: {ItemName}",
                            item.Name);
                        result.Errors++;
                        result.AddFailedItem(new FailedItem
                        {
                            ItemType = item.ItemType,
                            ItemId = item.ItemId,
                            Name = item.Name,
                            ErrorMessage = "404 Not Found (provider may be temporarily unavailable)",
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retry failed for {ItemType}: {ItemName}", item.ItemType, item.Name);
                    result.Errors++;
                    result.AddFailedItem(new FailedItem
                    {
                        ItemType = item.ItemType,
                        ItemId = item.ItemId,
                        Name = item.Name,
                        SeriesId = item.SeriesId,
                        SeasonNumber = item.SeasonNumber,
                        EpisodeNumber = item.EpisodeNumber,
                        ErrorMessage = ex.Message,
                    });
                }
                finally
                {
                    CurrentProgress.IncrementItemsProcessed();
                }
            }

            result.EndTime = DateTime.UtcNow;
            result.Success = true;

            _logger.LogInformation(
                "Retry completed: {MoviesCreated} movies, {EpisodesCreated} episodes created; {Errors} still failed",
                result.MoviesCreated,
                result.EpisodesCreated,
                result.Errors);

            // Update last sync result to remove successfully retried items
            if (LastSyncResult != null)
            {
                LastSyncResult.MoviesCreated += result.MoviesCreated;
                LastSyncResult.EpisodesCreated += result.EpisodesCreated;
                LastSyncResult.SeriesCreated += result.SeriesCreated;
                LastSyncResult.SeasonsCreated += result.SeasonsCreated;
                LastSyncResult.SetFailedItems(result.FailedItems);
                LastSyncResult.Errors = result.FailedItems.Count;
            }

            // Trigger library scan if enabled and items were created or updated
            if (config.TriggerLibraryScan && (result.MoviesCreated > 0 || result.MoviesUpdated > 0 || result.EpisodesCreated > 0 || result.EpisodesUpdated > 0))
            {
                _logger.LogWarning("Triggering full Jellyfin library scan after retry. Consider disabling this option for large libraries.");
                await TriggerLibraryScanAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            result.EndTime = DateTime.UtcNow;
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Retry operation failed");
        }
        finally
        {
            CurrentProgress.IsRunning = false;
        }

        return result;
    }

    private async Task RetryMovieAsync(
        ConnectionInfo connectionInfo,
        string moviesPath,
        FailedItem item,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        // Use stored item info to build the STRM file directly
        var customTerms = Plugin.Instance.Configuration.CustomTitleRemoveTerms;
        string movieName = SanitizeFileName(item.Name, customTerms);
        int? year = ExtractYear(item.Name);
        string folderName = year.HasValue ? $"{movieName} ({year})" : movieName;
        string? versionLabel = ExtractVersionLabel(item.Name);
        string movieFolder = Path.Combine(moviesPath, folderName);
        string strmFileName = BuildMovieStrmFileName(folderName, versionLabel);
        string strmPath = Path.Combine(movieFolder, strmFileName);

        // Build stream URL using stored item ID (assume mp4 as default extension)
        string streamUrl = $"{connectionInfo.BaseUrl}/movie/{connectionInfo.UserName}/{connectionInfo.Password}/{item.ItemId}.mp4";

        if (File.Exists(strmPath))
        {
            if (StrmContentMatches(strmPath, streamUrl))
            {
                result.MoviesSkipped++;
                return;
            }

            // Stream URL changed, update the STRM file
            await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
            result.MoviesUpdated++;
            _logger.LogInformation("Updated stale STRM for movie: {MovieName}", item.Name);
            return;
        }

        Directory.CreateDirectory(movieFolder);

        await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
        result.MoviesCreated++;

        _logger.LogInformation("Retry successful for movie: {MovieName}", item.Name);
    }

    private async Task RetrySingleSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        FailedItem item,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var seriesInfo = await _client.GetSeriesStreamsBySeriesAsync(connectionInfo, item.ItemId, cancellationToken).ConfigureAwait(false);

        if (seriesInfo.Episodes == null || seriesInfo.Episodes.Count == 0)
        {
            _logger.LogWarning("Series has no episodes: {SeriesName}", item.Name);
            return;
        }

        var customTerms = Plugin.Instance.Configuration.CustomTitleRemoveTerms;
        string seriesName = SanitizeFileName(item.Name, customTerms);
        int? year = ExtractYear(item.Name);
        string seriesFolderName = year.HasValue ? $"{seriesName} ({year})" : seriesName;
        string seriesFolder = Path.Combine(seriesPath, seriesFolderName);
        bool isNewSeries = !Directory.Exists(seriesFolder);
        bool createdEpisodes = false;

        foreach (var seasonEntry in seriesInfo.Episodes)
        {
            int seasonNumber = seasonEntry.Key;
            var episodes = seasonEntry.Value;
            string seasonFolder = Path.Combine(seriesFolder, $"Season {seasonNumber}");
            bool isNewSeason = !Directory.Exists(seasonFolder);
            bool seasonCreatedEpisodes = false;

            foreach (var episode in episodes)
            {
                string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode, customTerms);
                string strmPath = Path.Combine(seasonFolder, episodeFileName);

                string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

                if (File.Exists(strmPath))
                {
                    if (StrmContentMatches(strmPath, streamUrl))
                    {
                        result.EpisodesSkipped++;
                        continue;
                    }

                    // Stream URL changed, update the STRM file
                    await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
                    result.EpisodesUpdated++;
                    continue;
                }

                Directory.CreateDirectory(seasonFolder);

                await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
                result.EpisodesCreated++;
                seasonCreatedEpisodes = true;
                createdEpisodes = true;
            }

            if (isNewSeason && seasonCreatedEpisodes)
            {
                result.SeasonsCreated++;
            }
        }

        if (isNewSeries && createdEpisodes)
        {
            result.SeriesCreated++;
        }

        _logger.LogInformation("Retry successful for series: {SeriesName}", item.Name);
    }

    /// <summary>
    /// Searches for a series by name in the selected categories.
    /// Used to find series that may have been re-added with a new ID.
    /// </summary>
    private async Task<Series?> FindSeriesByNameAsync(
        ConnectionInfo connectionInfo,
        string seriesName,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var categoryIds = config.SelectedSeriesCategoryIds;

        // If no categories selected, we can't search efficiently
        if (categoryIds == null || categoryIds.Length == 0)
        {
            _logger.LogDebug("No series categories configured, cannot search for series by name");
            return null;
        }

        // Normalize the name for comparison (remove special chars, lowercase)
        string normalizedSearchName = NormalizeSeriesName(seriesName);

        foreach (var categoryId in categoryIds)
        {
            try
            {
                var seriesList = await _client.GetSeriesByCategoryAsync(connectionInfo, categoryId, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var series in seriesList)
                {
                    string normalizedSeriesName = NormalizeSeriesName(series.Name);
                    if (string.Equals(normalizedSearchName, normalizedSeriesName, StringComparison.OrdinalIgnoreCase))
                    {
                        return series;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error searching category {CategoryId} for series", categoryId);
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a series name for comparison by removing prefixes, special characters, and extra whitespace.
    /// </summary>
    private static string NormalizeSeriesName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // Remove common prefixes like "|NL|", "┃NL┃", etc.
        var normalized = Regex.Replace(name, @"^[\|\┃][A-Z]+[\|\┃]\s*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));

        // Remove year suffix like "(2024)" for comparison
        normalized = Regex.Replace(normalized, @"\s*\(\d{4}\)\s*$", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));

        // Normalize whitespace
        normalized = Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

        return normalized;
    }

    /// <summary>
    /// Performs a full sync of all content from the Xtream provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result with statistics.</returns>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var result = new SyncResult { StartTime = DateTime.UtcNow };

        // Check if sync is suppressed (e.g., after CleanLibraries)
        if (_syncSuppressed)
        {
            _logger.LogInformation("Sync suppressed (libraries were cleaned, waiting for manual sync trigger)");
            result.EndTime = DateTime.UtcNow;
            result.Error = "Sync suppressed — use the Sync button to resume";
            return result;
        }

        // Create linked cancellation token source for cancel support
        CancellationToken linkedToken;
        lock (_ctsLock)
        {
            _currentSyncCts?.Dispose();
            _currentSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedToken = _currentSyncCts.Token;
        }

        // Initialize progress tracking
        CurrentProgress.IsRunning = true;
        CurrentProgress.StartTime = DateTime.UtcNow;
        CurrentProgress.Phase = "Initializing";
        CurrentProgress.CurrentItem = string.Empty;
        CurrentProgress.TotalCategories = 0;
        CurrentProgress.CategoriesProcessed = 0;
        CurrentProgress.TotalItems = 0;
        CurrentProgress.ItemsProcessed = 0;
        CurrentProgress.MoviesCreated = 0;
        CurrentProgress.MoviesUpdated = 0;
        CurrentProgress.EpisodesCreated = 0;
        CurrentProgress.EpisodesUpdated = 0;

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            _logger.LogWarning("Xtream credentials not configured, skipping sync");
            result.Error = "Credentials not configured";
            CurrentProgress.IsRunning = false;
            return result;
        }

        var connectionInfo = Plugin.Instance.Creds;

        // Configure rate limiting settings
        _client.RequestDelayMs = config.RequestDelayMs;
        _client.MaxRetries = config.MaxRetries;
        _client.RetryDelayMs = config.RetryDelayMs;

        // Load previous snapshot for incremental sync
        ContentSnapshot? previousSnapshot = null;
        ContentSnapshot? hintSnapshot = null;
        bool isIncrementalSync = false;

        if (config.EnableIncrementalSync)
        {
            CurrentProgress.Phase = "Loading snapshot";
            previousSnapshot = await _snapshotService.LoadLatestSnapshotAsync(linkedToken).ConfigureAwait(false);

            // Keep raw snapshot as hint for smart-skip optimization (even during full sync)
            hintSnapshot = previousSnapshot;

            if (previousSnapshot != null)
            {
                // Force full sync if provider URL changed
                if (!string.Equals(previousSnapshot.ProviderUrl, config.BaseUrl, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Provider URL changed ({Old} -> {New}), forcing full sync", previousSnapshot.ProviderUrl, config.BaseUrl);
                    previousSnapshot = null;
                }

                // Force full sync if folder structure config changed
                if (previousSnapshot != null)
                {
                    var currentFingerprint = SnapshotService.CalculateConfigFingerprint(config);
                    if (!string.IsNullOrEmpty(previousSnapshot.ConfigFingerprint) &&
                        !string.Equals(previousSnapshot.ConfigFingerprint, currentFingerprint, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("Configuration changed (folder mode, categories, or metadata settings), forcing full sync");
                        previousSnapshot = null;
                    }
                }

                // Force full sync if snapshot is too old
                if (previousSnapshot != null)
                {
                    var daysSinceSnapshot = (DateTime.UtcNow - previousSnapshot.CreatedAt).TotalDays;
                    if (daysSinceSnapshot >= config.FullSyncIntervalDays)
                    {
                        _logger.LogInformation("Snapshot is {Days:F1} days old (threshold: {Threshold} days), forcing full sync", daysSinceSnapshot, config.FullSyncIntervalDays);
                        previousSnapshot = null;
                    }
                }
            }

            isIncrementalSync = previousSnapshot != null;
        }

        _logger.LogInformation(
            "Starting {SyncType} Xtream library sync to {LibraryPath} (requestDelay={DelayMs}ms, maxRetries={MaxRetries})",
            isIncrementalSync ? "incremental" : "full",
            config.LibraryPath,
            config.RequestDelayMs,
            config.MaxRetries);

        var existingStrmFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track all collected content for snapshot building
        var allCollectedMovies = new ConcurrentBag<StreamInfo>();
        var allCollectedSeries = new ConcurrentBag<Series>();
        var allSeriesInfoDict = new ConcurrentDictionary<int, SeriesStreamInfo>();

        try
        {
            // Ensure base directories exist
            string moviesPath = Path.Combine(config.LibraryPath, "Movies");
            string seriesPath = Path.Combine(config.LibraryPath, "Series");

            Directory.CreateDirectory(moviesPath);
            Directory.CreateDirectory(seriesPath);

            // Collect existing STRM files for orphan cleanup
            if (config.CleanupOrphans)
            {
                if (config.SyncMovies)
                {
                    CollectExistingStrmFiles(moviesPath, existingStrmFiles);
                }

                if (config.SyncSeries)
                {
                    CollectExistingStrmFiles(seriesPath, existingStrmFiles);
                }
            }

            var syncedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            // Sync Movies and Series - run concurrently when parallelism > 1,
            // sequentially when parallelism <= 1 to respect strict rate limits
            bool runConcurrently = config.SyncParallelism > 1;
            CurrentProgress.Phase = isIncrementalSync ? "Incremental Sync: Movies + Series" : "Syncing Movies + Series";

            if (runConcurrently)
            {
                var syncTasks = new List<Task>();
                if (config.SyncMovies)
                {
                    syncTasks.Add(Task.Run(
                        async () =>
                        {
                            _logger.LogInformation("Syncing movies/VOD content...");
                            await SyncMoviesAsync(
                                connectionInfo,
                                moviesPath,
                                syncedFiles,
                                result,
                                previousSnapshot,
                                allCollectedMovies,
                                linkedToken).ConfigureAwait(false);
                        },
                        linkedToken));
                }

                if (config.SyncSeries)
                {
                    syncTasks.Add(Task.Run(
                        async () =>
                        {
                            _logger.LogInformation("Syncing series content...");
                            await SyncSeriesAsync(
                                connectionInfo,
                                seriesPath,
                                syncedFiles,
                                result,
                                previousSnapshot,
                                hintSnapshot,
                                allCollectedSeries,
                                allSeriesInfoDict,
                                linkedToken).ConfigureAwait(false);
                        },
                        linkedToken));
                }

                await Task.WhenAll(syncTasks).ConfigureAwait(false);
            }
            else
            {
                // Sequential sync to ensure only 1 API request at a time
                if (config.SyncMovies)
                {
                    _logger.LogInformation("Syncing movies/VOD content (sequential mode)...");
                    await SyncMoviesAsync(
                        connectionInfo,
                        moviesPath,
                        syncedFiles,
                        result,
                        previousSnapshot,
                        allCollectedMovies,
                        linkedToken).ConfigureAwait(false);
                }

                if (config.SyncSeries)
                {
                    _logger.LogInformation("Syncing series content (sequential mode)...");
                    await SyncSeriesAsync(
                        connectionInfo,
                        seriesPath,
                        syncedFiles,
                        result,
                        previousSnapshot,
                        hintSnapshot,
                        allCollectedSeries,
                        allSeriesInfoDict,
                        linkedToken).ConfigureAwait(false);
                }
            }

            // Clear concurrent sub-phases so subsequent sequential phases show via Phase
            CurrentProgress.MoviePhase = string.Empty;
            CurrentProgress.SeriesPhase = string.Empty;

            // Cleanup orphaned files - works for both full and incremental syncs because
            // incrementally-skipped items have their existing STRM paths added to syncedFiles
            if (config.CleanupOrphans)
            {
                CurrentProgress.Phase = "Cleaning up orphans";
                CurrentProgress.CurrentItem = string.Empty;
                var orphanedFiles = existingStrmFiles.Except(syncedFiles.Keys, StringComparer.OrdinalIgnoreCase).ToList();

                // Protection: Check if deletion would exceed safety threshold (provider glitch protection)
                double safetyThreshold = config.OrphanSafetyThreshold;
                int orphanedMovies = orphanedFiles.Count(f => f.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase));
                int orphanedEpisodes = orphanedFiles.Count(f => f.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase));
                int existingMovieCount = existingStrmFiles.Count(f => f.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase));
                int existingEpisodeCount = existingStrmFiles.Count(f => f.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase));

                double movieDeletionRatio = existingMovieCount > 0 ? (double)orphanedMovies / existingMovieCount : 0;
                double episodeDeletionRatio = existingEpisodeCount > 0 ? (double)orphanedEpisodes / existingEpisodeCount : 0;

                bool skipMovieCleanup = existingMovieCount > 10 && movieDeletionRatio > safetyThreshold;
                bool skipEpisodeCleanup = existingEpisodeCount > 10 && episodeDeletionRatio > safetyThreshold;

                if (skipMovieCleanup)
                {
                    _logger.LogWarning(
                        "Skipping movie orphan cleanup: {OrphanCount}/{ExistingCount} ({Percent:P0}) exceeds {Threshold:P0} safety threshold - possible provider issue",
                        orphanedMovies,
                        existingMovieCount,
                        movieDeletionRatio,
                        safetyThreshold);
                }

                if (skipEpisodeCleanup)
                {
                    _logger.LogWarning(
                        "Skipping episode orphan cleanup: {OrphanCount}/{ExistingCount} ({Percent:P0}) exceeds {Threshold:P0} safety threshold - possible provider issue",
                        orphanedEpisodes,
                        existingEpisodeCount,
                        episodeDeletionRatio,
                        safetyThreshold);
                }

                // Filter orphans based on safety checks
                var safeOrphans = orphanedFiles
                    .Where(f =>
                        !(skipMovieCleanup && f.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase)) &&
                        !(skipEpisodeCleanup && f.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                CurrentProgress.TotalItems = safeOrphans.Count;
                CurrentProgress.ItemsProcessed = 0;

                foreach (var orphan in safeOrphans)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        CurrentProgress.IncrementItemsProcessed();
                        File.Delete(orphan);
                        result.FilesDeleted++;

                        // Track movie vs episode deletions separately
                        if (orphan.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase))
                        {
                            result.MoviesDeleted++;
                        }
                        else if (orphan.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase))
                        {
                            result.EpisodesDeleted++;
                        }

                        _logger.LogDebug("Deleted orphaned file: {FilePath}", orphan);

                        // Try to clean up empty parent directories
                        CleanupEmptyDirectories(Path.GetDirectoryName(orphan)!, config.LibraryPath, seriesPath, result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned file: {FilePath}", orphan);
                    }
                }

                if (safeOrphans.Count > 0)
                {
                    _logger.LogInformation(
                        "Cleaned up {Count} orphaned STRM files ({Movies} movies, {Episodes} episodes) and {Series} series, {Seasons} seasons",
                        safeOrphans.Count,
                        result.MoviesDeleted,
                        result.EpisodesDeleted,
                        result.SeriesDeleted,
                        result.SeasonsDeleted);
                }
            }

            // Save snapshot for next incremental sync
            if (config.EnableIncrementalSync && !linkedToken.IsCancellationRequested)
            {
                CurrentProgress.Phase = "Saving snapshot";
                await SaveSnapshotAsync(config, allCollectedMovies, allCollectedSeries, allSeriesInfoDict, result.FailedItems, linkedToken).ConfigureAwait(false);
            }

            result.WasIncrementalSync = isIncrementalSync;
            result.EndTime = DateTime.UtcNow;
            result.Success = true;

            _logger.LogInformation(
                "{SyncType} sync completed: Movies({MoviesCreated} added, {MoviesUpdated} updated, {MoviesSkipped} skipped, {MoviesDeleted} deleted), Series({SeriesCreated} added, {SeriesSkipped} skipped), Episodes({EpisodesCreated} added, {EpisodesUpdated} updated, {EpisodesSkipped} skipped, {EpisodesDeleted} deleted)",
                isIncrementalSync ? "Incremental" : "Full",
                result.MoviesCreated,
                result.MoviesUpdated,
                result.MoviesSkipped,
                result.MoviesDeleted,
                result.SeriesCreated,
                result.SeriesSkipped,
                result.EpisodesCreated,
                result.EpisodesUpdated,
                result.EpisodesSkipped,
                result.EpisodesDeleted);

            // Flush metadata cache to disk
            if (config.EnableMetadataLookup)
            {
                await _metadataLookup.FlushCacheAsync().ConfigureAwait(false);
            }

            // Trigger library scan if enabled (off by default - file monitor handles changes automatically)
            if (config.TriggerLibraryScan && (result.MoviesCreated > 0 || result.MoviesUpdated > 0 || result.EpisodesCreated > 0 || result.EpisodesUpdated > 0 || result.FilesDeleted > 0))
            {
                _logger.LogWarning("Triggering full Jellyfin library scan ({Movies} movies, {Episodes} episodes created, {MoviesUpdated} movies, {EpisodesUpdated} episodes updated, {Deleted} deleted). This may use significant memory with large libraries. Consider disabling this option and relying on Jellyfin's file monitor instead.", result.MoviesCreated, result.EpisodesCreated, result.MoviesUpdated, result.EpisodesUpdated, result.FilesDeleted);
                await TriggerLibraryScanAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync was cancelled");
            result.Error = "Sync was cancelled by user";
            result.EndTime = DateTime.UtcNow;
            result.Success = false;
            CurrentProgress.Phase = "Cancelled";
        }
        catch (OutOfMemoryException ex)
        {
            _logger.LogError(ex, "Sync failed due to out of memory - try reducing Category Batch Size");
            result.Error = "Out of memory - reduce Category Batch Size in settings (current batches may be too large for available memory)";
            result.EndTime = DateTime.UtcNow;
            result.Success = false;
            CurrentProgress.Phase = "Failed - Out of Memory";
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogError(ex, "Sync failed - server may have restarted due to memory pressure");
            result.Error = "Server restarted during sync - possible memory issue. Try reducing Category Batch Size.";
            result.EndTime = DateTime.UtcNow;
            result.Success = false;
            CurrentProgress.Phase = "Failed - Server Restarted";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed with error");
            result.Error = ex.Message;
            result.EndTime = DateTime.UtcNow;
        }
        finally
        {
            // Ensure cache is flushed even on error
            try
            {
                await _metadataLookup.FlushCacheAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush metadata cache");
            }

            CurrentProgress.IsRunning = false;
            if (CurrentProgress.Phase != "Cancelled")
            {
                CurrentProgress.Phase = "Complete";
            }

            // Clean up the CTS
            lock (_ctsLock)
            {
                _currentSyncCts?.Dispose();
                _currentSyncCts = null;
            }
        }

        LastSyncResult = result;
        RecordSyncHistory(result);
        return result;
    }

    private void RecordSyncHistory(SyncResult result)
    {
        lock (_syncHistoryLock)
        {
            EnsureHistoryLoaded();
            _syncHistory.Insert(0, result);
            while (_syncHistory.Count > 10)
            {
                _syncHistory.RemoveAt(_syncHistory.Count - 1);
            }

            PersistHistory();
        }
    }

    /// <summary>
    /// Loads sync history from disk if not already loaded. Must be called within _syncHistoryLock.
    /// </summary>
    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded)
        {
            return;
        }

        _historyLoaded = true;

        try
        {
            var path = SyncHistoryPath;
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var entries = JsonConvert.DeserializeObject<List<SyncResult>>(json);
            if (entries != null)
            {
                _syncHistory.Clear();
                _syncHistory.AddRange(entries);
                _logger.LogInformation("Loaded {Count} sync history entries from disk", entries.Count);

                // Restore LastSyncResult from history if not set (e.g., after restart)
                if (LastSyncResult == null && _syncHistory.Count > 0)
                {
                    LastSyncResult = _syncHistory[0];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sync history from disk");
        }
    }

    /// <summary>
    /// Persists sync history to disk. Must be called within _syncHistoryLock.
    /// </summary>
    private void PersistHistory()
    {
        try
        {
            var path = SyncHistoryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonConvert.SerializeObject(_syncHistory, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist sync history to disk");
        }
    }

    private async Task SyncMoviesAsync(
        ConnectionInfo connectionInfo,
        string moviesPath,
        ConcurrentDictionary<string, byte> syncedFiles,
        SyncResult result,
        ContentSnapshot? previousSnapshot,
        ConcurrentBag<StreamInfo> allCollectedMovies,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var categories = await _client.GetVodCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        var processedStreamIds = new ConcurrentDictionary<int, bool>();

        // Filter categories if user has selected specific ones
        var selectedIds = config.SelectedVodCategoryIds;
        if (selectedIds.Length > 0)
        {
            var selectedIdSet = selectedIds.ToHashSet();
            var filteredCategories = categories.Where(c => selectedIdSet.Contains(c.CategoryId)).ToList();
            var skippedCount = categories.Count - filteredCategories.Count;
            if (skippedCount > 0)
            {
                _logger.LogInformation("Filtering VOD categories: {Selected} selected, {Skipped} skipped", filteredCategories.Count, skippedCount);
            }

            // Warn about non-existent category IDs
            var existingIds = categories.Select(c => c.CategoryId).ToHashSet();
            var missingIds = selectedIds.Where(id => !existingIds.Contains(id)).ToList();
            if (missingIds.Count > 0)
            {
                _logger.LogWarning("Selected VOD category IDs not found on provider: {MissingIds}", string.Join(", ", missingIds));
            }

            categories = filteredCategories;
        }

        // Parse folder mappings (category ID → folder names) - only in Multiple folder mode
        var folderMappings = new Dictionary<int, List<string>>();
        if (string.Equals(config.MovieFolderMode, "Multiple", StringComparison.OrdinalIgnoreCase))
        {
            folderMappings = ParseFolderMappings(config.MovieFolderMappings);
            if (folderMappings.Count > 0)
            {
                _logger.LogInformation("Multiple folder mode: loaded movie folder mappings for {Count} categories", folderMappings.Count);
            }
        }

        // Determine batch size - 0 means process all at once (legacy behavior)
        var batchSize = config.CategoryBatchSize > 0 ? config.CategoryBatchSize : categories.Count;
        var totalBatches = (int)Math.Ceiling((double)categories.Count / batchSize);

        _logger.LogInformation(
            "Processing movies from {Count} categories in {Batches} batch(es) of {BatchSize} (parallelism={Parallelism})...",
            categories.Count,
            totalBatches,
            batchSize,
            config.SyncParallelism);

        CurrentProgress.AddTotalCategories(totalBatches);
        CurrentProgress.MoviePhase = "Syncing Movies";

        // Thread-safe counters and collections (shared across batches)
        int moviesCreated = 0;
        int moviesUpdated = 0;
        int moviesSkipped = 0;
        int errors = 0;
        int unmatchedCount = 0;
        var failedItems = new ConcurrentBag<FailedItem>();
        var unmatchedMovies = new ConcurrentBag<string>();

        // Parse folder ID overrides
        var tmdbOverrides = ParseFolderIdOverrides(config.TmdbFolderIdOverrides);
        if (tmdbOverrides.Count > 0)
        {
            _logger.LogInformation("Loaded {Count} TMDb folder ID overrides", tmdbOverrides.Count);
        }

        var parallelism = Math.Max(1, config.SyncParallelism);
        var enableMetadataLookup = config.EnableMetadataLookup;
        var enableProactiveMediaInfo = config.EnableProactiveMediaInfo;
        var enableDispatcharrMode = config.EnableDispatcharrMode && !string.IsNullOrEmpty(config.DispatcharrApiUser);
        if (enableDispatcharrMode)
        {
            _dispatcharrClient.RequestDelayMs = config.RequestDelayMs;
            _dispatcharrClient.Configure(config.DispatcharrApiUser, config.DispatcharrApiPass);
            _logger.LogInformation("Dispatcharr mode enabled, will discover multi-stream providers per movie");
        }

        _logger.LogInformation("Processing movies with parallelism={Parallelism}, metadataLookup={MetadataLookup}, proactiveMediaInfo={ProactiveMediaInfo}", parallelism, enableMetadataLookup, enableProactiveMediaInfo);

        // Pre-scan existing movie folders for faster skip detection
        var existingMovieFolders = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(moviesPath))
        {
            _logger.LogInformation("Pre-scanning existing movie folders...");
            CurrentProgress.Phase = "Scanning existing movies";

            // Scan root movie folder
            var rootFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.GetDirectories(moviesPath))
            {
                var folderName = Path.GetFileName(folder);
                // Extract base name (remove [tmdbid-X] suffix if present)
                var bracketIndex = folderName.LastIndexOf(" [", StringComparison.Ordinal);
                var baseKey = bracketIndex > 0 ? folderName[..bracketIndex] : folderName;
                rootFolders.TryAdd(baseKey, folderName);
            }

            existingMovieFolders[string.Empty] = rootFolders;

            // Scan category subfolders if using Multiple folder mode
            if (folderMappings.Count > 0)
            {
                var subfolderNames = folderMappings.Values.SelectMany(v => v).Distinct();
                foreach (var subfolder in subfolderNames)
                {
                    var subPath = Path.Combine(moviesPath, subfolder);
                    if (Directory.Exists(subPath))
                    {
                        var subFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var folder in Directory.GetDirectories(subPath))
                        {
                            var folderName = Path.GetFileName(folder);
                            var bracketIndex = folderName.LastIndexOf(" [", StringComparison.Ordinal);
                            var baseKey = bracketIndex > 0 ? folderName[..bracketIndex] : folderName;
                            subFolders.TryAdd(baseKey, folderName);
                        }

                        existingMovieFolders[subfolder] = subFolders;
                    }
                }
            }

            var totalFolders = existingMovieFolders.Values.Sum(d => d.Count);
            _logger.LogInformation("Found {Count} existing movie folders", totalFolders);
        }

        // Process categories in batches to reduce memory usage
        for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchCategories = categories
                .Skip(batchIndex * batchSize)
                .Take(batchSize)
                .ToList();

            _logger.LogInformation(
                "Processing movie batch {Batch}/{Total} ({Count} categories)...",
                batchIndex + 1,
                totalBatches,
                batchCategories.Count);

            CurrentProgress.MoviePhase = $"Collecting movies (batch {batchIndex + 1}/{totalBatches})";

            // Collect movies from this batch of categories
            var batchMovies = new List<(StreamInfo Stream, HashSet<int> CategoryIds)>();
            var batchCategoryMap = new ConcurrentDictionary<int, HashSet<int>>();
            var streamBag = new ConcurrentBag<(StreamInfo Stream, int CategoryId)>();

            await Parallel.ForEachAsync(
                batchCategories,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (category, ct) =>
                {
                    try
                    {
                        var streams = await _client.GetVodStreamsByCategoryAsync(connectionInfo, category.CategoryId, ct)
                            .ConfigureAwait(false);
                        foreach (var stream in streams)
                        {
                            streamBag.Add((stream, category.CategoryId));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch VOD streams for category {CategoryId} ({CategoryName})", category.CategoryId, category.CategoryName);
                        Interlocked.Increment(ref errors);
                    }
                }).ConfigureAwait(false);

            // Process collected streams to build category membership
            foreach (var (stream, categoryId) in streamBag)
            {
                if (processedStreamIds.TryAdd(stream.StreamId, true))
                {
                    var categorySet = new HashSet<int> { categoryId };
                    batchCategoryMap[stream.StreamId] = categorySet;
                    batchMovies.Add((stream, categorySet));
                }
                else if (batchCategoryMap.TryGetValue(stream.StreamId, out var existingCategories))
                {
                    lock (existingCategories)
                    {
                        existingCategories.Add(categoryId);
                    }
                }
            }

            _logger.LogInformation("Batch {Batch}: Found {Count} unique movies", batchIndex + 1, batchMovies.Count);

            // Track all collected movies for snapshot building
            foreach (var (stream, _) in batchMovies)
            {
                allCollectedMovies.Add(stream);
            }

            // Incremental sync: filter to only new/modified movies
            if (previousSnapshot != null)
            {
                var originalCount = batchMovies.Count;
                var unchangedMovies = new List<(StreamInfo Stream, HashSet<int> CategoryIds)>();
                batchMovies = batchMovies.Where(m =>
                {
                    var checksum = SnapshotService.CalculateChecksum(m.Stream);
                    if (previousSnapshot.Movies.TryGetValue(m.Stream.StreamId, out var prev))
                    {
                        if (prev.Checksum == checksum)
                        {
                            unchangedMovies.Add(m);
                            return false; // Unchanged
                        }
                    }

                    return true; // New or modified
                }).ToList();

                // Track existing STRM paths for unchanged movies (orphan protection)
                foreach (var m in unchangedMovies)
                {
                    string movieName = SanitizeFileName(m.Stream.Name, config.CustomTitleRemoveTerms);
                    int? year = ExtractYear(m.Stream.Name);
                    string baseName = year.HasValue ? $"{movieName} ({year})" : movieName;

                    var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var categoryId in m.CategoryIds)
                    {
                        if (folderMappings.TryGetValue(categoryId, out var mappedFolders))
                        {
                            foreach (var folder in mappedFolders)
                            {
                                targetFolders.Add(folder);
                            }
                        }
                    }

                    if (targetFolders.Count == 0)
                    {
                        targetFolders.Add(string.Empty);
                    }

                    foreach (var targetFolder in targetFolders)
                    {
                        if (existingMovieFolders.TryGetValue(targetFolder, out var folderCache) &&
                            folderCache.TryGetValue(baseName, out var existingFolderName))
                        {
                            string movieBasePath = string.IsNullOrEmpty(targetFolder)
                                ? moviesPath
                                : Path.Combine(moviesPath, targetFolder);
                            string movieFolder = Path.Combine(movieBasePath, existingFolderName);
                            if (Directory.Exists(movieFolder))
                            {
                                foreach (var strmFile in Directory.GetFiles(movieFolder, "*.strm"))
                                {
                                    syncedFiles.TryAdd(strmFile, 0);
                                }
                            }
                        }
                    }
                }

                var skipped = unchangedMovies.Count;
                if (skipped > 0)
                {
                    _logger.LogInformation("Incremental sync: skipping {Skipped} unchanged movies, processing {Count} changed", skipped, batchMovies.Count);
                    Interlocked.Add(ref moviesSkipped, skipped);
                }
            }

            // Pre-fetch VOD info for new movies that need metadata lookup
            // This bulk phase is much faster than per-movie fetching during processing
            var vodInfoCache = new ConcurrentDictionary<int, VodInfoResponse?>();
            if (enableMetadataLookup)
            {
                var moviesToPreFetch = batchMovies.Where(m =>
                {
                    string movieName = SanitizeFileName(m.Stream.Name, config.CustomTitleRemoveTerms);
                    int? year = ExtractYear(m.Stream.Name);
                    string baseName = year.HasValue ? $"{movieName} ({year})" : movieName;
                    if (tmdbOverrides.ContainsKey(baseName))
                    {
                        return false;
                    }

                    // Check if folder already exists (no need to fetch VOD info)
                    foreach (var targetFolder in m.CategoryIds.SelectMany(cid =>
                        folderMappings.TryGetValue(cid, out var f) ? f : Enumerable.Empty<string>())
                        .DefaultIfEmpty(string.Empty))
                    {
                        if (existingMovieFolders.TryGetValue(targetFolder, out var fc) &&
                            fc.ContainsKey(baseName))
                        {
                            return false;
                        }
                    }

                    return true;
                }).ToList();

                if (moviesToPreFetch.Count > 0)
                {
                    int preFetched = 0;
                    int preFetchTotal = moviesToPreFetch.Count;
                    _logger.LogInformation("Pre-fetching VOD info for {Count} movies (batch {Batch}/{Total})...", preFetchTotal, batchIndex + 1, totalBatches);
                    CurrentProgress.MoviePhase = $"Fetching movie info (batch {batchIndex + 1}/{totalBatches}): 0/{preFetchTotal}";
                    await Parallel.ForEachAsync(
                        moviesToPreFetch,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelism,
                            CancellationToken = cancellationToken,
                        },
                        async (movieEntry, ct) =>
                        {
                            try
                            {
                                var info = await _client.GetVodInfoAsync(connectionInfo, movieEntry.Stream.StreamId, ct).ConfigureAwait(false);
                                vodInfoCache[movieEntry.Stream.StreamId] = info;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to pre-fetch VOD info: {StreamId}", movieEntry.Stream.StreamId);
                            }
                            finally
                            {
                                var count = Interlocked.Increment(ref preFetched);
                                string name = SanitizeFileName(movieEntry.Stream.Name, config.CustomTitleRemoveTerms);
                                CurrentProgress.MoviePhase = $"Fetching movie info (batch {batchIndex + 1}/{totalBatches}): {name} ({count}/{preFetchTotal})";
                            }
                        }).ConfigureAwait(false);

                    _logger.LogInformation("Pre-fetched {Cached}/{Total} movie VOD infos (batch {Batch}/{TotalBatches})", vodInfoCache.Count, preFetchTotal, batchIndex + 1, totalBatches);
                }
            }

            // Pre-fetch Dispatcharr providers for movies in this batch
            var dispatcharrCache = new ConcurrentDictionary<int, (string Uuid, List<Client.Models.DispatcharrMovieProvider> Providers)>();
            if (enableDispatcharrMode && batchMovies.Count > 0)
            {
                int dpFetched = 0;
                int dpTotal = batchMovies.Count;
                _logger.LogInformation("Fetching Dispatcharr providers for {Count} movies (batch {Batch}/{Total})...", dpTotal, batchIndex + 1, totalBatches);
                CurrentProgress.MoviePhase = $"Fetching providers (batch {batchIndex + 1}/{totalBatches}): 0/{dpTotal}";

                await Parallel.ForEachAsync(
                    batchMovies,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (movieEntry, ct) =>
                    {
                        try
                        {
                            var detail = await _dispatcharrClient.GetMovieDetailAsync(connectionInfo.BaseUrl, movieEntry.Stream.StreamId, ct).ConfigureAwait(false);
                            if (detail != null && !string.IsNullOrEmpty(detail.Uuid))
                            {
                                var providers = await _dispatcharrClient.GetMovieProvidersAsync(connectionInfo.BaseUrl, movieEntry.Stream.StreamId, ct).ConfigureAwait(false);
                                if (providers.Count > 1)
                                {
                                    // Deduplicate by stream_id
                                    var seen = new HashSet<int>();
                                    var unique = new List<Client.Models.DispatcharrMovieProvider>();
                                    foreach (var p in providers)
                                    {
                                        if (seen.Add(p.StreamId))
                                        {
                                            unique.Add(p);
                                        }
                                    }

                                    dispatcharrCache[movieEntry.Stream.StreamId] = (detail.Uuid, unique);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to fetch Dispatcharr providers for movie {StreamId}", movieEntry.Stream.StreamId);
                        }
                        finally
                        {
                            var count = Interlocked.Increment(ref dpFetched);
                            CurrentProgress.MoviePhase = $"Fetching providers (batch {batchIndex + 1}/{totalBatches}): {movieEntry.Stream.Name} ({count}/{dpTotal})";
                        }
                    }).ConfigureAwait(false);

                _logger.LogInformation("Found {Count} multi-provider movies in batch {Batch}/{Total}", dispatcharrCache.Count, batchIndex + 1, totalBatches);
            }

            CurrentProgress.MoviePhase = $"Syncing Movies (batch {batchIndex + 1}/{totalBatches})";
            CurrentProgress.AddTotalItems(batchMovies.Count);

            // Process movies in this batch
            await Parallel.ForEachAsync(
                batchMovies,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (movieEntry, ct) =>
                {
                var stream = movieEntry.Stream;
                var categoryIds = movieEntry.CategoryIds;

                try
                {
                    string movieName = SanitizeFileName(stream.Name, config.CustomTitleRemoveTerms);
                    int? year = ExtractYear(stream.Name);
                    string baseName = year.HasValue ? $"{movieName} ({year})" : movieName;
                    string? versionLabel = ExtractVersionLabel(stream.Name);

                    CurrentProgress.MoviePhase = $"Syncing Movies (batch {batchIndex + 1}/{totalBatches}): {baseName}";

                    // Determine target folders based on category mappings
                    var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var categoryId in categoryIds)
                    {
                        if (folderMappings.TryGetValue(categoryId, out var mappedFolders))
                        {
                            foreach (var folder in mappedFolders)
                            {
                                targetFolders.Add(folder);
                            }
                        }
                    }

                    if (targetFolders.Count == 0)
                    {
                        targetFolders.Add(string.Empty);
                    }

                    // Quick check: does a folder for this movie already exist? If so, skip API calls
                    // Uses pre-scanned cache for O(1) lookup instead of filesystem scan
                    string? existingFolderName = null;
                    foreach (var targetFolder in targetFolders)
                    {
                        if (existingMovieFolders.TryGetValue(targetFolder, out var folderCache) &&
                            folderCache.TryGetValue(baseName, out var cachedFolder))
                        {
                            existingFolderName = cachedFolder;
                            break;
                        }
                    }

                    // If folder exists, use existing name and skip API calls
                    VodInfoResponse? vodInfo = null;
                    int? providerTmdbId = null;
                    int? autoLookupTmdbId = null;
                    string folderName;

                    if (existingFolderName != null)
                    {
                        folderName = existingFolderName;
                    }
                    else
                    {
                        // New movie - get provider TMDB ID from pre-fetched cache
                        if (enableMetadataLookup && !tmdbOverrides.ContainsKey(baseName))
                        {
                            if (vodInfoCache.TryGetValue(stream.StreamId, out var cachedInfo))
                            {
                                vodInfo = cachedInfo;
                            }
                            else
                            {
                                try
                                {
                                    vodInfo = await _client.GetVodInfoAsync(connectionInfo, stream.StreamId, ct).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to fetch VOD info for provider TMDB: {StreamId}", stream.StreamId);
                                }
                            }

                            if (!string.IsNullOrEmpty(vodInfo?.Info?.TmdbId) && int.TryParse(vodInfo.Info.TmdbId, out int tmdbParsed))
                            {
                                providerTmdbId = tmdbParsed;
                            }
                        }

                        // Only do metadata lookup if provider doesn't have TMDB ID
                        if (!providerTmdbId.HasValue && enableMetadataLookup && !tmdbOverrides.ContainsKey(baseName))
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                            try
                            {
                                autoLookupTmdbId = await _metadataLookup.LookupMovieTmdbIdAsync(movieName, year, timeoutCts.Token).ConfigureAwait(false);
                                if (!autoLookupTmdbId.HasValue)
                                {
                                    Interlocked.Increment(ref unmatchedCount);
                                    unmatchedMovies.Add(baseName);
                                }
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                _logger.LogWarning("Metadata lookup timed out for movie: {MovieName}", movieName);
                                Interlocked.Increment(ref unmatchedCount);
                                unmatchedMovies.Add(baseName);
                            }
                        }

                        folderName = BuildMovieFolderName(movieName, year, tmdbOverrides, providerTmdbId, autoLookupTmdbId);
                    }

                    // Build STRM URLs and filenames — Dispatcharr multi-stream or standard single-stream
                    var strmEntries = new List<(string StreamUrl, string StrmFileName)>();
                    if (enableDispatcharrMode &&
                        dispatcharrCache.TryGetValue(stream.StreamId, out var movieProviderInfo))
                    {
                        var providers = movieProviderInfo.Providers;
                        string uuid = movieProviderInfo.Uuid;
                        for (int i = 0; i < providers.Count; i++)
                        {
                            string providerStreamUrl = $"{connectionInfo.BaseUrl}/proxy/vod/movie/{uuid}?stream_id={providers[i].StreamId}";
                            string strmFileName = BuildMovieStrmFileName(folderName, i == 0 ? null : $"Version {i + 1}");
                            strmEntries.Add((providerStreamUrl, strmFileName));
                        }
                    }
                    else
                    {
                        string extension = string.IsNullOrEmpty(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension;
                        string streamUrl = $"{connectionInfo.BaseUrl}/movie/{connectionInfo.UserName}/{connectionInfo.Password}/{stream.StreamId}.{extension}";
                        string strmFileName = BuildMovieStrmFileName(folderName, versionLabel);
                        strmEntries.Add((streamUrl, strmFileName));
                    }

                    bool anyCreated = false;
                    bool anyUpdated = false;
                    bool allSkipped = true;
                    string? firstPosterPath = null;

                    // Sync to each target folder
                    foreach (var targetFolder in targetFolders)
                    {
                        string movieBasePath = string.IsNullOrEmpty(targetFolder)
                            ? moviesPath
                            : Path.Combine(moviesPath, targetFolder);
                        string movieFolder = Path.Combine(movieBasePath, folderName);

                        foreach (var (streamUrl, strmFileName) in strmEntries)
                        {
                        string strmPath = Path.Combine(movieFolder, strmFileName);

                        syncedFiles.TryAdd(strmPath, 0);

                        if (File.Exists(strmPath))
                        {
                            if (StrmContentMatches(strmPath, streamUrl))
                            {
                                continue;
                            }

                            // Stream URL changed, update the STRM file
                            await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                            anyUpdated = true;
                            allSkipped = false;
                            continue;
                        }

                        allSkipped = false;

                        // Create movie folder
                        Directory.CreateDirectory(movieFolder);

                        // Write STRM file
                        try
                        {
                            await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                            anyCreated = true;
                        }
                        catch (IOException) when (File.Exists(strmPath))
                        {
                            // File was created by another thread/process, skip
                            continue;
                        }

                        _logger.LogDebug("Created movie STRM: {StrmPath}", strmPath);
                        } // end strmEntries foreach

                        // Write NFO if proactive media info enabled (only for first target folder)
                        if (anyCreated && enableProactiveMediaInfo && targetFolders.First() == targetFolder)
                        {
                            try
                            {
                                // Reuse vodInfo if already fetched, otherwise fetch now
                                if (vodInfo == null)
                                {
                                    vodInfo = await _client.GetVodInfoAsync(connectionInfo, stream.StreamId, ct)
                                        .ConfigureAwait(false);
                                }

                                if (vodInfo?.Info != null)
                                {
                                    var nfoPath = Path.Combine(movieFolder, $"{folderName}.nfo");
                                    await NfoWriter.WriteMovieNfoAsync(
                                        nfoPath,
                                        movieName,
                                        vodInfo.Info.Video,
                                        vodInfo.Info.Audio,
                                        vodInfo.Info.DurationSecs,
                                        ct).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to fetch VOD info for NFO: {StreamId}", stream.StreamId);
                            }
                        }

                        // Download artwork for unmatched movies (only for first target folder)
                        if (targetFolders.First() == targetFolder &&
                            !providerTmdbId.HasValue && !autoLookupTmdbId.HasValue && !tmdbOverrides.ContainsKey(baseName) &&
                            config.DownloadArtworkForUnmatched && !string.IsNullOrEmpty(stream.StreamIcon))
                        {
                            var posterExt = GetImageExtension(stream.StreamIcon);
                            var posterPath = Path.Combine(movieFolder, $"poster{posterExt}");
                            await DownloadImageAsync(stream.StreamIcon, posterPath, ct).ConfigureAwait(false);
                            firstPosterPath = posterPath;
                        }

                        // Copy artwork to additional folders
                        else if (firstPosterPath != null && File.Exists(firstPosterPath))
                        {
                            var posterExt = Path.GetExtension(firstPosterPath);
                            var posterPath = Path.Combine(movieFolder, $"poster{posterExt}");
                            try
                            {
                                File.Copy(firstPosterPath, posterPath, overwrite: false);
                            }
                            catch (IOException)
                            {
                                // File already exists or copy failed, continue
                            }
                        }
                    }

                    if (anyCreated)
                    {
                        Interlocked.Increment(ref moviesCreated);
                    }
                    else if (anyUpdated)
                    {
                        Interlocked.Increment(ref moviesUpdated);
                    }
                    else if (allSkipped)
                    {
                        Interlocked.Increment(ref moviesSkipped);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create STRM for movie: {MovieName}", stream.Name);
                    Interlocked.Increment(ref errors);
                    Interlocked.Increment(ref moviesSkipped);
                    failedItems.Add(new FailedItem
                    {
                        ItemType = "Movie",
                        ItemId = stream.StreamId,
                        Name = stream.Name,
                        ErrorMessage = ex.Message,
                    });
                }
                finally
                {
                    CurrentProgress.IncrementItemsProcessed();
                    CurrentProgress.MoviesCreated = moviesCreated;
                    CurrentProgress.MoviesUpdated = moviesUpdated;
                }
            }).ConfigureAwait(false);

            // Update batch progress
            CurrentProgress.IncrementCategoriesProcessed();

            // Allow GC to reclaim batch memory
            batchMovies.Clear();
            streamBag = null!;
        } // End of batch loop

        // Update result with thread-safe counters
        result.MoviesCreated += moviesCreated;
        result.MoviesUpdated += moviesUpdated;
        result.MoviesSkipped += moviesSkipped;
        result.AddErrors(errors);
        result.AddFailedItems(failedItems);
        result.MoviesUnmatched = unmatchedCount;

        // Log unmatched movies
        if (unmatchedCount > 0)
        {
            _logger.LogInformation("Movies without TMDb match: {Count} items", unmatchedCount);
            foreach (var movie in unmatchedMovies.OrderBy(m => m))
            {
                _logger.LogInformation("  Unmatched movie: {MovieName}", movie);
            }
        }

        CurrentProgress.MoviePhase = string.Empty;
    }

    private async Task SyncSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        ConcurrentDictionary<string, byte> syncedFiles,
        SyncResult result,
        ContentSnapshot? previousSnapshot,
        ContentSnapshot? hintSnapshot,
        ConcurrentBag<Series> allCollectedSeries,
        ConcurrentDictionary<int, SeriesStreamInfo> allSeriesInfoDict,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var categories = await _client.GetSeriesCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        var processedSeriesIds = new ConcurrentDictionary<int, bool>();

        // Filter categories if user has selected specific ones
        var selectedIds = config.SelectedSeriesCategoryIds;
        if (selectedIds.Length > 0)
        {
            var selectedIdSet = selectedIds.ToHashSet();
            var filteredCategories = categories.Where(c => selectedIdSet.Contains(c.CategoryId)).ToList();
            var skippedCount = categories.Count - filteredCategories.Count;
            if (skippedCount > 0)
            {
                _logger.LogInformation("Filtering Series categories: {Selected} selected, {Skipped} skipped", filteredCategories.Count, skippedCount);
            }

            // Warn about non-existent category IDs
            var existingIds = categories.Select(c => c.CategoryId).ToHashSet();
            var missingIds = selectedIds.Where(id => !existingIds.Contains(id)).ToList();
            if (missingIds.Count > 0)
            {
                _logger.LogWarning("Selected Series category IDs not found on provider: {MissingIds}", string.Join(", ", missingIds));
            }

            categories = filteredCategories;
        }

        // Parse folder mappings (category ID → folder names) - only in Multiple folder mode
        var folderMappings = new Dictionary<int, List<string>>();
        if (string.Equals(config.SeriesFolderMode, "Multiple", StringComparison.OrdinalIgnoreCase))
        {
            folderMappings = ParseFolderMappings(config.SeriesFolderMappings);
            if (folderMappings.Count > 0)
            {
                _logger.LogInformation("Multiple folder mode: loaded series folder mappings for {Count} categories", folderMappings.Count);
            }
        }

        // Determine batch size - 0 means process all at once (legacy behavior)
        var batchSize = config.CategoryBatchSize > 0 ? config.CategoryBatchSize : categories.Count;
        var totalBatches = (int)Math.Ceiling((double)categories.Count / batchSize);

        _logger.LogInformation(
            "Processing series from {Count} categories in {Batches} batch(es) of {BatchSize} (parallelism={Parallelism})...",
            categories.Count,
            totalBatches,
            batchSize,
            config.SyncParallelism);

        CurrentProgress.SeriesPhase = "Syncing Series";
        CurrentProgress.AddTotalCategories(totalBatches);

        // Thread-safe counters (shared across batches)
        int seriesCreated = 0;
        int seriesSkipped = 0;
        int seasonsCreated = 0;
        int seasonsSkipped = 0;
        int episodesCreated = 0;
        int episodesUpdated = 0;
        int episodesSkipped = 0;
        int errors = 0;
        int smartSkipped = 0;
        int unmatchedCount = 0;
        var processedSeasons = new ConcurrentDictionary<string, bool>();
        var failedItems = new ConcurrentBag<FailedItem>();
        var unmatchedSeries = new ConcurrentBag<string>();

        // Parse folder ID overrides
        var tvdbOverrides = ParseFolderIdOverrides(config.TvdbFolderIdOverrides);
        if (tvdbOverrides.Count > 0)
        {
            _logger.LogInformation("Loaded {Count} TVDb folder ID overrides", tvdbOverrides.Count);
        }

        var parallelism = Math.Max(1, config.SyncParallelism);
        var enableMetadataLookup = config.EnableMetadataLookup;
        var enableProactiveMediaInfo = config.EnableProactiveMediaInfo;
        _logger.LogInformation("Processing series with parallelism={Parallelism}, smartSkip={SmartSkip}, metadataLookup={MetadataLookup}, proactiveMediaInfo={ProactiveMediaInfo}", parallelism, config.SmartSkipExisting, enableMetadataLookup, enableProactiveMediaInfo);

        // Pre-scan existing series folders for fast skip-before-API-call detection
        // seriesFolderLookup provides O(1) lookup by (parentDir, baseName) instead of O(N) linear scan
        var existingSeriesFolderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seriesFolderLookup = new Dictionary<string, (string Path, int Count)>(StringComparer.OrdinalIgnoreCase);
        if ((config.SmartSkipExisting || config.CleanupOrphans) && Directory.Exists(seriesPath))
        {
            CurrentProgress.Phase = "Scanning existing series";
            _logger.LogInformation("Pre-scanning existing series folders...");

            // Scan all series directories and count STRM files
            var scanDirs = new List<string> { seriesPath };
            if (folderMappings.Count > 0)
            {
                foreach (var subfolder in folderMappings.Values.SelectMany(v => v).Distinct())
                {
                    var subPath = Path.Combine(seriesPath, subfolder);
                    if (Directory.Exists(subPath))
                    {
                        scanDirs.Add(subPath);
                    }
                }
            }

            foreach (var scanDir in scanDirs)
            {
                try
                {
                    foreach (var seriesDir in Directory.GetDirectories(scanDir))
                    {
                        var strmCount = Directory.GetFiles(seriesDir, "*.strm", SearchOption.AllDirectories).Length;
                        existingSeriesFolderCounts.TryAdd(seriesDir, strmCount);

                        // Build O(1) lookup: key = "parentDir|baseName" (strip " [...]" suffix)
                        var folderName = Path.GetFileName(seriesDir);
                        var bracketIndex = folderName.LastIndexOf(" [", StringComparison.Ordinal);
                        var baseKey = bracketIndex > 0 ? folderName[..bracketIndex] : folderName;
                        var parentDir = Path.GetDirectoryName(seriesDir)!;
                        var lookupKey = parentDir + "|" + baseKey;
                        seriesFolderLookup.TryAdd(lookupKey, (seriesDir, strmCount));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error scanning series directory: {Dir}", scanDir);
                }
            }

            _logger.LogInformation("Found {Count} existing series folders", existingSeriesFolderCounts.Count);
        }

        int preApiSkipped = 0;

        // Process categories in batches to reduce memory usage
        for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchCategories = categories
                .Skip(batchIndex * batchSize)
                .Take(batchSize)
                .ToList();

            _logger.LogInformation(
                "Processing series batch {Batch}/{Total} ({Count} categories)...",
                batchIndex + 1,
                totalBatches,
                batchCategories.Count);

            CurrentProgress.SeriesPhase = $"Collecting series (batch {batchIndex + 1}/{totalBatches})";

            // Cache for directory scans (per batch to limit memory)
            var directoryCache = new ConcurrentDictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            // Collect series from this batch of categories
            var batchSeries = new List<(Series Series, HashSet<int> CategoryIds)>();
            var batchCategoryMap = new ConcurrentDictionary<int, HashSet<int>>();
            var seriesBag = new ConcurrentBag<(Series Series, int CategoryId)>();

            await Parallel.ForEachAsync(
                batchCategories,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (category, ct) =>
                {
                    try
                    {
                        var seriesList = await _client.GetSeriesByCategoryAsync(connectionInfo, category.CategoryId, ct)
                            .ConfigureAwait(false);
                        foreach (var series in seriesList)
                        {
                            seriesBag.Add((series, category.CategoryId));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch series for category {CategoryId} ({CategoryName})", category.CategoryId, category.CategoryName);
                        Interlocked.Increment(ref errors);
                    }
                }).ConfigureAwait(false);

            // Process collected series to build category membership
            foreach (var (series, categoryId) in seriesBag)
            {
                if (processedSeriesIds.TryAdd(series.SeriesId, true))
                {
                    var categorySet = new HashSet<int> { categoryId };
                    batchCategoryMap[series.SeriesId] = categorySet;
                    batchSeries.Add((series, categorySet));
                }
                else if (batchCategoryMap.TryGetValue(series.SeriesId, out var existingCategories))
                {
                    lock (existingCategories)
                    {
                        existingCategories.Add(categoryId);
                    }
                }
            }

            _logger.LogInformation("Batch {Batch}: Found {Count} unique series", batchIndex + 1, batchSeries.Count);

            // Track all collected series for snapshot building
            foreach (var (series, _) in batchSeries)
            {
                allCollectedSeries.Add(series);
            }

            // Incremental sync: filter to only new/modified series
            if (previousSnapshot != null)
            {
                var originalCount = batchSeries.Count;
                var unchangedSeries = new List<(Series Series, HashSet<int> CategoryIds)>();
                batchSeries = batchSeries.Where(s =>
                {
                    if (previousSnapshot.Series.TryGetValue(s.Series.SeriesId, out var prev))
                    {
                        // Use previous episode count for comparison - if LastModified changed,
                        // checksum will differ. This avoids the expensive series info API call.
                        var checksum = SnapshotService.CalculateChecksum(s.Series, prev.EpisodeCount);
                        if (prev.Checksum == checksum)
                        {
                            unchangedSeries.Add(s);
                            return false; // Unchanged
                        }
                    }

                    return true; // New or modified series
                }).ToList();

                // Track existing STRM paths for unchanged series (orphan protection)
                foreach (var s in unchangedSeries)
                {
                    string seriesName = SanitizeFileName(s.Series.Name, config.CustomTitleRemoveTerms);
                    int? year = ExtractYear(s.Series.Name);
                    string baseName = year.HasValue ? $"{seriesName} ({year})" : seriesName;

                    var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var categoryId in s.CategoryIds)
                    {
                        if (folderMappings.TryGetValue(categoryId, out var mappedFolders))
                        {
                            foreach (var folder in mappedFolders)
                            {
                                targetFolders.Add(folder);
                            }
                        }
                    }

                    if (targetFolders.Count == 0)
                    {
                        targetFolders.Add(string.Empty);
                    }

                    foreach (var targetFolder in targetFolders)
                    {
                        string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                            ? seriesPath
                            : Path.Combine(seriesPath, targetFolder);
                        var lookupKey = seriesBasePath + "|" + baseName;
                        if (seriesFolderLookup.TryGetValue(lookupKey, out var match))
                        {
                            try
                            {
                                foreach (var strm in Directory.GetFiles(match.Path, "*.strm", SearchOption.AllDirectories))
                                {
                                    syncedFiles.TryAdd(strm, 0);
                                }
                            }
                            catch (Exception)
                            {
                                // Ignore filesystem errors during orphan protection scan
                            }
                        }
                    }
                }

                var skipped = unchangedSeries.Count;
                if (skipped > 0)
                {
                    _logger.LogInformation("Incremental sync: skipping {Skipped} unchanged series, processing {Count} changed", skipped, batchSeries.Count);
                    Interlocked.Add(ref seriesSkipped, skipped);
                }
            }

            // Pre-fetch series info for series that will likely need processing
            // This separates API latency from file I/O for much faster throughput
            var seriesInfoCache = new ConcurrentDictionary<int, SeriesStreamInfo?>();
            {
                // Determine which series will NOT be smart-skipped (need API call)
                var seriesToPreFetch = batchSeries.Where(s =>
                {
                    if (!config.SmartSkipExisting || hintSnapshot == null)
                    {
                        return true;
                    }

                    if (!hintSnapshot.Series.TryGetValue(s.Series.SeriesId, out var hintEntry) ||
                        hintEntry.EpisodeCount <= 0 ||
                        Math.Abs((s.Series.LastModified - hintEntry.LastModified).TotalSeconds) >= 1)
                    {
                        return true; // Would not be smart-skipped
                    }

                    // Check if all target folders have matching content
                    string sName = SanitizeFileName(s.Series.Name, config.CustomTitleRemoveTerms);
                    int? sYear = ExtractYear(s.Series.Name);
                    string sBase = sYear.HasValue ? $"{sName} ({sYear})" : sName;

                    var checkFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var catId in s.CategoryIds)
                    {
                        if (folderMappings.TryGetValue(catId, out var mapped))
                        {
                            foreach (var f in mapped)
                            {
                                checkFolders.Add(f);
                            }
                        }
                    }

                    if (checkFolders.Count == 0)
                    {
                        checkFolders.Add(string.Empty);
                    }

                    foreach (var folder in checkFolders)
                    {
                        string basePath = string.IsNullOrEmpty(folder) ? seriesPath : Path.Combine(seriesPath, folder);
                        var key = basePath + "|" + sBase;
                        if (!seriesFolderLookup.TryGetValue(key, out var m) || m.Count < hintEntry.EpisodeCount)
                        {
                            return true; // Would not be smart-skipped
                        }
                    }

                    return false; // Would be smart-skipped, no need to pre-fetch
                }).ToList();

                if (seriesToPreFetch.Count > 0)
                {
                    int preFetched = 0;
                    int preFetchTotal = seriesToPreFetch.Count;
                    _logger.LogInformation("Pre-fetching series info for {Count} series (batch {Batch}/{Total})...", preFetchTotal, batchIndex + 1, totalBatches);
                    CurrentProgress.SeriesPhase = $"Fetching series info (batch {batchIndex + 1}/{totalBatches}): 0/{preFetchTotal}";
                    await Parallel.ForEachAsync(
                        seriesToPreFetch,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelism,
                            CancellationToken = cancellationToken,
                        },
                        async (seriesEntry, ct) =>
                        {
                            try
                            {
                                var info = await _client.GetSeriesStreamsBySeriesAsync(connectionInfo, seriesEntry.Series.SeriesId, ct).ConfigureAwait(false);
                                seriesInfoCache[seriesEntry.Series.SeriesId] = info;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to pre-fetch series info: {SeriesId}", seriesEntry.Series.SeriesId);
                            }
                            finally
                            {
                                var count = Interlocked.Increment(ref preFetched);
                                string name = SanitizeFileName(seriesEntry.Series.Name, config.CustomTitleRemoveTerms);
                                CurrentProgress.SeriesPhase = $"Fetching series info (batch {batchIndex + 1}/{totalBatches}): {name} ({count}/{preFetchTotal})";
                            }
                        }).ConfigureAwait(false);

                    _logger.LogInformation("Pre-fetched {Cached}/{Total} series infos (batch {Batch}/{TotalBatches})", seriesInfoCache.Count, preFetchTotal, batchIndex + 1, totalBatches);
                }
            }

            CurrentProgress.SeriesPhase = $"Syncing Series (batch {batchIndex + 1}/{totalBatches})";
            CurrentProgress.AddTotalItems(batchSeries.Count);

            // Process series in this batch
            await Parallel.ForEachAsync(
                batchSeries,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken,
                },
                async (seriesEntry, ct) =>
                {
                var series = seriesEntry.Series;
                var categoryIds = seriesEntry.CategoryIds;

                try
                {
                    string seriesName = SanitizeFileName(series.Name, config.CustomTitleRemoveTerms);
                    int? year = ExtractYear(series.Name);
                    string baseName = year.HasValue ? $"{seriesName} ({year})" : seriesName;

                    CurrentProgress.SeriesPhase = $"Syncing Series (batch {batchIndex + 1}/{totalBatches}): {baseName}";

                    // Pre-API smart skip: use snapshot hints to avoid expensive API call
                    // Check if all target folders already have this series with matching episode count
                    // Also verify LastModified hasn't changed to catch new episodes
                    // Compare at second precision since LastModified comes from Unix timestamp
                    if (config.SmartSkipExisting && hintSnapshot != null &&
                        hintSnapshot.Series.TryGetValue(series.SeriesId, out var hintEntry) &&
                        hintEntry.EpisodeCount > 0 &&
                        Math.Abs((series.LastModified - hintEntry.LastModified).TotalSeconds) < 1)
                    {
                        // Determine target folders for this series
                        var preCheckFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var categoryId in categoryIds)
                        {
                            if (folderMappings.TryGetValue(categoryId, out var mappedFolders))
                            {
                                foreach (var folder in mappedFolders)
                                {
                                    preCheckFolders.Add(folder);
                                }
                            }
                        }

                        if (preCheckFolders.Count == 0)
                        {
                            preCheckFolders.Add(string.Empty);
                        }

                        bool allFoldersComplete = true;
                        foreach (var targetFolder in preCheckFolders)
                        {
                            string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                                ? seriesPath
                                : Path.Combine(seriesPath, targetFolder);

                            // Find matching folder via O(1) lookup (baseName may have [tmdbid-X]/[tvdbid-X] suffix)
                            bool foundMatch = false;
                            var lookupKey = seriesBasePath + "|" + baseName;
                            if (seriesFolderLookup.TryGetValue(lookupKey, out var match) &&
                                match.Count >= hintEntry.EpisodeCount)
                            {
                                foundMatch = true;

                                // Add existing files to synced set (for orphan protection)
                                try
                                {
                                    foreach (var strm in Directory.GetFiles(match.Path, "*.strm", SearchOption.AllDirectories))
                                    {
                                        syncedFiles.TryAdd(strm, 0);
                                    }
                                }
                                catch (Exception)
                                {
                                    // Ignore filesystem errors during orphan protection scan
                                }
                            }

                            if (!foundMatch)
                            {
                                allFoldersComplete = false;
                                break;
                            }
                        }

                        if (allFoldersComplete)
                        {
                            // All folders have complete content - skip the expensive API call
                            Interlocked.Increment(ref seriesSkipped);
                            Interlocked.Increment(ref smartSkipped);
                            Interlocked.Increment(ref preApiSkipped);

                            // Re-use snapshot data for snapshot building (avoid API call)
                            allCollectedSeries.Add(series);
                            return;
                        }
                    }

                    // Fetch series info to get provider TMDB ID and episodes (use pre-fetched cache if available)
                    SeriesStreamInfo seriesInfo;
                    if (seriesInfoCache.TryGetValue(series.SeriesId, out var cachedInfo) && cachedInfo != null)
                    {
                        seriesInfo = cachedInfo;
                    }
                    else
                    {
                        seriesInfo = await _client.GetSeriesStreamsBySeriesAsync(connectionInfo, series.SeriesId, ct).ConfigureAwait(false);
                    }

                    // Track for snapshot building
                    allSeriesInfoDict[series.SeriesId] = seriesInfo;

                    // Early return if no episodes
                    if (seriesInfo.Episodes == null || seriesInfo.Episodes.Count == 0)
                    {
                        Interlocked.Increment(ref seriesSkipped);
                        // Note: ItemsProcessed is incremented in finally block
                        return;
                    }

                    // Try to get TMDB ID from provider
                    int? providerTmdbId = null;
                    if (!string.IsNullOrEmpty(seriesInfo.Info?.Tmdb) && int.TryParse(seriesInfo.Info.Tmdb, out int tmdbParsed))
                    {
                        providerTmdbId = tmdbParsed;
                    }

                    // Only do TVDB lookup if provider doesn't have TMDB ID and metadata lookup is enabled
                    int? autoLookupTvdbId = null;
                    if (!providerTmdbId.HasValue && enableMetadataLookup && !tvdbOverrides.ContainsKey(baseName))
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            autoLookupTvdbId = await _metadataLookup.LookupSeriesTvdbIdAsync(seriesName, year, timeoutCts.Token).ConfigureAwait(false);
                            if (!autoLookupTvdbId.HasValue)
                            {
                                Interlocked.Increment(ref unmatchedCount);
                                unmatchedSeries.Add(baseName);
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _logger.LogWarning("Metadata lookup timed out for series: {SeriesName}", seriesName);
                            Interlocked.Increment(ref unmatchedCount);
                            unmatchedSeries.Add(baseName);
                        }
                    }

                    string seriesFolderName = BuildSeriesFolderName(seriesName, year, tvdbOverrides, providerTmdbId, autoLookupTvdbId);

                    // Determine target folders based on category mappings
                    var targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var categoryId in categoryIds)
                    {
                        if (folderMappings.TryGetValue(categoryId, out var mappedFolders))
                        {
                            foreach (var folder in mappedFolders)
                            {
                                targetFolders.Add(folder);
                            }
                        }
                    }

                    // If no folder mappings, sync to root series folder
                    if (targetFolders.Count == 0)
                    {
                        targetFolders.Add(string.Empty);
                    }

                    // Smart skip check with exact folder name (skip when no existing folders - initial scan)
                    if (config.SmartSkipExisting && existingSeriesFolderCounts.Count > 0)
                    {
                        bool anyNeedsSync = false;
                        foreach (var targetFolder in targetFolders)
                        {
                            string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                                ? seriesPath
                                : Path.Combine(seriesPath, targetFolder);
                            string seriesFolderPath = Path.Combine(seriesBasePath, seriesFolderName);

                            if (!Directory.Exists(seriesFolderPath))
                            {
                                anyNeedsSync = true;
                                break;
                            }

                            // Use cached directory scan to avoid redundant I/O
                            var existingStrms = directoryCache.GetOrAdd(
                                seriesFolderPath,
                                path => Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories));

                            if (existingStrms.Length == 0)
                            {
                                anyNeedsSync = true;
                                break;
                            }

                            // Check if provider has more episodes than we have locally
                            int providerEpisodeCount = seriesInfo.Episodes.Values.Sum(seasonEps => seasonEps.Count);
                            if (existingStrms.Length < providerEpisodeCount)
                            {
                                anyNeedsSync = true;
                                break;
                            }

                            // Add existing files to synced set (for orphan protection)
                            foreach (var strm in existingStrms)
                            {
                                syncedFiles.TryAdd(strm, 0);
                            }
                        }

                        if (!anyNeedsSync)
                        {
                            // All target folders have existing content - count as smart skipped
                            foreach (var targetFolder in targetFolders)
                            {
                                string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                                    ? seriesPath
                                    : Path.Combine(seriesPath, targetFolder);
                                string seriesFolderPath = Path.Combine(seriesBasePath, seriesFolderName);

                                var existingStrms = directoryCache.GetOrAdd(
                                    seriesFolderPath,
                                    path => Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories));
                                var existingSeasonsCount = Directory.GetDirectories(seriesFolderPath, "Season *").Length;
                                Interlocked.Add(ref seasonsSkipped, existingSeasonsCount);
                                Interlocked.Add(ref episodesSkipped, existingStrms.Length);
                            }

                            Interlocked.Increment(ref seriesSkipped);
                            Interlocked.Increment(ref smartSkipped);
                            // Note: ItemsProcessed is incremented in finally block
                            return;
                        }
                    }

                    bool seriesHasNewEpisodes = false;
                    var pendingImageDownloads = new List<(string Url, string Path)>();

                    // Sync to each target folder
                    foreach (var targetFolder in targetFolders)
                    {
                        string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                            ? seriesPath
                            : Path.Combine(seriesPath, targetFolder);
                        string seriesFolderPath = Path.Combine(seriesBasePath, seriesFolderName);
                        bool isNewSeries = !Directory.Exists(seriesFolderPath);

                        foreach (var seasonEntry in seriesInfo.Episodes)
                        {
                            int seasonNumber = seasonEntry.Key;
                            var episodes = seasonEntry.Value;
                            string seasonFolder = Path.Combine(seriesFolderPath, $"Season {seasonNumber}");
                            bool isNewSeason = !Directory.Exists(seasonFolder);

                            bool seasonHasNewEpisodes = false;

                            foreach (var episode in episodes)
                            {
                                string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode, config.CustomTitleRemoveTerms);
                                string strmPath = Path.Combine(seasonFolder, episodeFileName);

                                syncedFiles.TryAdd(strmPath, 0);

                                // Build stream URL
                                string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                                string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

                                if (File.Exists(strmPath))
                                {
                                    if (StrmContentMatches(strmPath, streamUrl))
                                    {
                                        Interlocked.Increment(ref episodesSkipped);
                                        continue;
                                    }

                                    // Stream URL changed, update the STRM file
                                    await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                                    Interlocked.Increment(ref episodesUpdated);
                                    continue;
                                }

                                // Create season folder
                                Directory.CreateDirectory(seasonFolder);

                                // Write STRM file
                                try
                                {
                                    await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                                    Interlocked.Increment(ref episodesCreated);
                                    seasonHasNewEpisodes = true;
                                    seriesHasNewEpisodes = true;
                                }
                                catch (IOException) when (File.Exists(strmPath))
                                {
                                    // File was created by another thread/process, skip
                                    continue;
                                }

                                // Write NFO with media info if enabled (only for first target folder)
                                if (enableProactiveMediaInfo && targetFolders.First() == targetFolder && episode.Info != null)
                                {
                                    var nfoFileName = Path.GetFileNameWithoutExtension(episodeFileName) + ".nfo";
                                    var nfoPath = Path.Combine(seasonFolder, nfoFileName);
                                    await NfoWriter.WriteEpisodeNfoAsync(
                                        nfoPath,
                                        seriesName,
                                        seasonNumber,
                                        episode.EpisodeNum,
                                        episode.Title,
                                        episode.Info.Video,
                                        episode.Info.Audio,
                                        episode.Info.DurationSecs,
                                        ct).ConfigureAwait(false);
                                }

                                // Collect episode thumbnail for batch download
                                if (!providerTmdbId.HasValue && !autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched)
                                {
                                    var episodeThumbUrl = episode.Info?.MovieImage;
                                    if (!string.IsNullOrEmpty(episodeThumbUrl))
                                    {
                                        var episodeThumbName = Path.GetFileNameWithoutExtension(episodeFileName);
                                        var thumbExt = GetImageExtension(episodeThumbUrl);
                                        var thumbPath = Path.Combine(seasonFolder, $"{episodeThumbName}-thumb{thumbExt}");
                                        pendingImageDownloads.Add((episodeThumbUrl, thumbPath));
                                    }
                                }
                            }

                            // Track season created/skipped (only count each season once)
                            if (processedSeasons.TryAdd(seasonFolder, true))
                            {
                                if (isNewSeason && seasonHasNewEpisodes)
                                {
                                    Interlocked.Increment(ref seasonsCreated);
                                }
                                else
                                {
                                    Interlocked.Increment(ref seasonsSkipped);
                                }
                            }

                            // Collect season poster for batch download
                            if (!autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched && isNewSeason && seasonHasNewEpisodes)
                            {
                                var season = seriesInfo.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                                var seasonCoverUrl = season?.CoverBig ?? season?.Cover;
                                if (!string.IsNullOrEmpty(seasonCoverUrl))
                                {
                                    var seasonPosterExt = GetImageExtension(seasonCoverUrl);
                                    var seasonPosterPath = Path.Combine(seasonFolder, $"poster{seasonPosterExt}");
                                    pendingImageDownloads.Add((seasonCoverUrl, seasonPosterPath));
                                }
                            }
                        }

                        // Collect series artwork for batch download
                        if (!autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched && seriesHasNewEpisodes && isNewSeries)
                        {
                            if (!string.IsNullOrEmpty(series.Cover))
                            {
                                var posterExt = GetImageExtension(series.Cover);
                                var posterPath = Path.Combine(seriesFolderPath, $"poster{posterExt}");
                                pendingImageDownloads.Add((series.Cover, posterPath));
                            }

                            if (series.BackdropPaths.Count > 0)
                            {
                                var backdropUrl = series.BackdropPaths.First();
                                var fanartExt = GetImageExtension(backdropUrl);
                                var fanartPath = Path.Combine(seriesFolderPath, $"fanart{fanartExt}");
                                pendingImageDownloads.Add((backdropUrl, fanartPath));
                            }
                        }
                    }

                    // Batch download all collected images with bounded parallelism
                    if (pendingImageDownloads.Count > 0)
                    {
                        await Parallel.ForEachAsync(
                            pendingImageDownloads,
                            new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
                            async (img, t) => await DownloadImageAsync(img.Url, img.Path, t).ConfigureAwait(false)).ConfigureAwait(false);
                    }

                    // Track series created/skipped (once per series, not per folder)
                    if (seriesHasNewEpisodes)
                    {
                        Interlocked.Increment(ref seriesCreated);
                    }
                    else
                    {
                        Interlocked.Increment(ref seriesSkipped);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync series: {SeriesName}", series.Name);
                    Interlocked.Increment(ref errors);
                    Interlocked.Increment(ref seriesSkipped);
                    failedItems.Add(new FailedItem
                    {
                        ItemType = "Series",
                        ItemId = series.SeriesId,
                        Name = series.Name,
                        ErrorMessage = ex.Message,
                    });
                }
                finally
                {
                    CurrentProgress.IncrementItemsProcessed();
                    CurrentProgress.EpisodesCreated = episodesCreated;
                    CurrentProgress.EpisodesUpdated = episodesUpdated;
                }
            }).ConfigureAwait(false);

            // Update batch progress
            CurrentProgress.IncrementCategoriesProcessed();

            // Allow GC to reclaim batch memory
            batchSeries.Clear();
            directoryCache.Clear();
            seriesBag = null!;
        } // End of batch loop

        // Update result with thread-safe counters
        result.SeriesCreated += seriesCreated;
        result.SeriesSkipped += seriesSkipped;
        result.SeasonsCreated += seasonsCreated;
        result.SeasonsSkipped += seasonsSkipped;
        result.EpisodesCreated += episodesCreated;
        result.EpisodesUpdated += episodesUpdated;
        result.EpisodesSkipped += episodesSkipped;
        result.AddErrors(errors);
        result.AddFailedItems(failedItems);
        result.SeriesUnmatched = unmatchedCount;

        if (smartSkipped > 0)
        {
            _logger.LogInformation(
                "Smart-skipped {Count} series ({PreApi} before API call, {PostApi} after API call)",
                smartSkipped,
                preApiSkipped,
                smartSkipped - preApiSkipped);
        }

        // Log unmatched series
        if (unmatchedCount > 0)
        {
            _logger.LogInformation("Series without TVDb match: {Count} items", unmatchedCount);
            foreach (var series in unmatchedSeries.OrderBy(s => s))
            {
                _logger.LogInformation("  Unmatched series: {SeriesName}", series);
            }
        }

        CurrentProgress.SeriesPhase = string.Empty;
    }

    private async Task SyncSingleSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        Series series,
        ConcurrentDictionary<string, byte> syncedFiles,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        // This method is now only used as fallback - main logic moved to parallel SyncSeriesAsync
        var seriesInfo = await _client.GetSeriesStreamsBySeriesAsync(connectionInfo, series.SeriesId, cancellationToken).ConfigureAwait(false);

        if (seriesInfo.Episodes == null || seriesInfo.Episodes.Count == 0)
        {
            _logger.LogDebug("Series {SeriesName} has no episodes, skipping", series.Name);
            return;
        }

        var customTerms = Plugin.Instance.Configuration.CustomTitleRemoveTerms;
        string seriesName = SanitizeFileName(series.Name, customTerms);
        int? year = ExtractYear(series.Name);

        string seriesFolderName = year.HasValue ? $"{seriesName} ({year})" : seriesName;
        string seriesFolder = Path.Combine(seriesPath, seriesFolderName);

        foreach (var seasonEntry in seriesInfo.Episodes)
        {
            int seasonNumber = seasonEntry.Key;
            var episodes = seasonEntry.Value;

            string seasonFolder = Path.Combine(seriesFolder, $"Season {seasonNumber}");

            foreach (var episode in episodes)
            {
                try
                {
                    string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode, customTerms);
                    string strmPath = Path.Combine(seasonFolder, episodeFileName);

                    syncedFiles.TryAdd(strmPath, 0);

                    // Build stream URL
                    string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                    string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

                    if (File.Exists(strmPath))
                    {
                        if (StrmContentMatches(strmPath, streamUrl))
                        {
                            result.EpisodesSkipped++;
                            continue;
                        }

                        // Stream URL changed, update the STRM file
                        await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
                        result.EpisodesUpdated++;
                        continue;
                    }

                    // Create season folder
                    Directory.CreateDirectory(seasonFolder);

                    // Write STRM file
                    await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
                    result.EpisodesCreated++;

                    _logger.LogDebug("Created episode STRM: {StrmPath}", strmPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create STRM for episode: {SeriesName} S{Season}E{Episode}", series.Name, seasonNumber, episode.EpisodeNum);
                    result.Errors++;
                }
            }
        }
    }

    internal static string BuildEpisodeFileName(string seriesName, int seasonNumber, Episode episode, string? customRemoveTerms = null)
    {
        string episodeTitle = SanitizeFileName(episode.Title, customRemoveTerms);
        string seasonStr = seasonNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        string episodeStr = episode.EpisodeNum.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(episodeTitle) || episodeTitle.Equals($"Episode {episode.EpisodeNum}", StringComparison.OrdinalIgnoreCase))
        {
            return $"{seriesName} - S{seasonStr}E{episodeStr}.strm";
        }

        return $"{seriesName} - S{seasonStr}E{episodeStr} - {episodeTitle}.strm";
    }

    internal static string SanitizeFileName(string? name, string? customRemoveTerms = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }

        string cleanName = name;

        // Apply user-defined custom removal terms first
        if (!string.IsNullOrWhiteSpace(customRemoveTerms))
        {
            foreach (var term in ChannelNameCleaner.ParseUserTerms(customRemoveTerms))
            {
                cleanName = cleanName.Replace(term, string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Remove prefix language tags like "┃NL┃" or "| NL |" at start of name
        cleanName = PrefixLanguageTagPattern().Replace(cleanName, string.Empty);

        // Remove language/country tags like "| NL |", "┃NL┃", "[NL]", "| EN |", etc.
        cleanName = LanguageTagPattern().Replace(cleanName, string.Empty);

        // Remove language phrases like "(NL GESPROKEN)", "(EN SPOKEN)", "(DUBBED)", "[NL Gesproken]", "[NL Gepsroken]", etc.
        cleanName = LanguagePhrasePattern().Replace(cleanName, string.Empty);

        // Remove bracketed content with Asian characters (Japanese/Chinese original titles)
        cleanName = AsianBracketedTextPattern().Replace(cleanName, string.Empty);

        // Remove codec tags like "HEVC", "x264", "x265", "H.264", "AVC", etc.
        cleanName = CodecTagPattern().Replace(cleanName, string.Empty);

        // Remove quality tags like "4K", "1080p", "720p", "HDR", "UHD", etc.
        cleanName = QualityTagPattern().Replace(cleanName, string.Empty);

        // Remove source tags like "BluRay", "WEBRip", "HDTV", etc.
        cleanName = SourceTagPattern().Replace(cleanName, string.Empty);

        // Remove empty brackets left after tag stripping (e.g., "Movie [4K]" → "Movie []" → "Movie")
        cleanName = EmptyBracketsPattern().Replace(cleanName, string.Empty);

        // Remove year from name if present (we'll add it back in folder name format)
        cleanName = YearPattern().Replace(cleanName, string.Empty);

        // Fix malformed quotes/apostrophes (e.g., "Angela'\'s" -> "Angela's")
        cleanName = MalformedQuotePattern().Replace(cleanName, "'");

        // Remove invalid file name characters
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            cleanName = cleanName.Replace(c, '_');
        }

        // Clean up whitespace and underscores
        cleanName = MultipleSpacesPattern().Replace(cleanName, " ");
        cleanName = MultipleUnderscoresPattern().Replace(cleanName, "_");
        cleanName = cleanName.Trim('_', ' ', '-');

        return string.IsNullOrEmpty(cleanName) ? "Unknown" : cleanName;
    }

    internal static string? ExtractVersionLabel(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Strip prefix language tags first (same as SanitizeFileName step 1)
        string cleanName = PrefixLanguageTagPattern().Replace(name, string.Empty);

        var labels = new List<string>();

        foreach (Match match in CodecTagPattern().Matches(cleanName))
        {
            labels.Add(match.Value);
        }

        foreach (Match match in QualityTagPattern().Matches(cleanName))
        {
            labels.Add(match.Value);
        }

        foreach (Match match in SourceTagPattern().Matches(cleanName))
        {
            labels.Add(match.Value);
        }

        return labels.Count > 0 ? string.Join(" ", labels) : null;
    }

    internal static string BuildMovieStrmFileName(string folderName, string? versionLabel)
    {
        return versionLabel != null
            ? $"{folderName} - {versionLabel}.strm"
            : $"{folderName}.strm";
    }

    internal static int? ExtractYear(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var match = YearPattern().Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int year))
        {
            // Sanity check: year should be between 1900 and current year + 5
            if (year >= 1900 && year <= DateTime.Now.Year + 5)
            {
                return year;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses folder mapping configuration into a reverse lookup (category ID → folder names).
    /// </summary>
    /// <param name="config">The configuration string with one mapping per line.</param>
    /// <returns>Dictionary mapping category IDs to list of folder names.</returns>
    internal static Dictionary<int, List<string>> ParseFolderMappings(string? config)
    {
        var result = new Dictionary<int, List<string>>();
        if (string.IsNullOrWhiteSpace(config))
        {
            return result;
        }

        foreach (string line in config.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equalsIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0 && equalsIndex < line.Length - 1)
            {
                string folderName = line[..equalsIndex].Trim();
                string categoryIdsStr = line[(equalsIndex + 1)..].Trim();

                if (string.IsNullOrEmpty(folderName))
                {
                    continue;
                }

                foreach (string idStr in categoryIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(idStr, out int categoryId))
                    {
                        if (!result.TryGetValue(categoryId, out var folders))
                        {
                            folders = new List<string>();
                            result[categoryId] = folders;
                        }

                        if (!folders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                        {
                            folders.Add(folderName);
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Parses folder ID override configuration string into a dictionary.
    /// </summary>
    /// <param name="config">The configuration string with one mapping per line.</param>
    /// <returns>Dictionary mapping folder names to provider IDs.</returns>
    internal static Dictionary<string, int> ParseFolderIdOverrides(string? config)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(config))
        {
            return result;
        }

        foreach (string line in config.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equalsIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0 && equalsIndex < line.Length - 1)
            {
                string folderName = line[..equalsIndex].Trim();
                string idStr = line[(equalsIndex + 1)..].Trim();
                if (!string.IsNullOrEmpty(folderName) && int.TryParse(idStr, out int id))
                {
                    result[folderName] = id;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a movie folder name, optionally with TMDb ID suffix.
    /// </summary>
    /// <param name="sanitizedName">The sanitized movie name.</param>
    /// <param name="year">Optional release year.</param>
    /// <param name="overrides">Dictionary of folder name to TMDb ID overrides.</param>
    /// <param name="providerTmdbId">Optional TMDB ID from provider (Xtream API).</param>
    /// <param name="autoLookupTmdbId">Optional TMDb ID from automatic lookup.</param>
    /// <returns>Folder name, with [tmdbid-X] suffix if ID exists.</returns>
    internal static string BuildMovieFolderName(string sanitizedName, int? year, Dictionary<string, int> overrides, int? providerTmdbId = null, int? autoLookupTmdbId = null)
    {
        string baseName = year.HasValue ? $"{sanitizedName} ({year})" : sanitizedName;

        // Priority: manual override > provider TMDB > auto-lookup > no ID
        if (overrides.TryGetValue(baseName, out int tmdbId))
        {
            return $"{baseName} [tmdbid-{tmdbId}]";
        }

        if (providerTmdbId.HasValue)
        {
            return $"{baseName} [tmdbid-{providerTmdbId.Value}]";
        }

        if (autoLookupTmdbId.HasValue)
        {
            return $"{baseName} [tmdbid-{autoLookupTmdbId.Value}]";
        }

        return baseName;
    }

    /// <summary>
    /// Builds a series folder name, optionally with TMDB or TVDb ID suffix.
    /// </summary>
    /// <param name="sanitizedName">The sanitized series name.</param>
    /// <param name="year">Optional premiere year.</param>
    /// <param name="overrides">Dictionary of folder name to TVDb ID overrides.</param>
    /// <param name="providerTmdbId">Optional TMDB ID from provider (Xtream API).</param>
    /// <param name="autoLookupTvdbId">Optional TVDb ID from automatic lookup.</param>
    /// <returns>Folder name, with [tmdbid-X] or [tvdbid-X] suffix if ID exists.</returns>
    internal static string BuildSeriesFolderName(string sanitizedName, int? year, Dictionary<string, int> overrides, int? providerTmdbId = null, int? autoLookupTvdbId = null)
    {
        string baseName = year.HasValue ? $"{sanitizedName} ({year})" : sanitizedName;

        // Priority: manual override > provider TMDB > TVDB lookup > no ID
        if (overrides.TryGetValue(baseName, out int tvdbId))
        {
            return $"{baseName} [tvdbid-{tvdbId}]";
        }

        if (providerTmdbId.HasValue)
        {
            return $"{baseName} [tmdbid-{providerTmdbId.Value}]";
        }

        if (autoLookupTvdbId.HasValue)
        {
            return $"{baseName} [tvdbid-{autoLookupTvdbId.Value}]";
        }

        return baseName;
    }

    private static bool StrmContentMatches(string strmPath, string expectedUrl)
    {
        try
        {
            return string.Equals(File.ReadAllText(strmPath).TrimEnd(), expectedUrl, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void CollectExistingStrmFiles(string basePath, HashSet<string> files)
    {
        if (!Directory.Exists(basePath))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(basePath, "*.strm", SearchOption.AllDirectories))
            {
                files.Add(file);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory was deleted during enumeration
        }
        catch (IOException)
        {
            // I/O error during enumeration (permissions, corrupted filesystem, etc.)
        }
    }

    internal static void CleanupEmptyDirectories(string directory, string stopAt, string seriesPath, SyncResult result)
    {
        while (!string.IsNullOrEmpty(directory) &&
               !directory.Equals(stopAt, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(directory))
        {
            if (Directory.GetFileSystemEntries(directory).Length == 0)
            {
                try
                {
                    // Check if this is a season folder (parent is a series folder)
                    string? parentDir = Path.GetDirectoryName(directory);
                    string folderName = Path.GetFileName(directory);

                    if (parentDir != null &&
                        Path.GetDirectoryName(parentDir)?.Equals(seriesPath, StringComparison.OrdinalIgnoreCase) == true &&
                        folderName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
                    {
                        result.SeasonsDeleted++;
                    }
                    else if (parentDir != null &&
                             parentDir.Equals(seriesPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // This is a series folder (direct child of seriesPath)
                        result.SeriesDeleted++;
                    }

                    Directory.Delete(directory);
                    directory = parentDir!;
                }
                catch
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }
    }

    private Task TriggerLibraryScanAsync()
    {
        // Use the library manager to trigger a scan
        // This triggers an async scan of all libraries
        _libraryManager.QueueLibraryScan();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Downloads an image from a URL and saves it to the specified path.
    /// </summary>
    /// <param name="imageUrl">The URL of the image to download.</param>
    /// <param name="destinationPath">The local path to save the image.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download was successful, false otherwise.</returns>
    private async Task<bool> DownloadImageAsync(string? imageUrl, string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            return false;
        }

        // Skip if file already exists
        if (File.Exists(destinationPath))
        {
            return true;
        }

        try
        {
            using var response = await ImageHttpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Save image
            var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(destinationPath, imageBytes, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to download image from {Url}", imageUrl);
            return false;
        }
    }

    /// <summary>
    /// Gets the file extension from an image URL, defaulting to .jpg.
    /// </summary>
    private static string GetImageExtension(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            return ".jpg";
        }

        var uri = new Uri(imageUrl, UriKind.RelativeOrAbsolute);
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : imageUrl;
        var ext = Path.GetExtension(path)?.ToLowerInvariant();

        return ext switch
        {
            ".png" => ".png",
            ".webp" => ".webp",
            ".gif" => ".gif",
            _ => ".jpg"
        };
    }

    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"_+")]
    private static partial Regex MultipleUnderscoresPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesPattern();

    // Matches prefix language tags like "┃NL┃" or "| NL |" at start of name
    [GeneratedRegex(@"^[\|\┃]\s*[A-Z]{2,3}\s*[\|\┃]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixLanguageTagPattern();

    // Matches language tags like "| NL |", "┃NL┃", "[NL]", "| EN |", "| DE |", etc.
    [GeneratedRegex(@"[\|\┃\[]\s*[A-Z]{2,3}\s*[\|\┃\]]", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTagPattern();

    // Matches language phrases like "(NL GESPROKEN)", "(EN SPOKEN)", "(DUBBED)", "(OV)", "(SUB)", "[NL Gesproken]", "[NL Gepsroken]", "(DE-)", "(DE -)", etc.
    [GeneratedRegex(@"[\(\[]\s*(?:NL|EN|DE|FR|ES|IT|PT|RU|PL|JP|KR|CN)\s*(?:GESPROKEN|GEPSROKEN|SPOKEN|DUBBED|SUBS?|SUBBED|OV|OmU|AUDIO)?-?\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePhrasePattern();

    // Matches bracketed content containing Asian characters (CJK: Chinese, Japanese, Korean)
    [GeneratedRegex(@"\s*[\[\(][^\]\)]*[\u3000-\u9FFF\uAC00-\uD7AF\u3040-\u309F\u30A0-\u30FF]+[^\]\)]*[\]\)]")]
    private static partial Regex AsianBracketedTextPattern();

    // Matches codec tags like "HEVC", "x264", "x265", "H.264", "H264", "AVC", "10bit", etc.
    [GeneratedRegex(@"\b(?:HEVC|[xh]\.?26[45]|AVC|MPEG-?[24]|VP9|AV1|10-?bit|8-?bit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodecTagPattern();

    // Matches quality tags like "4K", "UHD", "1080p", "720p", "480p", "HDR", "HDR10", "SDR", "2160p", etc.
    [GeneratedRegex(@"\b(?:4K|UHD|2160p|1080[pi]|720p|480p|576p|HDR10\+?|HDR|SDR|FHD|HD|SD)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTagPattern();

    // Matches source tags like "BluRay", "BRRip", "WEBRip", "WEB-DL", "HDTV", "DVDRip", "REMUX", etc.
    [GeneratedRegex(@"\b(?:Blu-?Ray|BRRip|BDRip|WEB-?(?:Rip|DL)?|HDTV|DVDRip|DVD|REMUX|CAM|TS|HC|PROPER|REPACK)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceTagPattern();

    // Fixes malformed quotes like "'\'" "\''" "'\''" "Bob'\''s" to just "'"
    [GeneratedRegex(@"'\\''|'\\'|\\''|''+")]
    private static partial Regex MalformedQuotePattern();

    // Matches empty brackets left after tag stripping, e.g., "[]" or "()"
    [GeneratedRegex(@"\[\s*\]|\(\s*\)")]
    private static partial Regex EmptyBracketsPattern();

    private async Task SaveSnapshotAsync(
        PluginConfiguration config,
        ConcurrentBag<StreamInfo> allMovies,
        ConcurrentBag<Series> allSeries,
        ConcurrentDictionary<int, SeriesStreamInfo> seriesInfoDict,
        IReadOnlyList<FailedItem> failedItems,
        CancellationToken cancellationToken)
    {
        try
        {
            // Exclude failed items from snapshot so they are retried on next sync
            var failedMovieIds = new HashSet<int>(
                failedItems.Where(f => f.ItemType == "Movie").Select(f => f.ItemId));
            var failedSeriesIds = new HashSet<int>(
                failedItems.Where(f => f.ItemType == "Series").Select(f => f.ItemId));

            var snapshot = new ContentSnapshot
            {
                ProviderUrl = config.BaseUrl,
                ConfigFingerprint = SnapshotService.CalculateConfigFingerprint(config)
            };

            // Build movie snapshots
            var processedMovieIds = new HashSet<int>();
            foreach (var movie in allMovies)
            {
                if (processedMovieIds.Add(movie.StreamId) && !failedMovieIds.Contains(movie.StreamId))
                {
                    snapshot.Movies[movie.StreamId] = new MovieSnapshot
                    {
                        StreamId = movie.StreamId,
                        Name = movie.Name,
                        StreamIcon = movie.StreamIcon,
                        ContainerExtension = movie.ContainerExtension,
                        CategoryId = movie.CategoryId ?? 0,
                        Added = movie.Added,
                        Checksum = SnapshotService.CalculateChecksum(movie)
                    };
                }
            }

            // Build series snapshots
            var processedSeriesIds = new HashSet<int>();
            foreach (var series in allSeries)
            {
                if (processedSeriesIds.Add(series.SeriesId) && !failedSeriesIds.Contains(series.SeriesId))
                {
                    var episodeCount = 0;
                    if (seriesInfoDict.TryGetValue(series.SeriesId, out var info) && info.Episodes != null)
                    {
                        episodeCount = info.Episodes.Values.Sum(eps => eps.Count);
                    }

                    snapshot.Series[series.SeriesId] = new SeriesSnapshot
                    {
                        SeriesId = series.SeriesId,
                        Name = series.Name,
                        Cover = series.Cover,
                        CategoryId = series.CategoryId,
                        EpisodeCount = episodeCount,
                        LastModified = series.LastModified,
                        Checksum = SnapshotService.CalculateChecksum(series, episodeCount)
                    };
                }
            }

            snapshot.Metadata = new SnapshotMetadata
            {
                TotalMovies = snapshot.Movies.Count,
                TotalSeries = snapshot.Series.Count,
                IsComplete = true
            };

            await _snapshotService.SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Snapshot saved ({Movies} movies, {Series} series)", snapshot.Movies.Count, snapshot.Series.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save snapshot - next sync will be full");
        }
    }

    private static HttpClient CreateImageHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "Jellyfin-Xtream-Library/1.0");
        return client;
    }
}

/// <summary>
/// Real-time progress of a sync operation.
/// </summary>
public class SyncProgress
{
    private int _itemsProcessed;
    private int _totalItems;
    private int _moviesCreated;
    private int _moviesUpdated;
    private int _episodesCreated;
    private int _episodesUpdated;
    private int _totalCategories;
    private int _categoriesProcessed;

    /// <summary>
    /// Gets or sets a value indicating whether a sync is currently in progress.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current phase of the sync (used for sequential phases like cleanup/retry).
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current movie sync phase (used during concurrent sync).
    /// </summary>
    public string MoviePhase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current series sync phase (used during concurrent sync).
    /// </summary>
    public string SeriesPhase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current item being processed.
    /// </summary>
    public string CurrentItem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total number of categories to process.
    /// </summary>
    public int TotalCategories
    {
        get => Volatile.Read(ref _totalCategories);
        set => Volatile.Write(ref _totalCategories, value);
    }

    /// <summary>
    /// Gets or sets the number of categories processed.
    /// </summary>
    public int CategoriesProcessed
    {
        get => Volatile.Read(ref _categoriesProcessed);
        set => Volatile.Write(ref _categoriesProcessed, value);
    }

    /// <summary>
    /// Gets or sets the total items in the current category.
    /// </summary>
    public int TotalItems
    {
        get => Volatile.Read(ref _totalItems);
        set => Volatile.Write(ref _totalItems, value);
    }

    /// <summary>
    /// Gets or sets the items processed in the current category.
    /// </summary>
    public int ItemsProcessed
    {
        get => Volatile.Read(ref _itemsProcessed);
        set => Volatile.Write(ref _itemsProcessed, value);
    }

    /// <summary>
    /// Gets or sets the number of movies created so far.
    /// </summary>
    public int MoviesCreated
    {
        get => Volatile.Read(ref _moviesCreated);
        set => Volatile.Write(ref _moviesCreated, value);
    }

    /// <summary>
    /// Gets or sets the number of movies updated (STRM content changed) so far.
    /// </summary>
    public int MoviesUpdated
    {
        get => Volatile.Read(ref _moviesUpdated);
        set => Volatile.Write(ref _moviesUpdated, value);
    }

    /// <summary>
    /// Gets or sets the number of episodes created so far.
    /// </summary>
    public int EpisodesCreated
    {
        get => Volatile.Read(ref _episodesCreated);
        set => Volatile.Write(ref _episodesCreated, value);
    }

    /// <summary>
    /// Gets or sets the number of episodes updated (STRM content changed) so far.
    /// </summary>
    public int EpisodesUpdated
    {
        get => Volatile.Read(ref _episodesUpdated);
        set => Volatile.Write(ref _episodesUpdated, value);
    }

    /// <summary>
    /// Gets or sets the start time.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Atomically increments the ItemsProcessed counter.
    /// </summary>
    public void IncrementItemsProcessed()
    {
        Interlocked.Increment(ref _itemsProcessed);
    }

    /// <summary>
    /// Atomically adds to the TotalItems counter.
    /// </summary>
    /// <param name="count">The number of items to add.</param>
    public void AddTotalItems(int count)
    {
        Interlocked.Add(ref _totalItems, count);
    }

    /// <summary>
    /// Atomically increments the MoviesCreated counter.
    /// </summary>
    public void IncrementMoviesCreated()
    {
        Interlocked.Increment(ref _moviesCreated);
    }

    /// <summary>
    /// Atomically increments the EpisodesCreated counter.
    /// </summary>
    public void IncrementEpisodesCreated()
    {
        Interlocked.Increment(ref _episodesCreated);
    }

    /// <summary>
    /// Atomically adds to the TotalCategories counter.
    /// </summary>
    /// <param name="count">The number of categories to add.</param>
    public void AddTotalCategories(int count)
    {
        Interlocked.Add(ref _totalCategories, count);
    }

    /// <summary>
    /// Atomically increments the CategoriesProcessed counter.
    /// </summary>
    public void IncrementCategoriesProcessed()
    {
        Interlocked.Increment(ref _categoriesProcessed);
    }
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public class SyncResult
{
    private readonly List<FailedItem> _failedItems = new();
    private readonly object _failedItemsLock = new();
    private int _errors;

    /// <summary>
    /// Gets or sets the start time of the sync.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time of the sync.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if sync failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the number of movies created.
    /// </summary>
    public int MoviesCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of movies skipped (already existed).
    /// </summary>
    public int MoviesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of movies updated (STRM content changed).
    /// </summary>
    public int MoviesUpdated { get; set; }

    /// <summary>
    /// Gets or sets the number of movies deleted (orphans).
    /// </summary>
    public int MoviesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of movies (created + skipped + updated).
    /// </summary>
    public int TotalMovies => MoviesCreated + MoviesSkipped + MoviesUpdated;

    /// <summary>
    /// Gets or sets the number of series created.
    /// </summary>
    public int SeriesCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of series skipped (already existed).
    /// </summary>
    public int SeriesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of series deleted (orphans).
    /// </summary>
    public int SeriesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of series (created + skipped).
    /// </summary>
    public int TotalSeries => SeriesCreated + SeriesSkipped;

    /// <summary>
    /// Gets or sets the number of seasons created.
    /// </summary>
    public int SeasonsCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons skipped (already existed).
    /// </summary>
    public int SeasonsSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons deleted (orphans).
    /// </summary>
    public int SeasonsDeleted { get; set; }

    /// <summary>
    /// Gets the total number of seasons (created + skipped).
    /// </summary>
    public int TotalSeasons => SeasonsCreated + SeasonsSkipped;

    /// <summary>
    /// Gets or sets the number of episodes created.
    /// </summary>
    public int EpisodesCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes skipped (already existed).
    /// </summary>
    public int EpisodesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes updated (STRM content changed).
    /// </summary>
    public int EpisodesUpdated { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes deleted (orphans).
    /// </summary>
    public int EpisodesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of episodes (created + skipped + updated).
    /// </summary>
    public int TotalEpisodes => EpisodesCreated + EpisodesSkipped + EpisodesUpdated;

    /// <summary>
    /// Gets or sets the number of files deleted (orphans) - legacy, use specific counts.
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the number of errors encountered. Thread-safe for concurrent movie+series sync.
    /// </summary>
    public int Errors
    {
        get => Volatile.Read(ref _errors);
        set => Volatile.Write(ref _errors, value);
    }

    /// <summary>
    /// Gets the list of failed items. Not persisted to disk.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<FailedItem> FailedItems
    {
        get
        {
            lock (_failedItemsLock)
            {
                return _failedItems.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the duration of the sync operation.
    /// </summary>
    [JsonIgnore]
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>
    /// Gets or sets the number of movies that could not be matched to TMDb.
    /// </summary>
    public int MoviesUnmatched { get; set; }

    /// <summary>
    /// Gets or sets the number of series that could not be matched to TVDb.
    /// </summary>
    public int SeriesUnmatched { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this sync was incremental (vs full).
    /// </summary>
    public bool WasIncrementalSync { get; set; }

    /// <summary>
    /// Atomically adds to the Errors counter. Thread-safe for concurrent movie+series sync.
    /// </summary>
    /// <param name="count">The number of errors to add.</param>
    internal void AddErrors(int count) => Interlocked.Add(ref _errors, count);

    /// <summary>
    /// Adds a failed item to the list. Thread-safe.
    /// </summary>
    /// <param name="item">The failed item to add.</param>
    internal void AddFailedItem(FailedItem item)
    {
        lock (_failedItemsLock)
        {
            _failedItems.Add(item);
        }
    }

    /// <summary>
    /// Adds multiple failed items to the list. Thread-safe.
    /// </summary>
    /// <param name="items">The failed items to add.</param>
    internal void AddFailedItems(IEnumerable<FailedItem> items)
    {
        lock (_failedItemsLock)
        {
            _failedItems.AddRange(items);
        }
    }

    /// <summary>
    /// Clears and replaces all failed items.
    /// </summary>
    /// <param name="items">The new list of failed items.</param>
    internal void SetFailedItems(IEnumerable<FailedItem> items)
    {
        lock (_failedItemsLock)
        {
            _failedItems.Clear();
            _failedItems.AddRange(items);
        }
    }
}

/// <summary>
/// Represents an item that failed during sync.
/// </summary>
public class FailedItem
{
    /// <summary>
    /// Gets or sets the type of item (Movie, Series, Episode).
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item ID from the provider.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series ID (for episodes).
    /// </summary>
    public int? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season number (for episodes).
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number (for episodes).
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the time of failure.
    /// </summary>
    public DateTime FailedAt { get; set; } = DateTime.UtcNow;
}
