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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// API controller for Live TV M3U and EPG endpoints.
/// </summary>
[ApiController]
[Route("XtreamLibrary")]
public class LiveTvController : ControllerBase
{
    private readonly LiveTvService _liveTvService;
    private readonly IXtreamClient _client;
    private readonly ILogger<LiveTvController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvController"/> class.
    /// </summary>
    /// <param name="liveTvService">The Live TV service.</param>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="logger">The logger instance.</param>
    public LiveTvController(
        LiveTvService liveTvService,
        IXtreamClient client,
        ILogger<LiveTvController> logger)
    {
        _liveTvService = liveTvService;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Gets the M3U playlist for Live TV.
    /// This endpoint does not require authentication as Jellyfin's tuner needs direct access.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The M3U playlist.</returns>
    [HttpGet("LiveTv.m3u")]
    [AllowAnonymous]
    [Produces("audio/x-mpegurl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetM3UPlaylist(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        if (!config.EnableLiveTv)
        {
            return BadRequest(new { Error = "Live TV is not enabled in plugin settings." });
        }

        if (!HasProviderCredentials(config))
        {
            return BadRequest(new { Error = "Provider credentials not configured." });
        }

        try
        {
            var m3u = await _liveTvService.GetM3UPlaylistAsync(cancellationToken).ConfigureAwait(false);
            return Content(m3u, "audio/x-mpegurl");
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to generate M3U playlist");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Failed to generate M3U playlist." });
        }
    }

    /// <summary>
    /// Gets the XMLTV EPG for Live TV.
    /// This endpoint does not require authentication as Jellyfin's guide data provider needs direct access.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The XMLTV EPG.</returns>
    [HttpGet("Epg.xml")]
    [AllowAnonymous]
    [Produces("application/xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetEpgXml(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        if (!config.EnableLiveTv)
        {
            return BadRequest(new { Error = "Live TV is not enabled in plugin settings." });
        }

        if (!config.EnableEpg)
        {
            return BadRequest(new { Error = "EPG is not enabled in plugin settings." });
        }

        if (!HasProviderCredentials(config))
        {
            return BadRequest(new { Error = "Provider credentials not configured." });
        }

        try
        {
            var epg = await _liveTvService.GetXmltvEpgAsync(cancellationToken).ConfigureAwait(false);
            return Content(epg, "application/xml");
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to generate XMLTV EPG");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Failed to generate EPG." });
        }
    }

    /// <summary>
    /// Gets the M3U playlist for catch-up enabled channels only.
    /// This endpoint does not require authentication as Jellyfin's tuner needs direct access.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catch-up M3U playlist.</returns>
    [HttpGet("Catchup.m3u")]
    [AllowAnonymous]
    [Produces("audio/x-mpegurl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetCatchupM3UPlaylist(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        if (!config.EnableLiveTv)
        {
            return BadRequest(new { Error = "Live TV is not enabled in plugin settings." });
        }

        if (!config.EnableCatchup)
        {
            return BadRequest(new { Error = "Catch-up is not enabled in plugin settings." });
        }

        if (!HasProviderCredentials(config))
        {
            return BadRequest(new { Error = "Provider credentials not configured." });
        }

        try
        {
            var m3u = await _liveTvService.GetCatchupM3UPlaylistAsync(cancellationToken).ConfigureAwait(false);
            return Content(m3u, "audio/x-mpegurl");
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Catchup M3U playlist");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Failed to generate Catchup M3U playlist." });
        }
    }

    /// <summary>
    /// Gets all Live TV categories from the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of Live TV categories.</returns>
    [HttpGet("Categories/Live")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<CategoryDto>>> GetLiveCategories(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        if (!HasProviderCredentials(config))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = Plugin.Instance.GetCreds(0);
            var categories = await _client.GetLiveCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);

            var result = categories.Select(c => new CategoryDto
            {
                CategoryId = c.CategoryId,
                CategoryName = c.CategoryName,
            }).OrderBy(c => c.CategoryName);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Live TV categories");
            return BadRequest($"Failed to fetch categories: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all Live TV channels for a given category.
    /// </summary>
    /// <param name="categoryId">The Live TV category ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of channels in the category.</returns>
    [HttpGet("Channels/Live")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<LiveChannelDto>>> GetLiveChannels(
        [FromQuery] int categoryId,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        if (!HasProviderCredentials(config))
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var connectionInfo = Plugin.Instance.GetCreds(0);
            var channels = await _client.GetLiveStreamsByCategoryAsync(connectionInfo, categoryId, cancellationToken).ConfigureAwait(false);

            var result = channels.Select(c => new LiveChannelDto
            {
                StreamId = c.StreamId,
                Name = c.Name,
                Num = c.Num,
                EpgChannelId = c.EpgChannelId,
                StreamIcon = c.StreamIcon,
            }).OrderBy(c => c.Num).ThenBy(c => c.Name);

            return Ok(result);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Live TV channels for category {CategoryId}", categoryId);
            return BadRequest($"Failed to fetch channels: {ex.Message}");
        }
    }

    /// <summary>
    /// Invalidates the Live TV cache (M3U and EPG).
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpPost("LiveTv/RefreshCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RefreshCache()
    {
        _liveTvService.InvalidateCache();
        _logger.LogInformation("Live TV cache refreshed via API");
        return Ok(new { Success = true, Message = "Live TV cache invalidated." });
    }

    /// <summary>
    /// Serves the local logo file configured for a channel via Channel Overrides (issue #53).
    /// Only paths that an administrator configured server-side are served; the request carries
    /// only the stream ID, so there is no path-traversal surface.
    /// </summary>
    /// <param name="streamId">The channel stream ID.</param>
    /// <returns>The image file, or 404 if there is no local-path override logo for the channel.</returns>
    [HttpGet("ChannelLogo/{streamId:int}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetChannelLogo([FromRoute] int streamId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        var overrides = ChannelOverrideParser.Parse(config.ChannelOverrides);
        if (!overrides.TryGetValue(streamId, out var channelOverride)
            || string.IsNullOrEmpty(channelOverride.LogoUrl)
            || !ChannelLogoResolver.IsLocalPath(channelOverride.LogoUrl))
        {
            return NotFound();
        }

        var path = channelOverride.LogoUrl;

        // file:// scheme support — convert to a filesystem path.
        if (path.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase)
            && System.Uri.TryCreate(path, System.UriKind.Absolute, out var fileUri))
        {
            path = fileUri.LocalPath;
        }

        if (!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path))
        {
            _logger.LogWarning("Channel logo file not found for stream {StreamId}: {Path}", streamId, path);
            return NotFound();
        }

        var contentType = GetImageContentType(path);
        return PhysicalFile(path, contentType);
    }

    private static string GetImageContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    private static bool HasProviderCredentials(PluginConfiguration config)
    {
        var provider = config.Providers.FirstOrDefault();
        return provider is not null
            && !string.IsNullOrEmpty(provider.BaseUrl)
            && !string.IsNullOrEmpty(provider.Username);
    }
}

/// <summary>
/// Data transfer object for a Live TV channel.
/// </summary>
public class LiveChannelDto
{
    /// <summary>Gets or sets the upstream stream ID.</summary>
    public int StreamId { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel number from the provider.</summary>
    public int Num { get; set; }

    /// <summary>Gets or sets the EPG channel identifier.</summary>
    public string EpgChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel logo URL.</summary>
    public string StreamIcon { get; set; } = string.Empty;
}
