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
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// API controller for Xtream Library sync operations.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("XtreamLibrary")]
[Produces(MediaTypeNames.Application.Json)]
public class SyncController : ControllerBase
{
    private readonly StrmSyncService _syncService;
    private readonly IXtreamClient _client;
    private readonly IDispatcharrClient _dispatcharrClient;
    private readonly IMetadataLookupService _metadataLookup;
    private readonly SnapshotService _snapshotService;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="syncService">The STRM sync service.</param>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="dispatcharrClient">The Dispatcharr REST API client.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="snapshotService">The snapshot service.</param>
    /// <param name="appPaths">The server application paths.</param>
    /// <param name="logger">The logger instance.</param>
    public SyncController(
        StrmSyncService syncService,
        IXtreamClient client,
        IDispatcharrClient dispatcharrClient,
        IMetadataLookupService metadataLookup,
        SnapshotService snapshotService,
        IServerApplicationPaths appPaths,
        ILogger<SyncController> logger)
    {
        _syncService = syncService;
        _client = client;
        _dispatcharrClient = dispatcharrClient;
        _metadataLookup = metadataLookup;
        _snapshotService = snapshotService;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Safely gets the plugin configuration, returning null if the plugin is not initialized.
    /// </summary>
    /// <returns>The plugin configuration, or null if not available.</returns>
    private PluginConfiguration? TryGetConfig()
    {
        try
        {
            return Plugin.Instance.Configuration;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Triggers a manual sync of Xtream content.
    /// </summary>
    /// <returns>Status indicating sync was started.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult TriggerSync()
    {
        // Check if sync is already running
        if (_syncService.CurrentProgress.IsRunning)
        {
            return Conflict(new { Success = false, Message = "A sync is already in progress." });
        }

        _logger.LogInformation("Manual sync triggered via API");

        // Clear any sync suppression (from CleanLibraries) — manual trigger always runs
        _syncService.ClearSuppression();

        // Start sync in background - NOT tied to HTTP request
        // Use CancellationToken.None so browser disconnect won't cancel the sync
        // The sync can still be cancelled via the Cancel endpoint
        _ = Task.Run(async () =>
        {
            try
            {
                await _syncService.SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background sync failed with unhandled exception");
            }
        });

        return Ok(new { Success = true, Message = "Sync started in background. Use /Status or /Progress to monitor." });
    }

    /// <summary>
    /// Cancels the currently running sync operation.
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpPost("Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CancelSync()
    {
        var cancelled = _syncService.CancelSync();
        if (cancelled)
        {
            _logger.LogInformation("Sync cancellation requested via API");
            return Ok(new { Success = true, Message = "Sync cancellation requested." });
        }

        return Ok(new { Success = false, Message = "No sync is currently running." });
    }

    /// <summary>
    /// Gets the status of the last sync operation.
    /// </summary>
    /// <returns>The last sync result, or null if no sync has been performed.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<SyncResult?> GetStatus()
    {
        var result = _syncService.LastSyncResult;
        if (result == null)
        {
            return NoContent();
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets the current sync progress.
    /// </summary>
    /// <returns>The current sync progress.</returns>
    [HttpGet("Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncProgress> GetProgress()
    {
        return Ok(_syncService.CurrentProgress);
    }

    /// <summary>
    /// Gets the list of failed items from the last sync.
    /// </summary>
    /// <returns>List of failed items.</returns>
    [HttpGet("FailedItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<FailedItem>> GetFailedItems()
    {
        return Ok(_syncService.FailedItems);
    }

    /// <summary>
    /// Retries syncing all failed items from the last sync.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The retry result.</returns>
    [HttpPost("RetryFailed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SyncResult>> RetryFailed(CancellationToken cancellationToken)
    {
        if (_syncService.FailedItems.Count == 0)
        {
            return BadRequest("No failed items to retry.");
        }

        _logger.LogInformation("Retry failed items triggered via API");

        var result = await _syncService.RetryFailedAsync(cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    /// Tests the connection to the Xtream provider.
    /// </summary>
    /// <param name="request">Optional connection test request with credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test result.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(
        [FromBody] ConnectionTestRequest? request,
        CancellationToken cancellationToken)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest(new ConnectionTestResult
            {
                Success = false,
                Message = "Plugin not initialized.",
            });
        }

        var baseUrl = request?.BaseUrl ?? config.BaseUrl;
        var username = request?.Username ?? config.Username;
        var password = request?.Password ?? config.Password;

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(username))
        {
            return Ok(new ConnectionTestResult
            {
                Success = false,
                Message = "Please enter Base URL and Username.",
            });
        }

        try
        {
            var connectionInfo = new Client.ConnectionInfo(baseUrl, username, password ?? string.Empty);
            var playerInfo = await _client.GetUserAndServerInfoAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            return Ok(new ConnectionTestResult
            {
                Success = true,
                Message = $"Connected successfully. User: {playerInfo.UserInfo.Username}, Status: {playerInfo.UserInfo.Status}",
                Username = playerInfo.UserInfo.Username,
                Status = playerInfo.UserInfo.Status,
                MaxConnections = playerInfo.UserInfo.MaxConnections,
                ActiveConnections = playerInfo.UserInfo.ActiveCons,
            });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Connection test failed");
            return Ok(new ConnectionTestResult
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// Tests the Dispatcharr REST API connection and JWT authentication.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test result.</returns>
    [HttpPost("TestDispatcharr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestDispatcharr(CancellationToken cancellationToken)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest(new { Success = false, Message = "Plugin not initialized." });
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            return Ok(new { Success = false, Message = "Please configure Base URL first." });
        }

        if (string.IsNullOrEmpty(config.DispatcharrApiUser))
        {
            return Ok(new { Success = false, Message = "Please enter Dispatcharr API credentials." });
        }

        try
        {
            _dispatcharrClient.Configure(config.DispatcharrApiUser, config.DispatcharrApiPass);
            var success = await _dispatcharrClient.TestConnectionAsync(config.BaseUrl, cancellationToken).ConfigureAwait(false);

            if (success)
            {
                return Ok(new { Success = true, Message = "Dispatcharr REST API connected successfully." });
            }

            return Ok(new { Success = false, Message = "Dispatcharr authentication failed. Check API credentials." });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Dispatcharr connection test failed");
            return Ok(new { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets all VOD categories from the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of VOD categories.</returns>
    [HttpGet("Categories/Vod")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<CategoryDto>>> GetVodCategories(CancellationToken cancellationToken)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest("Plugin not initialized.");
        }

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = Plugin.Instance.Creds;
            var categories = await _client.GetVodCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            var result = categories.Select(c => new CategoryDto
            {
                CategoryId = c.CategoryId,
                CategoryName = c.CategoryName,
            }).OrderBy(c => c.CategoryName);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch VOD categories");
            return BadRequest($"Failed to fetch categories: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all Series categories from the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of Series categories.</returns>
    [HttpGet("Categories/Series")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<CategoryDto>>> GetSeriesCategories(CancellationToken cancellationToken)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest("Plugin not initialized.");
        }

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = Plugin.Instance.Creds;
            var categories = await _client.GetSeriesCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            var result = categories.Select(c => new CategoryDto
            {
                CategoryId = c.CategoryId,
                CategoryName = c.CategoryName,
            }).OrderBy(c => c.CategoryName);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Series categories");
            return BadRequest($"Failed to fetch categories: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the metadata lookup cache.
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpPost("ClearMetadataCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearMetadataCache()
    {
        _logger.LogInformation("Clearing metadata cache via API");
        await _metadataLookup.ClearCacheAsync().ConfigureAwait(false);
        return Ok(new { Success = true, Message = "Metadata cache cleared." });
    }

    /// <summary>
    /// Gets the dashboard data including last sync, progress, history, schedule, and library stats.
    /// </summary>
    /// <returns>Dashboard data.</returns>
    [HttpGet("Dashboard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetDashboard()
    {
        var config = TryGetConfig();
        var lastSync = _syncService.LastSyncResult;
        var progress = _syncService.CurrentProgress;
        var history = _syncService.SyncHistory;

        // Calculate next sync time
        DateTime? nextSyncTime = null;
        string nextSyncDisplay = "Unknown";
        string scheduleType = config?.SyncScheduleType ?? "Interval";

        if (config != null)
        {
            if (string.Equals(scheduleType, "Daily", StringComparison.Ordinal))
            {
                var now = DateTime.Now;
                var todaySync = new DateTime(now.Year, now.Month, now.Day, config.SyncDailyHour, config.SyncDailyMinute, 0);
                nextSyncTime = now < todaySync ? todaySync : todaySync.AddDays(1);
            }
            else if (lastSync?.EndTime != null && lastSync.EndTime != default)
            {
                nextSyncTime = lastSync.EndTime.ToLocalTime().AddMinutes(config.SyncIntervalMinutes);
            }

            if (nextSyncTime.HasValue)
            {
                var diff = nextSyncTime.Value - DateTime.Now;
                if (diff.TotalSeconds > 0)
                {
                    if (diff.TotalHours >= 1)
                    {
                        nextSyncDisplay = $"In {(int)diff.TotalHours}h {diff.Minutes}m";
                    }
                    else
                    {
                        nextSyncDisplay = $"In {diff.Minutes}m";
                    }
                }
                else
                {
                    nextSyncDisplay = "Imminent";
                }
            }
        }

        // Calculate library stats from filesystem
        var libraryStats = GetLibraryStats(config?.LibraryPath);

        return Ok(new
        {
            LastSync = lastSync,
            Progress = progress,
            History = history,
            NextSyncTime = nextSyncTime,
            NextSyncDisplay = nextSyncDisplay,
            ScheduleType = scheduleType,
            LibraryStats = libraryStats,
        });
    }

    /// <summary>
    /// Gets the sync history (last 10 results).
    /// </summary>
    /// <returns>List of sync results.</returns>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<SyncResult>> GetHistory()
    {
        return Ok(_syncService.SyncHistory);
    }

    /// <summary>
    /// Deletes all content from the Movies library folder.
    /// Cancels any running sync first and waits for it to stop.
    /// </summary>
    /// <returns>Result with count of deleted items.</returns>
    [HttpPost("CleanMovies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CleanMovies()
    {
        return await CleanLibraryFolder("Movies").ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all content from the Series library folder.
    /// Cancels any running sync first and waits for it to stop.
    /// </summary>
    /// <returns>Result with count of deleted items.</returns>
    [HttpPost("CleanSeries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CleanSeries()
    {
        return await CleanLibraryFolder("Series").ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads plugin log entries with sensitive values redacted.
    /// </summary>
    /// <returns>A text file containing filtered and redacted log lines.</returns>
    [HttpGet("DownloadLog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [Produces("text/plain")]
    public ActionResult DownloadLog()
    {
        try
        {
            var logDir = _appPaths.LogDirectoryPath;
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, "Log directory not found.");
            }

            var logFiles = Directory.GetFiles(logDir, "log_*.log");
            if (logFiles.Length == 0)
            {
                return File(Encoding.UTF8.GetBytes("No log files found."), "text/plain", "xtream-library-log.txt");
            }

            var config = TryGetConfig();
            var sb = new StringBuilder();

            foreach (var logFile in logFiles.OrderBy(f => f))
            {
                foreach (var line in System.IO.File.ReadLines(logFile))
                {
                    if (line.Contains("Jellyfin.Xtream.Library", StringComparison.Ordinal))
                    {
                        sb.AppendLine(RedactLogLine(line, config));
                    }
                }
            }

            if (sb.Length == 0)
            {
                sb.AppendLine("No Jellyfin.Xtream.Library log entries found.");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain", "xtream-library-log.txt");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate log download");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to read log files.");
        }
    }

    private static string RedactLogLine(string line, PluginConfiguration? config)
    {
        var result = line;

        // Redact known config values
        if (config != null)
        {
            if (!string.IsNullOrEmpty(config.BaseUrl))
            {
                result = result.Replace(config.BaseUrl, "[REDACTED_URL]", StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(config.Username))
            {
                result = result.Replace(config.Username, "[REDACTED_USER]", StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(config.Password))
            {
                result = result.Replace(config.Password, "[REDACTED_PASS]", StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(config.DispatcharrApiUser))
            {
                result = result.Replace(config.DispatcharrApiUser, "[REDACTED_USER]", StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(config.DispatcharrApiPass))
            {
                result = result.Replace(config.DispatcharrApiPass, "[REDACTED_PASS]", StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(config.UserAgent))
            {
                result = result.Replace(config.UserAgent, "[REDACTED_UA]", StringComparison.Ordinal);
            }
        }

        // Safety-net regex: URLs with embedded credentials
        result = Regex.Replace(result, @"://[^@]+@", "://[REDACTED]@");

        // Safety-net regex: IP addresses (with optional port)
        result = Regex.Replace(result, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:\d+)?\b", "[REDACTED_IP]");

        return result;
    }

    private async Task<ActionResult> CleanLibraryFolder(string folderName)
    {
        var config = TryGetConfig();
        if (config == null)
        {
            return BadRequest(new { Success = false, Message = "Plugin not initialized." });
        }

        if (string.IsNullOrEmpty(config.LibraryPath))
        {
            return BadRequest(new { Success = false, Message = "Library path not configured." });
        }

        // Suppress all syncs until user manually presses Sync
        _syncService.SuppressSync();

        // Cancel any running sync and wait for it to stop
        bool wasCancelled = _syncService.CancelSync();
        if (wasCancelled)
        {
            _logger.LogInformation("Waiting for running sync to stop before cleaning...");
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (_syncService.CurrentProgress.IsRunning && DateTime.UtcNow < timeout)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }

            if (_syncService.CurrentProgress.IsRunning)
            {
                _logger.LogWarning("Sync did not stop within timeout, proceeding with clean anyway");
            }
            else
            {
                _logger.LogInformation("Sync stopped, proceeding with clean");
            }
        }

        var folderPath = System.IO.Path.Combine(config.LibraryPath, folderName);

        try
        {
            int deleted = 0;
            if (System.IO.Directory.Exists(folderPath))
            {
                var strms = System.IO.Directory.GetFiles(folderPath, "*.strm", System.IO.SearchOption.AllDirectories);
                deleted = strms.Length;
                ForceDeleteDirectoryContents(folderPath);
                _logger.LogInformation("Deleted {Count} items from {Path}", deleted, folderPath);
            }

            // Delete snapshots so next sync starts fresh
            _snapshotService.ClearAllSnapshots();

            var label = string.Equals(folderName, "Movies", System.StringComparison.Ordinal) ? "movies" : "episodes";
            return Ok(new
            {
                Success = true,
                Message = $"Deleted {deleted} {label}. Snapshots cleared. Press Sync to repopulate.",
                Deleted = deleted,
            });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to clean {Folder} library", folderName);
            return BadRequest(new { Success = false, Message = $"Failed to clean {folderName} library: {ex.Message}" });
        }
    }

    /// <summary>
    /// Deletes all contents of a directory without deleting the directory itself.
    /// More reliable than Directory.Delete(recursive: true) on Linux/Docker where
    /// file watchers can hold handles and cause "Directory not empty" errors.
    /// </summary>
    private static LibraryStatsDto GetLibraryStats(string? libraryPath)
    {
        var stats = new LibraryStatsDto();
        if (string.IsNullOrEmpty(libraryPath))
        {
            return stats;
        }

        CountFolders(Path.Combine(libraryPath, "Movies"), out int movieTotal, out int movieMatched);
        stats.TotalMovieFolders = movieTotal;
        stats.MatchedMovies = movieMatched;
        stats.UnmatchedMovies = movieTotal - movieMatched;

        CountFolders(Path.Combine(libraryPath, "Series"), out int seriesTotal, out int seriesMatched);
        stats.TotalSeriesFolders = seriesTotal;
        stats.MatchedSeries = seriesMatched;
        stats.UnmatchedSeries = seriesTotal - seriesMatched;

        return stats;
    }

    private static void CountFolders(string path, out int total, out int matched)
    {
        total = 0;
        matched = 0;
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var name = Path.GetFileName(dir);
            if (IsContentFolder(name))
            {
                total++;
                if (name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("[tvdbid-", StringComparison.OrdinalIgnoreCase))
                {
                    matched++;
                }
            }
            else
            {
                // Group folder (e.g., "Kids", "Generic") — count its children
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    total++;
                    var subName = Path.GetFileName(subDir);
                    if (subName.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("[tvdbid-", StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                    }
                }
            }
        }
    }

    private static bool IsContentFolder(string name)
    {
        // Content folders contain a year in parentheses, e.g., "Movie Name (2024)" or metadata IDs
        return name.Contains("[tmdbid-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("[tvdbid-", StringComparison.OrdinalIgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(name, @"\(\d{4}\)");
    }

    private static void ForceDeleteDirectoryContents(string directoryPath)
    {
        var di = new System.IO.DirectoryInfo(directoryPath);

        foreach (var file in di.EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                file.Delete();
            }
            catch (System.IO.IOException)
            {
                // Retry once after a brief delay (file watcher may release handle)
                System.Threading.Thread.Sleep(50);
                file.Delete();
            }
        }

        // Delete subdirectories bottom-up (deepest first)
        foreach (var dir in di.EnumerateDirectories("*", System.IO.SearchOption.AllDirectories)
            .OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                if (dir.Exists && dir.GetFileSystemInfos().Length == 0)
                {
                    dir.Delete();
                }
            }
            catch (System.IO.IOException)
            {
                // Best effort - directory may still have locked files
            }
        }
    }
}

/// <summary>
/// Request model for connection test with credentials.
/// </summary>
public class ConnectionTestRequest
{
    /// <summary>
    /// Gets or sets the base URL to test.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the username to test.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password to test.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>
/// Result of a connection test.
/// </summary>
public class ConnectionTestResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the connection test was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the result message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username from the provider.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the account status.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the maximum allowed connections.
    /// </summary>
    public int? MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets the current active connections.
    /// </summary>
    public int? ActiveConnections { get; set; }
}

/// <summary>
/// Data transfer object for category information.
/// </summary>
public class CategoryDto
{
    /// <summary>
    /// Gets or sets the category ID.
    /// </summary>
    public int CategoryId { get; set; }

    /// <summary>
    /// Gets or sets the category name.
    /// </summary>
    public string CategoryName { get; set; } = string.Empty;
}

/// <summary>
/// Library statistics showing matched vs unmatched content.
/// </summary>
public class LibraryStatsDto
{
    /// <summary>
    /// Gets or sets the total number of movie folders.
    /// </summary>
    public int TotalMovieFolders { get; set; }

    /// <summary>
    /// Gets or sets the number of movies matched to TMDb/TVDb.
    /// </summary>
    public int MatchedMovies { get; set; }

    /// <summary>
    /// Gets or sets the number of unmatched movies.
    /// </summary>
    public int UnmatchedMovies { get; set; }

    /// <summary>
    /// Gets or sets the total number of series folders.
    /// </summary>
    public int TotalSeriesFolders { get; set; }

    /// <summary>
    /// Gets or sets the number of series matched to TMDb/TVDb.
    /// </summary>
    public int MatchedSeries { get; set; }

    /// <summary>
    /// Gets or sets the number of unmatched series.
    /// </summary>
    public int UnmatchedSeries { get; set; }
}
