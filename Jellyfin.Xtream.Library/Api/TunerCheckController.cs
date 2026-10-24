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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// Tells the settings page whether Jellyfin also has an M3U tuner reading this plugin's playlist.
/// With the native tuner on as well, every channel then appears twice (BUG-010, GitHub #50).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("XtreamLibrary")]
public class TunerCheckController : ControllerBase
{
    // Jellyfin's M3uTunerHost.Type, which plugins cannot reference.
    private const string M3uTunerType = "m3u";

    private readonly IServerConfigurationManager _serverConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerCheckController"/> class.
    /// </summary>
    /// <param name="serverConfig">The Jellyfin server configuration manager.</param>
    public TunerCheckController(IServerConfigurationManager serverConfig)
    {
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
    }

    /// <summary>
    /// Reports whether an M3U tuner under Tuner Devices points at this plugin's playlists.
    /// </summary>
    /// <returns>The check result.</returns>
    [HttpGet("LiveTv/TunerCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTunerCheck()
    {
        var tuners = _serverConfig.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
        return Ok(new { M3uTunerOnPluginPlaylist = HasM3uTunerOnPluginPlaylist(tuners) });
    }

    /// <summary>
    /// Whether any M3U tuner reads a playlist served by this plugin, at any host or port.
    /// </summary>
    /// <param name="tuners">The tuners listed under Tuner Devices.</param>
    /// <returns>True when such a tuner exists.</returns>
    internal static bool HasM3uTunerOnPluginPlaylist(IEnumerable<TunerHostInfo> tuners)
        => tuners.Any(t =>
            string.Equals(t.Type, M3uTunerType, StringComparison.OrdinalIgnoreCase)
            && t.Url is not null
            && t.Url.Contains("/XtreamLibrary/", StringComparison.OrdinalIgnoreCase)
            && t.Url.Contains(".m3u", StringComparison.OrdinalIgnoreCase));
}
