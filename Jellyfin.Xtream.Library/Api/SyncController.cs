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
    private readonly ILogger<SyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncController"/> class.
    /// </summary>
    /// <param name="syncService">The STRM sync service.</param>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="logger">The logger instance.</param>
    public SyncController(
        StrmSyncService syncService,
        IXtreamClient client,
        ILogger<SyncController> logger)
    {
        _syncService = syncService;
        _client = client;
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
    /// Tests the connection to the Xtream provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test result.</returns>
    [HttpGet("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        if (string.IsNullOrEmpty(config.BaseUrl) || string.IsNullOrEmpty(config.Username))
        {
            return BadRequest(new ConnectionTestResult
            {
                Success = false,
                Message = "Credentials not configured",
            });
        }

        try
        {
            var connectionInfo = Plugin.Instance.Creds;
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
            return StatusCode(StatusCodes.Status500InternalServerError, new ConnectionTestResult
            {
                Success = false,
                Message = $"Connection failed: {ex.Message}",
            });
        }
    }
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
