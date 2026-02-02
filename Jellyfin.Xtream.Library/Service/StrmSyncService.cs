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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service responsible for syncing Xtream content to STRM files.
/// </summary>
public partial class StrmSyncService
{
    private static readonly HttpClient ImageHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IXtreamClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly IMetadataLookupService _metadataLookup;
    private readonly ILogger<StrmSyncService> _logger;
    private CancellationTokenSource? _currentSyncCts;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSyncService"/> class.
    /// </summary>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="logger">The logger instance.</param>
    public StrmSyncService(
        IXtreamClient client,
        ILibraryManager libraryManager,
        IMetadataLookupService metadataLookup,
        ILogger<StrmSyncService> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
        _metadataLookup = metadataLookup;
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the last sync operation.
    /// </summary>
    public SyncResult? LastSyncResult { get; private set; }

    /// <summary>
    /// Gets the current sync progress.
    /// </summary>
    public SyncProgress CurrentProgress { get; } = new SyncProgress();

    /// <summary>
    /// Gets the list of failed items from the last sync that can be retried.
    /// </summary>
    public IReadOnlyList<FailedItem> FailedItems => LastSyncResult?.FailedItems ?? Array.Empty<FailedItem>();

    /// <summary>
    /// Cancels the currently running sync operation, if any.
    /// </summary>
    /// <returns>True if a sync was cancelled, false if no sync was running.</returns>
    public bool CancelSync()
    {
        if (_currentSyncCts != null && !_currentSyncCts.IsCancellationRequested && CurrentProgress.IsRunning)
        {
            _logger.LogInformation("Cancelling sync operation...");
            _currentSyncCts.Cancel();
            return true;
        }

        return false;
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
                    CurrentProgress.ItemsProcessed++;
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

            // Trigger library scan if enabled and items were created
            if (config.TriggerLibraryScan && (result.MoviesCreated > 0 || result.EpisodesCreated > 0))
            {
                _logger.LogInformation("Triggering Jellyfin library scan after retry...");
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
        string movieName = SanitizeFileName(item.Name);
        int? year = ExtractYear(item.Name);
        string folderName = year.HasValue ? $"{movieName} ({year})" : movieName;
        string movieFolder = Path.Combine(moviesPath, folderName);
        string strmFileName = $"{folderName}.strm";
        string strmPath = Path.Combine(movieFolder, strmFileName);

        if (File.Exists(strmPath))
        {
            result.MoviesSkipped++;
            return;
        }

        Directory.CreateDirectory(movieFolder);

        // Build stream URL using stored item ID (assume mp4 as default extension)
        string streamUrl = $"{connectionInfo.BaseUrl}/movie/{connectionInfo.UserName}/{connectionInfo.Password}/{item.ItemId}.mp4";

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

        string seriesName = SanitizeFileName(item.Name);
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
                string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode);
                string strmPath = Path.Combine(seasonFolder, episodeFileName);

                if (File.Exists(strmPath))
                {
                    result.EpisodesSkipped++;
                    continue;
                }

                Directory.CreateDirectory(seasonFolder);

                string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

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
    /// Performs a full sync of all content from the Xtream provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result with statistics.</returns>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var result = new SyncResult { StartTime = DateTime.UtcNow };

        // Create linked cancellation token source for cancel support
        _currentSyncCts?.Dispose();
        _currentSyncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var linkedToken = _currentSyncCts.Token;

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
        CurrentProgress.EpisodesCreated = 0;

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            _logger.LogWarning("Xtream credentials not configured, skipping sync");
            result.Error = "Credentials not configured";
            CurrentProgress.IsRunning = false;
            return result;
        }

        var connectionInfo = Plugin.Instance.Creds;

        _logger.LogInformation("Starting Xtream library sync to {LibraryPath}", config.LibraryPath);

        var existingStrmFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                CollectExistingStrmFiles(config.LibraryPath, existingStrmFiles);
            }

            var syncedFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            // Sync Movies/VOD
            if (config.SyncMovies)
            {
                _logger.LogInformation("Syncing movies/VOD content...");
                CurrentProgress.Phase = "Syncing Movies";
                await SyncMoviesAsync(connectionInfo, moviesPath, syncedFiles, result, linkedToken).ConfigureAwait(false);
            }

            // Sync Series
            if (config.SyncSeries)
            {
                _logger.LogInformation("Syncing series content...");
                CurrentProgress.Phase = "Syncing Series";
                CurrentProgress.CategoriesProcessed = 0;
                CurrentProgress.TotalCategories = 0;
                await SyncSeriesAsync(connectionInfo, seriesPath, syncedFiles, result, linkedToken).ConfigureAwait(false);
            }

            // Cleanup orphaned files
            if (config.CleanupOrphans)
            {
                CurrentProgress.Phase = "Cleaning up orphans";
                CurrentProgress.CurrentItem = string.Empty;
                var orphanedFiles = existingStrmFiles.Except(syncedFiles.Keys, StringComparer.OrdinalIgnoreCase).ToList();

                // Protection: Check if deletion would exceed safety threshold (provider glitch protection)
                const double SafetyThreshold = 0.20; // 20%
                int orphanedMovies = orphanedFiles.Count(f => f.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase));
                int orphanedEpisodes = orphanedFiles.Count(f => f.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase));
                int existingMovieCount = existingStrmFiles.Count(f => f.StartsWith(moviesPath, StringComparison.OrdinalIgnoreCase));
                int existingEpisodeCount = existingStrmFiles.Count(f => f.StartsWith(seriesPath, StringComparison.OrdinalIgnoreCase));

                double movieDeletionRatio = existingMovieCount > 0 ? (double)orphanedMovies / existingMovieCount : 0;
                double episodeDeletionRatio = existingEpisodeCount > 0 ? (double)orphanedEpisodes / existingEpisodeCount : 0;

                bool skipMovieCleanup = existingMovieCount > 10 && movieDeletionRatio > SafetyThreshold;
                bool skipEpisodeCleanup = existingEpisodeCount > 10 && episodeDeletionRatio > SafetyThreshold;

                if (skipMovieCleanup)
                {
                    _logger.LogWarning(
                        "Skipping movie orphan cleanup: {OrphanCount}/{ExistingCount} ({Percent:P0}) exceeds {Threshold:P0} safety threshold - possible provider issue",
                        orphanedMovies,
                        existingMovieCount,
                        movieDeletionRatio,
                        SafetyThreshold);
                }

                if (skipEpisodeCleanup)
                {
                    _logger.LogWarning(
                        "Skipping episode orphan cleanup: {OrphanCount}/{ExistingCount} ({Percent:P0}) exceeds {Threshold:P0} safety threshold - possible provider issue",
                        orphanedEpisodes,
                        existingEpisodeCount,
                        episodeDeletionRatio,
                        SafetyThreshold);
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
                    try
                    {
                        CurrentProgress.ItemsProcessed++;
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

            result.EndTime = DateTime.UtcNow;
            result.Success = true;

            _logger.LogInformation(
                "Sync completed: Movies({MoviesCreated} added, {MoviesSkipped} skipped, {MoviesDeleted} deleted), Series({SeriesCreated} added, {SeriesSkipped} skipped), Episodes({EpisodesCreated} added, {EpisodesSkipped} skipped, {EpisodesDeleted} deleted)",
                result.MoviesCreated,
                result.MoviesSkipped,
                result.MoviesDeleted,
                result.SeriesCreated,
                result.SeriesSkipped,
                result.EpisodesCreated,
                result.EpisodesSkipped,
                result.EpisodesDeleted);

            // Flush metadata cache to disk
            if (config.EnableMetadataLookup)
            {
                await _metadataLookup.FlushCacheAsync().ConfigureAwait(false);
            }

            // Trigger library scan if enabled
            if (config.TriggerLibraryScan && (result.MoviesCreated > 0 || result.EpisodesCreated > 0 || result.FilesDeleted > 0))
            {
                _logger.LogInformation("Triggering Jellyfin library scan...");
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
            catch
            {
                // Ignore flush errors in finally
            }

            CurrentProgress.IsRunning = false;
            if (CurrentProgress.Phase != "Cancelled")
            {
                CurrentProgress.Phase = "Complete";
            }

            // Clean up the CTS
            _currentSyncCts?.Dispose();
            _currentSyncCts = null;
        }

        LastSyncResult = result;
        return result;
    }

    private async Task SyncMoviesAsync(
        ConnectionInfo connectionInfo,
        string moviesPath,
        ConcurrentDictionary<string, byte> syncedFiles,
        SyncResult result,
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

        CurrentProgress.TotalCategories = totalBatches;
        CurrentProgress.CategoriesProcessed = 0;
        CurrentProgress.Phase = "Syncing Movies";

        // Thread-safe counters and collections (shared across batches)
        int moviesCreated = 0;
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

            CurrentProgress.Phase = $"Collecting movies (batch {batchIndex + 1}/{totalBatches})";

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
                    var streams = await _client.GetVodStreamsByCategoryAsync(connectionInfo, category.CategoryId, ct)
                        .ConfigureAwait(false);
                    foreach (var stream in streams)
                    {
                        streamBag.Add((stream, category.CategoryId));
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
            CurrentProgress.Phase = $"Syncing Movies (batch {batchIndex + 1}/{totalBatches})";
            CurrentProgress.TotalItems += batchMovies.Count;

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
                    string movieName = SanitizeFileName(stream.Name);
                    int? year = ExtractYear(stream.Name);
                    string baseName = year.HasValue ? $"{movieName} ({year})" : movieName;

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
                        // New movie - fetch provider TMDB ID
                        if (enableMetadataLookup && !tmdbOverrides.ContainsKey(baseName))
                        {
                            try
                            {
                                vodInfo = await _client.GetVodInfoAsync(connectionInfo, stream.StreamId, ct).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(vodInfo?.Info?.TmdbId) && int.TryParse(vodInfo.Info.TmdbId, out int tmdbParsed))
                                {
                                    providerTmdbId = tmdbParsed;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to fetch VOD info for provider TMDB: {StreamId}", stream.StreamId);
                            }
                        }

                        // Only do metadata lookup if provider doesn't have TMDB ID
                        if (!providerTmdbId.HasValue && enableMetadataLookup && !tmdbOverrides.ContainsKey(baseName))
                        {
                            autoLookupTmdbId = await _metadataLookup.LookupMovieTmdbIdAsync(movieName, year, ct).ConfigureAwait(false);
                            if (!autoLookupTmdbId.HasValue)
                            {
                                Interlocked.Increment(ref unmatchedCount);
                                unmatchedMovies.Add(baseName);
                            }
                        }

                        folderName = BuildMovieFolderName(movieName, year, tmdbOverrides, providerTmdbId, autoLookupTmdbId);
                    }

                    // Build stream URL (same for all copies)
                    string extension = string.IsNullOrEmpty(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension;
                    string streamUrl = $"{connectionInfo.BaseUrl}/movie/{connectionInfo.UserName}/{connectionInfo.Password}/{stream.StreamId}.{extension}";

                    bool anyCreated = false;
                    bool allSkipped = true;

                    // Sync to each target folder
                    foreach (var targetFolder in targetFolders)
                    {
                        string movieBasePath = string.IsNullOrEmpty(targetFolder)
                            ? moviesPath
                            : Path.Combine(moviesPath, targetFolder);
                        string movieFolder = Path.Combine(movieBasePath, folderName);
                        string strmFileName = $"{folderName}.strm";
                        string strmPath = Path.Combine(movieFolder, strmFileName);

                        syncedFiles.TryAdd(strmPath, 0);

                        if (File.Exists(strmPath))
                        {
                            continue;
                        }

                        allSkipped = false;

                        // Create movie folder
                        Directory.CreateDirectory(movieFolder);

                        // Write STRM file
                        await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                        anyCreated = true;

                        // Write NFO if proactive media info enabled (only for first target folder)
                        if (enableProactiveMediaInfo && targetFolders.First() == targetFolder)
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
                                        stream.Name,
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

                        // Download artwork for unmatched movies
                        if (!providerTmdbId.HasValue && !autoLookupTmdbId.HasValue && !tmdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched)
                        {
                            if (!string.IsNullOrEmpty(stream.StreamIcon))
                            {
                                var posterExt = GetImageExtension(stream.StreamIcon);
                                var posterPath = Path.Combine(movieFolder, $"poster{posterExt}");
                                await DownloadImageAsync(stream.StreamIcon, posterPath, ct).ConfigureAwait(false);
                            }
                        }

                        _logger.LogDebug("Created movie STRM: {StrmPath}", strmPath);
                    }

                    if (anyCreated)
                    {
                        Interlocked.Increment(ref moviesCreated);
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
                    CurrentProgress.ItemsProcessed++;
                    CurrentProgress.MoviesCreated = moviesCreated;
                }
            }).ConfigureAwait(false);

            // Update batch progress
            CurrentProgress.CategoriesProcessed = batchIndex + 1;

            // Allow GC to reclaim batch memory
            batchMovies.Clear();
            streamBag = null!;
        } // End of batch loop

        // Update result with thread-safe counters
        result.MoviesCreated += moviesCreated;
        result.MoviesSkipped += moviesSkipped;
        result.Errors += errors;
        result.AddFailedItems(failedItems);

        // Log unmatched movies
        if (unmatchedCount > 0)
        {
            _logger.LogInformation("Movies without TMDb match: {Count} items", unmatchedCount);
            foreach (var movie in unmatchedMovies.OrderBy(m => m))
            {
                _logger.LogInformation("  Unmatched movie: {MovieName}", movie);
            }
        }

        CurrentProgress.CategoriesProcessed = 1;
    }

    private async Task SyncSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        ConcurrentDictionary<string, byte> syncedFiles,
        SyncResult result,
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

        CurrentProgress.Phase = "Syncing Series";
        CurrentProgress.TotalCategories = totalBatches;
        CurrentProgress.CategoriesProcessed = 0;
        CurrentProgress.TotalItems = 0;
        CurrentProgress.ItemsProcessed = 0;

        // Thread-safe counters (shared across batches)
        int seriesCreated = 0;
        int seriesSkipped = 0;
        int seasonsCreated = 0;
        int seasonsSkipped = 0;
        int episodesCreated = 0;
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

        // Pre-scan existing series folders for faster skip detection
        var existingSeriesFolders = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(seriesPath))
        {
            _logger.LogInformation("Pre-scanning existing series folders...");
            CurrentProgress.Phase = "Scanning existing series";

            // Scan root series folder
            var rootFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.GetDirectories(seriesPath))
            {
                var folderName = Path.GetFileName(folder);
                // Extract base name (remove [tmdbid-X] or [tvdbid-X] suffix if present)
                var bracketIndex = folderName.LastIndexOf(" [", StringComparison.Ordinal);
                var baseKey = bracketIndex > 0 ? folderName[..bracketIndex] : folderName;
                rootFolders.TryAdd(baseKey, folderName);
            }

            existingSeriesFolders[string.Empty] = rootFolders;

            // Scan category subfolders if using Multiple folder mode
            if (folderMappings.Count > 0)
            {
                var subfolderNames = folderMappings.Values.SelectMany(v => v).Distinct();
                foreach (var subfolder in subfolderNames)
                {
                    var subPath = Path.Combine(seriesPath, subfolder);
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

                        existingSeriesFolders[subfolder] = subFolders;
                    }
                }
            }

            var totalFolders = existingSeriesFolders.Values.Sum(d => d.Count);
            _logger.LogInformation("Found {Count} existing series folders", totalFolders);
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
                "Processing series batch {Batch}/{Total} ({Count} categories)...",
                batchIndex + 1,
                totalBatches,
                batchCategories.Count);

            CurrentProgress.Phase = $"Collecting series (batch {batchIndex + 1}/{totalBatches})";

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
                    var seriesList = await _client.GetSeriesByCategoryAsync(connectionInfo, category.CategoryId, ct)
                        .ConfigureAwait(false);
                    foreach (var series in seriesList)
                    {
                        seriesBag.Add((series, category.CategoryId));
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
            CurrentProgress.Phase = $"Syncing Series (batch {batchIndex + 1}/{totalBatches})";
            CurrentProgress.TotalItems += batchSeries.Count;

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
                    string seriesName = SanitizeFileName(series.Name);
                    int? year = ExtractYear(series.Name);
                    string baseName = year.HasValue ? $"{seriesName} ({year})" : seriesName;

                    // Determine target folders based on category mappings FIRST (before any API call)
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

                    // Quick check: does a folder for this series already exist? If so, skip API calls
                    // Uses pre-scanned cache for O(1) lookup instead of filesystem scan
                    string? existingFolderName = null;
                    bool existsInAllTargets = true;
                    foreach (var targetFolder in targetFolders)
                    {
                        if (existingSeriesFolders.TryGetValue(targetFolder, out var folderCache) &&
                            folderCache.TryGetValue(baseName, out var cachedFolder))
                        {
                            existingFolderName ??= cachedFolder;
                        }
                        else
                        {
                            existsInAllTargets = false;
                        }
                    }

                    if (existingFolderName != null && existsInAllTargets && config.SmartSkipExisting)
                    {
                        // Series already exists in all target folders - skip API call entirely
                        foreach (var targetFolder in targetFolders)
                        {
                            string seriesBasePath = string.IsNullOrEmpty(targetFolder)
                                ? seriesPath
                                : Path.Combine(seriesPath, targetFolder);

                            // Get cached folder name for this target
                            var cachedName = existingSeriesFolders[targetFolder][baseName];
                            string seriesFolderPath = Path.Combine(seriesBasePath, cachedName);

                            var existingStrms = directoryCache.GetOrAdd(
                                seriesFolderPath,
                                path => Directory.GetFiles(path, "*.strm", SearchOption.AllDirectories));

                            if (existingStrms.Length > 0)
                            {
                                var existingSeasonsCount = Directory.GetDirectories(seriesFolderPath, "Season *").Length;
                                Interlocked.Add(ref seasonsSkipped, existingSeasonsCount);
                                Interlocked.Add(ref episodesSkipped, existingStrms.Length);

                                // Add existing files to synced set (for orphan protection)
                                foreach (var strm in existingStrms)
                                {
                                    syncedFiles.TryAdd(strm, 0);
                                }
                            }
                        }

                        Interlocked.Increment(ref seriesSkipped);
                        Interlocked.Increment(ref smartSkipped);
                        CurrentProgress.ItemsProcessed++;
                        return;
                    }

                    // Need to fetch series info - either new series or partial existence
                    var seriesInfo = await _client.GetSeriesStreamsBySeriesAsync(connectionInfo, series.SeriesId, ct).ConfigureAwait(false);

                    // Early return if no episodes
                    if (seriesInfo.Episodes == null || seriesInfo.Episodes.Count == 0)
                    {
                        CurrentProgress.ItemsProcessed++;
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
                        autoLookupTvdbId = await _metadataLookup.LookupSeriesTvdbIdAsync(seriesName, year, ct).ConfigureAwait(false);
                        if (!autoLookupTvdbId.HasValue)
                        {
                            Interlocked.Increment(ref unmatchedCount);
                            unmatchedSeries.Add(baseName);
                        }
                    }

                    string seriesFolderName = BuildSeriesFolderName(seriesName, year, tvdbOverrides, providerTmdbId, autoLookupTvdbId);

                    bool seriesHasNewEpisodes = false;

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
                                string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode);
                                string strmPath = Path.Combine(seasonFolder, episodeFileName);

                                syncedFiles.TryAdd(strmPath, 0);

                                if (File.Exists(strmPath))
                                {
                                    Interlocked.Increment(ref episodesSkipped);
                                    continue;
                                }

                                // Create season folder
                                Directory.CreateDirectory(seasonFolder);

                                // Build stream URL
                                string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                                string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

                                // Write STRM file
                                await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);
                                Interlocked.Increment(ref episodesCreated);
                                seasonHasNewEpisodes = true;
                                seriesHasNewEpisodes = true;

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

                                // Download episode thumbnail for unmatched series
                                if (!providerTmdbId.HasValue && !autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched)
                                {
                                    var episodeThumbUrl = episode.Info?.MovieImage;
                                    if (!string.IsNullOrEmpty(episodeThumbUrl))
                                    {
                                        var episodeThumbName = Path.GetFileNameWithoutExtension(episodeFileName);
                                        var thumbExt = GetImageExtension(episodeThumbUrl);
                                        var thumbPath = Path.Combine(seasonFolder, $"{episodeThumbName}-thumb{thumbExt}");
                                        await DownloadImageAsync(episodeThumbUrl, thumbPath, ct).ConfigureAwait(false);
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

                            // Download season poster for unmatched series
                            if (!autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched && isNewSeason && seasonHasNewEpisodes)
                            {
                                var season = seriesInfo.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
                                var seasonCoverUrl = season?.CoverBig ?? season?.Cover;
                                if (!string.IsNullOrEmpty(seasonCoverUrl))
                                {
                                    var seasonPosterExt = GetImageExtension(seasonCoverUrl);
                                    var seasonPosterPath = Path.Combine(seasonFolder, $"poster{seasonPosterExt}");
                                    await DownloadImageAsync(seasonCoverUrl, seasonPosterPath, ct).ConfigureAwait(false);
                                }
                            }
                        }

                        // Download artwork for unmatched series in this folder
                        if (!autoLookupTvdbId.HasValue && !tvdbOverrides.ContainsKey(baseName) && config.DownloadArtworkForUnmatched && seriesHasNewEpisodes && isNewSeries)
                        {
                            // Download series poster
                            if (!string.IsNullOrEmpty(series.Cover))
                            {
                                var posterExt = GetImageExtension(series.Cover);
                                var posterPath = Path.Combine(seriesFolderPath, $"poster{posterExt}");
                                await DownloadImageAsync(series.Cover, posterPath, ct).ConfigureAwait(false);
                            }

                            // Download series backdrop/fanart
                            if (series.BackdropPaths.Count > 0)
                            {
                                var backdropUrl = series.BackdropPaths.First();
                                var fanartExt = GetImageExtension(backdropUrl);
                                var fanartPath = Path.Combine(seriesFolderPath, $"fanart{fanartExt}");
                                await DownloadImageAsync(backdropUrl, fanartPath, ct).ConfigureAwait(false);
                            }
                        }
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
                    CurrentProgress.ItemsProcessed++;
                    CurrentProgress.EpisodesCreated = episodesCreated;
                }
            }).ConfigureAwait(false);

            // Update batch progress
            CurrentProgress.CategoriesProcessed = batchIndex + 1;

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
        result.EpisodesSkipped += episodesSkipped;
        result.Errors += errors;
        result.AddFailedItems(failedItems);

        if (smartSkipped > 0)
        {
            _logger.LogInformation("Smart-skipped {Count} series (already had STRM files)", smartSkipped);
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

        CurrentProgress.CategoriesProcessed = 1;
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

        string seriesName = SanitizeFileName(series.Name);
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
                    string episodeFileName = BuildEpisodeFileName(seriesName, seasonNumber, episode);
                    string strmPath = Path.Combine(seasonFolder, episodeFileName);

                    syncedFiles.TryAdd(strmPath, 0);

                    if (File.Exists(strmPath))
                    {
                        result.EpisodesSkipped++;
                        continue;
                    }

                    // Create season folder
                    Directory.CreateDirectory(seasonFolder);

                    // Build stream URL
                    string extension = string.IsNullOrEmpty(episode.ContainerExtension) ? "mkv" : episode.ContainerExtension;
                    string streamUrl = $"{connectionInfo.BaseUrl}/series/{connectionInfo.UserName}/{connectionInfo.Password}/{episode.EpisodeId}.{extension}";

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

    internal static string BuildEpisodeFileName(string seriesName, int seasonNumber, Episode episode)
    {
        string episodeTitle = SanitizeFileName(episode.Title);
        string seasonStr = seasonNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        string episodeStr = episode.EpisodeNum.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(episodeTitle) || episodeTitle.Equals($"Episode {episode.EpisodeNum}", StringComparison.OrdinalIgnoreCase))
        {
            return $"{seriesName} - S{seasonStr}E{episodeStr}.strm";
        }

        return $"{seriesName} - S{seasonStr}E{episodeStr} - {episodeTitle}.strm";
    }

    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }

        string cleanName = name;

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

    private static void CollectExistingStrmFiles(string basePath, HashSet<string> files)
    {
        if (!Directory.Exists(basePath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(basePath, "*.strm", SearchOption.AllDirectories))
        {
            files.Add(file);
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

    // Matches language phrases like "(NL GESPROKEN)", "(EN SPOKEN)", "(DUBBED)", "(OV)", "(SUB)", "[NL Gesproken]", "[NL Gepsroken]", etc.
    [GeneratedRegex(@"[\(\[]\s*(?:NL|EN|DE|FR|ES|IT|PT|RU|PL|JP|KR|CN)\s*(?:GESPROKEN|GEPSROKEN|SPOKEN|DUBBED|SUBS?|SUBBED|OV|OmU|AUDIO)?\s*[\)\]]", RegexOptions.IgnoreCase)]
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
}

/// <summary>
/// Real-time progress of a sync operation.
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Gets or sets a value indicating whether a sync is currently in progress.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the current phase of the sync.
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current item being processed.
    /// </summary>
    public string CurrentItem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total number of categories to process.
    /// </summary>
    public int TotalCategories { get; set; }

    /// <summary>
    /// Gets or sets the number of categories processed.
    /// </summary>
    public int CategoriesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total items in the current category.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the items processed in the current category.
    /// </summary>
    public int ItemsProcessed { get; set; }

    /// <summary>
    /// Gets or sets the number of movies created so far.
    /// </summary>
    public int MoviesCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes created so far.
    /// </summary>
    public int EpisodesCreated { get; set; }

    /// <summary>
    /// Gets or sets the start time.
    /// </summary>
    public DateTime? StartTime { get; set; }
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public class SyncResult
{
    private readonly List<FailedItem> _failedItems = new();

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
    /// Gets or sets the number of movies deleted (orphans).
    /// </summary>
    public int MoviesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of movies (created + skipped).
    /// </summary>
    public int TotalMovies => MoviesCreated + MoviesSkipped;

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
    /// Gets or sets the number of episodes deleted (orphans).
    /// </summary>
    public int EpisodesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of episodes (created + skipped).
    /// </summary>
    public int TotalEpisodes => EpisodesCreated + EpisodesSkipped;

    /// <summary>
    /// Gets or sets the number of files deleted (orphans) - legacy, use specific counts.
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the number of errors encountered.
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Gets the list of failed items.
    /// </summary>
    public IReadOnlyList<FailedItem> FailedItems => _failedItems;

    /// <summary>
    /// Gets the duration of the sync operation.
    /// </summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>
    /// Adds a failed item to the list.
    /// </summary>
    /// <param name="item">The failed item to add.</param>
    internal void AddFailedItem(FailedItem item) => _failedItems.Add(item);

    /// <summary>
    /// Adds multiple failed items to the list.
    /// </summary>
    /// <param name="items">The failed items to add.</param>
    internal void AddFailedItems(IEnumerable<FailedItem> items) => _failedItems.AddRange(items);

    /// <summary>
    /// Clears and replaces all failed items.
    /// </summary>
    /// <param name="items">The new list of failed items.</param>
    internal void SetFailedItems(IEnumerable<FailedItem> items)
    {
        _failedItems.Clear();
        _failedItems.AddRange(items);
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
