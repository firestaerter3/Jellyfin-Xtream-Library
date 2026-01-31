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
using System.IO;
using System.Linq;
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
    private readonly IXtreamClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StrmSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSyncService"/> class.
    /// </summary>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The logger instance.</param>
    public StrmSyncService(
        IXtreamClient client,
        ILibraryManager libraryManager,
        ILogger<StrmSyncService> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the last sync operation.
    /// </summary>
    public SyncResult? LastSyncResult { get; private set; }

    /// <summary>
    /// Performs a full sync of all content from the Xtream provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result with statistics.</returns>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var result = new SyncResult { StartTime = DateTime.UtcNow };

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            _logger.LogWarning("Xtream credentials not configured, skipping sync");
            result.Error = "Credentials not configured";
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

            var syncedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Sync Movies/VOD
            if (config.SyncMovies)
            {
                _logger.LogInformation("Syncing movies/VOD content...");
                await SyncMoviesAsync(connectionInfo, moviesPath, syncedFiles, result, cancellationToken).ConfigureAwait(false);
            }

            // Sync Series
            if (config.SyncSeries)
            {
                _logger.LogInformation("Syncing series content...");
                await SyncSeriesAsync(connectionInfo, seriesPath, syncedFiles, result, cancellationToken).ConfigureAwait(false);
            }

            // Cleanup orphaned files
            if (config.CleanupOrphans)
            {
                var orphanedFiles = existingStrmFiles.Except(syncedFiles, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var orphan in orphanedFiles)
                {
                    try
                    {
                        File.Delete(orphan);
                        result.FilesDeleted++;
                        _logger.LogDebug("Deleted orphaned file: {FilePath}", orphan);

                        // Try to clean up empty parent directories
                        CleanupEmptyDirectories(Path.GetDirectoryName(orphan)!, config.LibraryPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned file: {FilePath}", orphan);
                    }
                }

                if (orphanedFiles.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} orphaned STRM files", orphanedFiles.Count);
                }
            }

            result.EndTime = DateTime.UtcNow;
            result.Success = true;

            _logger.LogInformation(
                "Sync completed: {MoviesCreated} movies, {EpisodesCreated} episodes created; {MoviesSkipped} movies, {EpisodesSkipped} episodes skipped; {Deleted} orphans deleted",
                result.MoviesCreated,
                result.EpisodesCreated,
                result.MoviesSkipped,
                result.EpisodesSkipped,
                result.FilesDeleted);

            // Trigger library scan if enabled
            if (config.TriggerLibraryScan && (result.MoviesCreated > 0 || result.EpisodesCreated > 0 || result.FilesDeleted > 0))
            {
                _logger.LogInformation("Triggering Jellyfin library scan...");
                await TriggerLibraryScanAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed with error");
            result.Error = ex.Message;
            result.EndTime = DateTime.UtcNow;
        }

        LastSyncResult = result;
        return result;
    }

    private async Task SyncMoviesAsync(
        ConnectionInfo connectionInfo,
        string moviesPath,
        HashSet<string> syncedFiles,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var categories = await _client.GetVodCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        var processedStreamIds = new HashSet<int>();

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogDebug("Processing VOD category: {CategoryName} ({CategoryId})", category.CategoryName, category.CategoryId);

            var streams = await _client.GetVodStreamsByCategoryAsync(connectionInfo, category.CategoryId, cancellationToken).ConfigureAwait(false);

            foreach (var stream in streams)
            {
                // Skip duplicates (same movie can appear in multiple categories)
                if (!processedStreamIds.Add(stream.StreamId))
                {
                    continue;
                }

                try
                {
                    string movieName = SanitizeFileName(stream.Name);
                    int? year = ExtractYear(stream.Name);

                    string folderName = year.HasValue ? $"{movieName} ({year})" : movieName;
                    string movieFolder = Path.Combine(moviesPath, folderName);
                    string strmFileName = $"{folderName}.strm";
                    string strmPath = Path.Combine(movieFolder, strmFileName);

                    syncedFiles.Add(strmPath);

                    if (File.Exists(strmPath))
                    {
                        result.MoviesSkipped++;
                        continue;
                    }

                    // Create movie folder
                    Directory.CreateDirectory(movieFolder);

                    // Build stream URL
                    string extension = string.IsNullOrEmpty(stream.ContainerExtension) ? "mp4" : stream.ContainerExtension;
                    string streamUrl = $"{connectionInfo.BaseUrl}/movie/{connectionInfo.UserName}/{connectionInfo.Password}/{stream.StreamId}.{extension}";

                    // Write STRM file
                    await File.WriteAllTextAsync(strmPath, streamUrl, cancellationToken).ConfigureAwait(false);
                    result.MoviesCreated++;

                    _logger.LogDebug("Created movie STRM: {StrmPath}", strmPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create STRM for movie: {MovieName}", stream.Name);
                    result.Errors++;
                }
            }
        }
    }

    private async Task SyncSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        HashSet<string> syncedFiles,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        var categories = await _client.GetSeriesCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        var processedSeriesIds = new HashSet<int>();

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogDebug("Processing series category: {CategoryName} ({CategoryId})", category.CategoryName, category.CategoryId);

            var seriesList = await _client.GetSeriesByCategoryAsync(connectionInfo, category.CategoryId, cancellationToken).ConfigureAwait(false);

            foreach (var series in seriesList)
            {
                // Skip duplicates (same series can appear in multiple categories)
                if (!processedSeriesIds.Add(series.SeriesId))
                {
                    continue;
                }

                try
                {
                    await SyncSingleSeriesAsync(connectionInfo, seriesPath, series, syncedFiles, result, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync series: {SeriesName}", series.Name);
                    result.Errors++;
                }
            }
        }
    }

    private async Task SyncSingleSeriesAsync(
        ConnectionInfo connectionInfo,
        string seriesPath,
        Series series,
        HashSet<string> syncedFiles,
        SyncResult result,
        CancellationToken cancellationToken)
    {
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

                    syncedFiles.Add(strmPath);

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

    private static string BuildEpisodeFileName(string seriesName, int seasonNumber, Episode episode)
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

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }

        // Remove year from name if present (we'll add it back in folder name format)
        string cleanName = YearPattern().Replace(name, string.Empty).Trim();

        // Remove invalid file name characters
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char c in invalidChars)
        {
            cleanName = cleanName.Replace(c, '_');
        }

        // Remove consecutive underscores and trim
        cleanName = MultipleUnderscoresPattern().Replace(cleanName, "_").Trim('_', ' ');

        return string.IsNullOrEmpty(cleanName) ? "Unknown" : cleanName;
    }

    private static int? ExtractYear(string name)
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

    private static void CleanupEmptyDirectories(string directory, string stopAt)
    {
        while (!string.IsNullOrEmpty(directory) &&
               !directory.Equals(stopAt, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(directory))
        {
            if (Directory.GetFileSystemEntries(directory).Length == 0)
            {
                try
                {
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory)!;
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

    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"_+")]
    private static partial Regex MultipleUnderscoresPattern();
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public class SyncResult
{
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
    /// Gets or sets the number of episodes created.
    /// </summary>
    public int EpisodesCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes skipped (already existed).
    /// </summary>
    public int EpisodesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of files deleted (orphans).
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Gets or sets the number of errors encountered.
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Gets the duration of the sync operation.
    /// </summary>
    public TimeSpan Duration => EndTime - StartTime;
}
