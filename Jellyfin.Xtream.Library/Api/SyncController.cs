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
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
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
    private readonly IMetadataLookupService _metadataLookup;
    private readonly SnapshotService _snapshotService;
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="syncService">The STRM sync service.</param>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="snapshotService">The snapshot service.</param>
    /// <param name="logger">The logger instance.</param>
    public SyncController(
        StrmSyncService syncService,
        IXtreamClient client,
        IMetadataLookupService metadataLookup,
        SnapshotService snapshotService,
        ILogger<SyncController> logger)
    {
        _syncService = syncService;
        _client = client;
        _metadataLookup = metadataLookup;
        _snapshotService = snapshotService;
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
    /// Deletes all content from the Movies and Series library folders.
    /// Cancels any running sync first and waits for it to stop.
    /// </summary>
    /// <returns>Result with counts of deleted items.</returns>
    [HttpPost("CleanLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> CleanLibraries()
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

        var moviesPath = System.IO.Path.Combine(config.LibraryPath, "Movies");
        var seriesPath = System.IO.Path.Combine(config.LibraryPath, "Series");

        int moviesDeleted = 0;
        int seriesDeleted = 0;

        try
        {
            if (System.IO.Directory.Exists(moviesPath))
            {
                var movieStrms = System.IO.Directory.GetFiles(moviesPath, "*.strm", System.IO.SearchOption.AllDirectories);
                moviesDeleted = movieStrms.Length;
                ForceDeleteDirectoryContents(moviesPath);
                _logger.LogInformation("Deleted {Count} movies from {Path}", moviesDeleted, moviesPath);
            }

            if (System.IO.Directory.Exists(seriesPath))
            {
                var seriesStrms = System.IO.Directory.GetFiles(seriesPath, "*.strm", System.IO.SearchOption.AllDirectories);
                seriesDeleted = seriesStrms.Length;
                ForceDeleteDirectoryContents(seriesPath);
                _logger.LogInformation("Deleted {Count} episodes from {Path}", seriesDeleted, seriesPath);
            }

            // Delete snapshots so next sync starts fresh
            _snapshotService.ClearAllSnapshots();

            return Ok(new
            {
                Success = true,
                Message = $"Deleted {moviesDeleted} movies and {seriesDeleted} episodes. Snapshots cleared. Press Sync to repopulate.",
                MoviesDeleted = moviesDeleted,
                EpisodesDeleted = seriesDeleted,
            });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to clean libraries");
            return BadRequest(new { Success = false, Message = $"Failed to clean libraries: {ex.Message}" });
        }
    }

    /// <summary>
    /// Deletes all contents of a directory without deleting the directory itself.
    /// More reliable than Directory.Delete(recursive: true) on Linux/Docker where
    /// file watchers can hold handles and cause "Directory not empty" errors.
    /// </summary>
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
