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
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="syncService">The STRM sync service.</param>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="metadataLookup">The metadata lookup service.</param>
    /// <param name="logger">The logger instance.</param>
    public SyncController(
        StrmSyncService syncService,
        IXtreamClient client,
        IMetadataLookupService metadataLookup,
        ILogger<SyncController> logger)
    {
        _syncService = syncService;
        _client = client;
        _metadataLookup = metadataLookup;
        _logger = logger;
    }

    /// <summary>
    /// Triggers a manual sync of Xtream content.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sync result.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<SyncResult>> TriggerSync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manual sync triggered via API");

        var result = await _syncService.SyncAsync(cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            return Ok(result);
        }

        return StatusCode(StatusCodes.Status500InternalServerError, result);
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
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(
        [FromBody] ConnectionTestRequest? request,
        CancellationToken cancellationToken)
    {
        var baseUrl = request?.BaseUrl ?? Plugin.Instance.Configuration.BaseUrl;
        var username = request?.Username ?? Plugin.Instance.Configuration.Username;
        var password = request?.Password ?? Plugin.Instance.Configuration.Password;

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
        var config = Plugin.Instance.Configuration;
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
        var config = Plugin.Instance.Configuration;
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
    /// </summary>
    /// <returns>Result with counts of deleted items.</returns>
    [HttpPost("CleanLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult CleanLibraries()
    {
        var config = Plugin.Instance.Configuration;
        if (string.IsNullOrEmpty(config.LibraryPath))
        {
            return BadRequest(new { Success = false, Message = "Library path not configured." });
        }

        var moviesPath = System.IO.Path.Combine(config.LibraryPath, "Movies");
        var seriesPath = System.IO.Path.Combine(config.LibraryPath, "Series");

        int moviesDeleted = 0;
        int seriesDeleted = 0;

        try
        {
            if (System.IO.Directory.Exists(moviesPath))
            {
                var movieDirs = System.IO.Directory.GetDirectories(moviesPath);
                moviesDeleted = movieDirs.Length;
                System.IO.Directory.Delete(moviesPath, recursive: true);
                System.IO.Directory.CreateDirectory(moviesPath);
                _logger.LogInformation("Deleted {Count} movie folders from {Path}", moviesDeleted, moviesPath);
            }

            if (System.IO.Directory.Exists(seriesPath))
            {
                var seriesDirs = System.IO.Directory.GetDirectories(seriesPath);
                seriesDeleted = seriesDirs.Length;
                System.IO.Directory.Delete(seriesPath, recursive: true);
                System.IO.Directory.CreateDirectory(seriesPath);
                _logger.LogInformation("Deleted {Count} series folders from {Path}", seriesDeleted, seriesPath);
            }

            return Ok(new
            {
                Success = true,
                Message = $"Deleted {moviesDeleted} movies and {seriesDeleted} series.",
                MoviesDeleted = moviesDeleted,
                SeriesDeleted = seriesDeleted,
            });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to clean libraries");
            return BadRequest(new { Success = false, Message = $"Failed to clean libraries: {ex.Message}" });
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
